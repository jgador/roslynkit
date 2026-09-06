using System.Runtime.ExceptionServices;

namespace RoslynKit;

/// <summary>
/// Retains a repository workspace and publishes immutable revisions behind refresh and reader-lifetime barriers.
/// </summary>
internal sealed class RepositoryWorkspaceSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string _repositoryRoot;
    private readonly string? _targetPath;
    private readonly Func<CancellationToken, Task<RoslynWorkspaceLoader>> _load;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _prepare;
    private readonly TimeSpan _changeDebounce;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, WatcherChangeTypes> _pendingChanges = new(WorkspaceInputManifest.PathComparer);
    private readonly HashSet<string> _excludedOutputs = new(WorkspaceInputManifest.PathComparer);
    private readonly RepositoryFileWatcher? _watcher;
    private readonly Task _reconciliationLoop;
    private SnapshotState? _current;
    private Task? _refreshTask;
    private ExceptionDispatchInfo? _failure;
    private bool _reconcileRequested;
    private bool _started;
    private bool _disposed;
    private bool _retiring;
    private int _activeReaders;
    private long _revision;

    public RepositoryWorkspaceSession(string repositoryRoot, string? targetPath, bool automaticRestore = true)
        : this(
            repositoryRoot,
            targetPath,
            token => RoslynWorkspaceLoader.LoadAsync(targetPath ?? repositoryRoot, filePath: null, token),
            async token =>
            {
                var preparation = await WorkspacePreparation.PrepareAsync(repositoryRoot, targetPath, automaticRestore, token).ConfigureAwait(false);
                RoslynWorkspaceLoader.RegisterMSBuild(preparation.Sdk.SdkDirectory);
                return preparation.BuildInputPaths.Concat(preparation.AssetsPaths).Concat(preparation.ProjectPaths)
                    .Distinct(WorkspaceInputManifest.PathComparer).ToArray();
            })
    {
    }

    internal RepositoryWorkspaceSession(
        string repositoryRoot,
        string? targetPath,
        Func<CancellationToken, Task<RoslynWorkspaceLoader>> load,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? prepare = null,
        TimeSpan? reconciliationInterval = null,
        TimeSpan? changeDebounce = null,
        bool watchFiles = true)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _targetPath = targetPath is null ? null : Path.GetFullPath(targetPath, _repositoryRoot);
        _load = load;
        _prepare = prepare ?? (_ => Task.FromResult<IReadOnlyList<string>>([]));
        _changeDebounce = changeDebounce ?? TimeSpan.FromMilliseconds(100);
        if (watchFiles)
        {
            _watcher = new RepositoryFileWatcher(_repositoryRoot, NotifyFileChanged, NotifyWatcherError);
        }

        _reconciliationLoop = ReconcilePeriodicallyAsync(reconciliationInterval ?? TimeSpan.FromSeconds(30));
    }

    public event Action? Changed;

    public void ExcludeOutputPath(string path)
    {
        var fullPath = Path.GetFullPath(path, _repositoryRoot);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _retiring, this);
            if (_current?.Manifest.ExplicitInputs.Contains(fullPath, WorkspaceInputManifest.PathComparer) == true)
            {
                throw new InvalidOperationException("The search index output path is also an evaluated workspace input. Select a different index path.");
            }

            var added = false;
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                added |= _excludedOutputs.Add(fullPath + suffix);
            }

            if (added && _started)
            {
                _reconcileRequested = true;
                StartRefreshUnderLock();
            }
        }
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _refreshTask is not null;
            }
        }
    }

    public bool TryBeginRetirement()
    {
        lock (_gate)
        {
            if (_disposed || _retiring || _refreshTask is not null || _activeReaders != 0)
            {
                return false;
            }

            _retiring = true;
            return true;
        }
    }

    /// <summary>
    /// Acquires a fixed solution after any already-known input refresh, without scanning files on a warm query.
    /// </summary>
    public async Task<WorkspaceSnapshotLease> AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? pending;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed || _retiring, this);
                if (!_started)
                {
                    _reconcileRequested = true;
                    StartRefreshUnderLock();
                }

                pending = _refreshTask;
                if (pending is null)
                {
                    _failure?.Throw();
                    var current = _current ?? throw new InvalidOperationException("The repository workspace has no published revision.");
                    current.Owner.AddReference();
                    _activeReaders++;
                    return new WorkspaceSnapshotLease(current.Snapshot, current.Revision, current.Manifest, () => ReleaseLease(current.Owner));
                }
            }

            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies complete input contents and membership before returning the resulting workspace revision.
    /// </summary>
    public async Task<long> SynchronizeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _retiring, this);
            _reconcileRequested = true;
            StartRefreshUnderLock();
        }

        using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        return lease.Revision;
    }

    public bool IsCurrent(long revision)
    {
        lock (_gate)
        {
            return !_disposed && !_retiring && _failure is null && _refreshTask is null && _current?.Revision == revision;
        }
    }

    /// <summary>
    /// Rechecks disk inputs at an index publication boundary and schedules reconciliation for a stale captured revision.
    /// </summary>
    public async Task<bool> ValidateSnapshotAsync(WorkspaceSnapshotLease lease, CancellationToken cancellationToken)
    {
        if (!IsCurrent(lease.Revision))
        {
            return false;
        }

        var observed = await lease.Manifest.ReconcileAsync(cancellationToken, GetExcludedOutputs()).ConfigureAwait(false);
        if (observed.Fingerprint != lease.Fingerprint)
        {
            NotifyWatcherError();
            return false;
        }

        lock (_gate)
        {
            return !_disposed && !_retiring && _failure is null && _refreshTask is null
                && _current?.Revision == lease.Revision && ReferenceEquals(_current.Manifest, lease.Manifest);
        }
    }

    internal void NotifyFileChanged(string path, WatcherChangeTypes changeType = WatcherChangeTypes.Changed)
    {
        var fullPath = Path.GetFullPath(path);
        lock (_gate)
        {
            if (_disposed || _retiring || _excludedOutputs.Contains(fullPath) || !(_current?.Manifest.IsCandidate(fullPath)
                    ?? WorkspaceInputManifest.IsCandidateRootInput(_repositoryRoot, fullPath)))
            {
                return;
            }

            _pendingChanges.TryGetValue(fullPath, out var previous);
            _pendingChanges[fullPath] = previous | changeType;
            if (_started)
            {
                StartRefreshUnderLock();
            }
        }
    }

    internal void NotifyWatcherError()
    {
        lock (_gate)
        {
            if (_disposed || _retiring)
            {
                return;
            }

            _reconcileRequested = true;
            if (_started)
            {
                StartRefreshUnderLock();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? refresh;
        SnapshotState? current;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            refresh = _refreshTask;
            current = _current;
            _current = null;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _watcher?.Dispose();
        if (refresh is not null)
        {
            await refresh.ConfigureAwait(false);
        }

        current?.Owner.Release();
        await _reconciliationLoop.ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private void StartRefreshUnderLock()
    {
        _started = true;
        _refreshTask ??= Task.Run(ProcessUpdatesAsync);
    }

    private async Task ProcessUpdatesAsync()
    {
        try
        {
            while (true)
            {
                if (_changeDebounce > TimeSpan.Zero)
                {
                    await Task.Delay(_changeDebounce, _lifetime.Token).ConfigureAwait(false);
                }

                SnapshotState? previous;
                Dictionary<string, WatcherChangeTypes> changes;
                bool reconcile;
                bool retryFailure;
                lock (_gate)
                {
                    previous = _current;
                    changes = new Dictionary<string, WatcherChangeTypes>(_pendingChanges, WorkspaceInputManifest.PathComparer);
                    _pendingChanges.Clear();
                    reconcile = _reconcileRequested;
                    _reconcileRequested = false;
                    retryFailure = _failure is not null;
                }

                var replacement = await RefreshAsync(previous, changes, reconcile, retryFailure, _lifetime.Token).ConfigureAwait(false);
                var finished = false;
                SnapshotState? retired = null;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        replacement?.Owner.Release();
                        return;
                    }

                    if (replacement is not null)
                    {
                        retired = _current;
                        _current = replacement with { Revision = ++_revision };
                    }

                    _failure = null;
                }

                retired?.Owner.Release();
                if (replacement is not null)
                {
                    RaiseChanged();
                }

                lock (_gate)
                {
                    if (!_reconcileRequested && _pendingChanges.Count == 0)
                    {
                        _refreshTask = null;
                        finished = true;
                    }
                }

                if (finished)
                {
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _failure = ExceptionDispatchInfo.Capture(exception);
                }

                _refreshTask = null;
                if (!_disposed && (_pendingChanges.Count > 0 || _reconcileRequested))
                {
                    StartRefreshUnderLock();
                }
            }
        }
    }

    private async Task<SnapshotState?> RefreshAsync(
        SnapshotState? previous,
        IReadOnlyDictionary<string, WatcherChangeTypes> changes,
        bool reconcile,
        bool retryFailure,
        CancellationToken cancellationToken)
    {
        if (previous is null || retryFailure)
        {
            return await LoadReplacementAsync(cancellationToken).ConfigureAwait(false);
        }

        var sourcesOnly = changes.All(change => change.Value == WatcherChangeTypes.Changed && previous.Manifest.IsSource(change.Key));
        var manifest = reconcile || !sourcesOnly
            ? await previous.Manifest.ReconcileAsync(cancellationToken, GetExcludedOutputs()).ConfigureAwait(false)
            : await previous.Manifest.ReadChangedSourcesAsync(changes.Keys, cancellationToken).ConfigureAwait(false);
        if (manifest.Fingerprint == previous.Manifest.Fingerprint)
        {
            return null;
        }

        if (manifest.RequiresReplacement(previous.Manifest))
        {
            return await LoadReplacementAsync(cancellationToken).ConfigureAwait(false);
        }

        var snapshot = previous.Snapshot.WithSolution(manifest.FreezeText(previous.Snapshot.Solution));
        previous.Owner.AddReference();
        return new SnapshotState(previous.Owner, snapshot, manifest, 0);
    }

    private async Task<SnapshotState> LoadReplacementAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var additionalInputs = await _prepare(cancellationToken).ConfigureAwait(false);
            var excludedOutputs = GetExcludedOutputs();
            var before = await WorkspaceInputManifest.CaptureBeforeLoadAsync(
                _repositoryRoot, _targetPath, additionalInputs, cancellationToken, excludedOutputs).ConfigureAwait(false);
            var loaded = await _load(cancellationToken).ConfigureAwait(false);
            try
            {
                WorkspaceSupportValidator.ValidateLoaded(loaded.Solution);
                var manifest = await WorkspaceInputManifest.CaptureAsync(loaded, additionalInputs, cancellationToken, excludedOutputs).ConfigureAwait(false);
                if (!before.MatchesKnownInputs(manifest))
                {
                    loaded.Dispose();
                    continue;
                }

                var pinnedSolution = await manifest.FreezeMetadataAsync(manifest.FreezeText(loaded.Solution), cancellationToken).ConfigureAwait(false);
                if (pinnedSolution is null)
                {
                    loaded.Dispose();
                    continue;
                }

                var snapshot = loaded.WithSolution(pinnedSolution);
                foreach (var project in snapshot.Solution.Projects)
                {
                    _ = await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false);
                }

                _watcher?.UpdateInputs(manifest);
                return new SnapshotState(new WorkspaceOwner(loaded), snapshot, manifest, 0);
            }
            catch
            {
                loaded.Dispose();
                throw;
            }
        }

        throw new InvalidOperationException("Repository inputs changed repeatedly while loading the workspace. Retry after file changes settle.");
    }

    private async Task ReconcilePeriodicallyAsync(TimeSpan interval)
    {
        if (interval == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                NotifyWatcherError();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private IReadOnlySet<string> GetExcludedOutputs()
    {
        lock (_gate)
        {
            return new HashSet<string>(_excludedOutputs, WorkspaceInputManifest.PathComparer);
        }
    }

    private void ReleaseLease(WorkspaceOwner owner)
    {
        owner.Release();
        lock (_gate)
        {
            _activeReaders--;
        }
    }

    private void RaiseChanged()
    {
        if (Changed is not { } changed)
        {
            return;
        }

        foreach (var handler in changed.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch
            {
                // Consumer failures must not invalidate an already-published workspace revision.
            }
        }
    }

    private sealed record SnapshotState(WorkspaceOwner Owner, RoslynWorkspaceLoader Snapshot, WorkspaceInputManifest Manifest, long Revision);

    /// <summary>
    /// Releases one materialized workspace only after both the session and every captured reader retire it.
    /// </summary>
    private sealed class WorkspaceOwner(RoslynWorkspaceLoader loader)
    {
        private int _references = 1;

        internal void AddReference() => Interlocked.Increment(ref _references);

        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
            {
                loader.Dispose();
            }
        }
    }
}
