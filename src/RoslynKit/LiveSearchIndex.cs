namespace RoslynKit;

/// <summary>
/// Builds search partitions from retained workspace snapshots and coalesces background refreshes per index path.
/// </summary>
internal sealed class LiveSearchIndex : IAsyncDisposable
{
    private static readonly string CorpusBuildIdentity = typeof(RoslynSearchCorpusBuilder).Module.ModuleVersionId.ToString("N");

    private readonly RepositoryWorkspaceSession _session;
    private readonly string _repositoryRoot;
    private readonly string _targetPath;
    private readonly Func<CancellationToken, Task>? _beforeRefreshCompletion;
    private readonly Func<CancellationToken, Task>? _beforeObservingRefreshSuccessor;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, RefreshState> _states = new(PathComparer);
    private bool _disposed;

    public LiveSearchIndex(
        RepositoryWorkspaceSession session,
        string repositoryRoot,
        string? targetPath,
        Func<CancellationToken, Task>? beforeRefreshCompletion = null,
        Func<CancellationToken, Task>? beforeObservingRefreshSuccessor = null)
    {
        _session = session;
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _targetPath = Path.GetFullPath(targetPath ?? repositoryRoot);
        _beforeRefreshCompletion = beforeRefreshCompletion;
        _beforeObservingRefreshSuccessor = beforeObservingRefreshSuccessor;
        _session.ExcludeOutputPath(Path.Combine(_repositoryRoot, ".roslynkit", ".gitignore"));
        _session.Changed += OnWorkspaceChanged;
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _states.Values.Any(state => state.Work is not null);
            }
        }
    }

    /// <summary>
    /// Waits for the current known workspace revision and reads matching metadata and results atomically.
    /// </summary>
    public async Task<SearchResult> SearchAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        command = BindCommand(command);
        if (command.Flag("text-only"))
        {
            return await SearchCommandService.SearchAsync(command, cancellationToken).ConfigureAwait(false);
        }

        var state = GetState(command);
        var context = await state.Context.WaitAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var snapshot = await _session.AcquireAsync(cancellationToken).ConfigureAwait(false);
            await RequestRefresh(state, forceRebuild: false).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!_session.IsCurrent(snapshot.Revision))
            {
                continue;
            }

            try
            {
                var result = await SearchCommandService.QueryAsync(
                    command, context, Fingerprint(context, snapshot), snapshot.Snapshot.Solution,
                    snapshot.Snapshot.WorkspaceDiagnostics, cancellationToken).ConfigureAwait(false);
                if (_session.IsCurrent(snapshot.Revision))
                {
                    return result;
                }
            }
            catch (SearchIndexRevisionChangedException)
            {
                // A different process published this partition after the readiness check.
            }
        }
    }

    /// <summary>
    /// Explicitly prepares a partition using the same snapshot and publication boundary as background indexing.
    /// </summary>
    public async Task<IndexResult> IndexAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        command = BindCommand(command);
        if (command.Flag("text-only"))
        {
            return await SearchCommandService.IndexAsync(command, cancellationToken).ConfigureAwait(false);
        }

        var state = GetState(command);
        var context = await state.Context.WaitAsync(cancellationToken).ConfigureAwait(false);
        var forceRebuild = command.Flag("rebuild");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var snapshot = await _session.AcquireAsync(cancellationToken).ConfigureAwait(false);
            await RequestRefresh(state, forceRebuild).WaitAsync(cancellationToken).ConfigureAwait(false);
            forceRebuild = false;
            var metadata = await context.Index.ReadMetadataAsync(context.TargetIdentity, cancellationToken).ConfigureAwait(false);
            if (_session.IsCurrent(snapshot.Revision)
                && string.Equals(metadata?.Fingerprint, Fingerprint(context, snapshot), StringComparison.Ordinal))
            {
                return new IndexResult(
                    context.Path.TargetPath, context.Path.DatabasePath, SearchIndexState.Fresh,
                    metadata!.SymbolCount, command.Flag("rebuild"), snapshot.Snapshot.WorkspaceDiagnostics,
                    RepositoryScope: Directory.Exists(context.Path.TargetPath));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session.Changed -= OnWorkspaceChanged;
            _lifetime.Cancel();
            pending = _states.Values.SelectMany(state => new[] { state.Context, state.Work?.Completion })
                .OfType<Task>().ToArray();
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Request callers observe refresh failures; shutdown must still release every snapshot and writer.
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private ParsedCommand BindCommand(ParsedCommand command)
    {
        var requestedTarget = Path.GetFullPath(command.Optional("target") ?? _targetPath, _repositoryRoot);
        if (!PathComparer.Equals(requestedTarget, _targetPath))
        {
            throw new CliUsageException(command.Name, "The search target does not match this repository workspace session.");
        }

        var options = new Dictionary<string, string>(command.Options, StringComparer.Ordinal)
        {
            ["target"] = _targetPath,
        };
        if (command.Optional("index-path") is { } indexPath)
        {
            options["index-path"] = Path.GetFullPath(indexPath, _repositoryRoot);
        }

        return command with { Options = options };
    }

    private RefreshState GetState(ParsedCommand command)
    {
        var key = command.Optional("index-path") ?? Path.Combine(_repositoryRoot, ".roslynkit", "roslynkit.db");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_states.TryGetValue(key, out var state))
            {
                _session.ExcludeOutputPath(key);
                state = new RefreshState(SearchCommandService.ResolveContextAsync(command, _lifetime.Token));
                _states.Add(key, state);
            }

            return state;
        }
    }

    private void OnWorkspaceChanged()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_states.Count == 0)
            {
                GetState(BindCommand(CliParser.Parse(["index"])));
            }

            foreach (var state in _states.Values)
            {
                state.Dirty = true;
                StartRefreshLocked(state);
            }
        }
    }

    private Task RequestRefresh(RefreshState state, bool forceRebuild)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (forceRebuild)
            {
                state.ForceRebuild = true;
                state.Dirty = true;
            }

            return AwaitRefreshCompletionAsync(StartRefreshLocked(state));
        }
    }

    private async Task AwaitRefreshCompletionAsync(RefreshWork work)
    {
        while (true)
        {
            await work.Completion.ConfigureAwait(false);
            if (_beforeObservingRefreshSuccessor is not null)
            {
                await _beforeObservingRefreshSuccessor(_lifetime.Token).ConfigureAwait(false);
            }

            lock (_gate)
            {
                if (work.Successor is null)
                {
                    return;
                }

                work = work.Successor;
            }
        }
    }

    private RefreshWork StartRefreshLocked(RefreshState state)
    {
        if (state.Work is null)
        {
            var work = new RefreshWork();
            state.Work = work;
            work.Completion = Task.Run(() => RefreshUntilCurrentAsync(state, work), CancellationToken.None);
            _ = work.Completion.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return state.Work;
    }

    private async Task RefreshUntilCurrentAsync(RefreshState state, RefreshWork work)
    {
        var cancellationToken = _lifetime.Token;
        try
        {
            var context = await state.Context.ConfigureAwait(false);
            while (true)
            {
                bool forceRebuild;
                lock (_gate)
                {
                    state.Dirty = false;
                    forceRebuild = state.ForceRebuild;
                    state.ForceRebuild = false;
                }

                using (var snapshot = await _session.AcquireAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!await RefreshSnapshotAsync(context, snapshot, forceRebuild, cancellationToken).ConfigureAwait(false))
                    {
                        lock (_gate)
                        {
                            state.ForceRebuild |= forceRebuild;
                        }

                        continue;
                    }
                }

                bool finished;
                lock (_gate)
                {
                    finished = !state.Dirty;
                }

                if (finished)
                {
                    if (_beforeRefreshCompletion is not null)
                    {
                        await _beforeRefreshCompletion(cancellationToken).ConfigureAwait(false);
                    }

                    return;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                state.Work = null;
                if (!_disposed && state.Dirty)
                {
                    work.Successor = StartRefreshLocked(state);
                }
            }
        }
    }

    private async Task<bool> RefreshSnapshotAsync(
        SearchCommandService.SearchCommandContext context,
        WorkspaceSnapshotLease snapshot,
        bool forceRebuild,
        CancellationToken cancellationToken)
    {
        var fingerprint = Fingerprint(context, snapshot);
        var metadata = await context.Index.ReadMetadataAsync(context.TargetIdentity, cancellationToken).ConfigureAwait(false);
        if (!forceRebuild && string.Equals(metadata?.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return _session.IsCurrent(snapshot.Revision);
        }

        await using var writer = await SearchCommandService.WaitForWriterLeaseAsync(context.Index, cancellationToken).ConfigureAwait(false);
        if (!await _session.ValidateSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        metadata = await writer.ReadMetadataAsync(context.TargetIdentity, cancellationToken).ConfigureAwait(false);
        if (!forceRebuild && string.Equals(metadata?.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return _session.IsCurrent(snapshot.Revision);
        }

        SearchCommandService.ValidateSingleTargetFrameworkProjects("index", snapshot.Snapshot.Solution);
        var corpus = await SearchCommandService.BuildCorpusAsync(
            "index", context, snapshot.Snapshot.Solution, projectSelector: null, cancellationToken).ConfigureAwait(false);
        await writer.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(context.TargetIdentity, fingerprint),
            corpus.Records.Select(record => record.ToSqliteSymbol()).ToArray(), cancellationToken).ConfigureAwait(false);
        if (!await _session.ValidateSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
        return _session.IsCurrent(snapshot.Revision);
    }

    private static string Fingerprint(SearchCommandService.SearchCommandContext context, WorkspaceSnapshotLease snapshot)
    {
        return $"search-v{SqliteSearchIndex.SchemaVersion}|{CorpusBuildIdentity}|{context.TargetIdentity.Value}|{snapshot.Fingerprint}";
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// Tracks index readiness without retaining query results or separate semantic state.
    /// </summary>
    private sealed class RefreshState(Task<SearchCommandService.SearchCommandContext> context)
    {
        public Task<SearchCommandService.SearchCommandContext> Context { get; } = context;
        public RefreshWork? Work { get; set; }
        public bool Dirty { get; set; }
        public bool ForceRebuild { get; set; }
    }

    /// <summary>
    /// Keeps the exact successor completion observable after a later pump finishes or fails.
    /// </summary>
    private sealed class RefreshWork
    {
        public Task Completion { get; set; } = Task.CompletedTask;
        public RefreshWork? Successor { get; set; }
    }
}
