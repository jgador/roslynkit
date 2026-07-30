namespace RoslynKit;

/// <summary>
/// Describes the workspace availability owned by a daemon session.
/// </summary>
internal enum WorkspaceDaemonSessionState
{
    NotLoaded,
    Reloading,
    Ready,
    Disposed,
}

/// <summary>
/// Captures one atomic view of workspace generation and request state for daemon status reporting.
/// </summary>
internal sealed record WorkspaceDaemonSessionSnapshot(
    WorkspaceDaemonSessionState State,
    long? Generation,
    int ActiveRequests,
    int QueuedRequests,
    string? LastInfrastructureDiagnostic);

/// <summary>
/// Owns one loaded workspace generation and executes commands without reloading it.
/// </summary>
internal sealed class WorkspaceDaemonGeneration : IDisposable
{
    private readonly IDisposable _owner;
    private readonly Func<ParsedCommand, CancellationToken, Task<CliProcessResult>> _executeAsync;
    private int _disposed;

    internal WorkspaceDaemonGeneration(
        IDisposable owner,
        Func<ParsedCommand, CancellationToken, Task<CliProcessResult>> executeAsync)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(executeAsync);

        _owner = owner;
        _executeAsync = executeAsync;
    }

    private WorkspaceDaemonGeneration(RoslynWorkspaceLoader loadedWorkspace)
    {
        LoadedWorkspace = loadedWorkspace;
        _owner = loadedWorkspace;
        _executeAsync = async (command, cancellationToken) =>
        {
            var data = await RoslynCommandExecutor.ExecuteAsync(
                command,
                loadedWorkspace,
                cancellationToken).ConfigureAwait(false);
            return CliProcessResult.Success(MarkdownProjection.Render(data));
        };
    }

    internal RoslynWorkspaceLoader? LoadedWorkspace { get; }

    internal static async Task<WorkspaceDaemonGeneration> LoadAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        var loaded = await RoslynWorkspaceLoader.LoadAsync(targetPath, cancellationToken).ConfigureAwait(false);
        return new WorkspaceDaemonGeneration(loaded);
    }

    internal Task<CliProcessResult> ExecuteAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _executeAsync(command, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.Dispose();
        }
    }
}

/// <summary>
/// Carries either a completed command response or a Git infrastructure failure that requires standalone fallback.
/// </summary>
internal sealed record WorkspaceDaemonSessionResult(
    CliProcessResult? ProcessResult,
    long? Generation,
    GitWorktreeFingerprintFailureKind? InfrastructureFailureKind,
    string? Diagnostic)
{
    public bool IsSuccessful => ProcessResult is not null;

    public static WorkspaceDaemonSessionResult Successful(CliProcessResult processResult, long generation)
    {
        return new WorkspaceDaemonSessionResult(processResult, generation, null, null);
    }

    public static WorkspaceDaemonSessionResult InfrastructureFailure(
        GitWorktreeFingerprintFailureKind failureKind,
        string diagnostic)
    {
        return new WorkspaceDaemonSessionResult(null, null, failureKind, diagnostic);
    }
}

/// <summary>
/// Reconciles Git state, owns immutable workspace generations, and leases clean snapshots to daemon requests.
/// </summary>
internal sealed class WorkspaceDaemonSession : IAsyncDisposable
{
    private const int MaximumConcurrentRequests = 3;
    private static readonly TimeSpan ReloadRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly object _stateGate = new();
    private readonly Func<CancellationToken, Task<GitWorktreeFingerprintResolution>> _captureFingerprintAsync;
    private readonly Func<CancellationToken, Task<WorkspaceDaemonGeneration>> _loadGenerationAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private TaskCompletionSource _stateChanged = CreateSignal();
    private TaskCompletionSource? _reloadCompleted;
    private WorkspaceDaemonGeneration? _currentGeneration;
    private GitWorktreeFingerprint? _successfulFingerprint;
    private WorkspaceDaemonSessionState _state;
    private string? _lastInfrastructureDiagnostic;
    private long _nextGeneration;
    private long? _generation;
    private int _activeRequests;
    private int _queuedRequests;
    private bool _reloadPending;
    private bool _forceReload;
    private bool _disposeRequested;
    private bool _disposed;

    internal WorkspaceDaemonSession(string targetPath, string worktreeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeRoot);

        TargetPath = PathCanonicalizer.ResolveExistingPath(targetPath);
        var fingerprintService = new GitWorktreeFingerprintService(worktreeRoot);
        _captureFingerprintAsync = fingerprintService.CaptureAsync;
        _loadGenerationAsync = cancellationToken => WorkspaceDaemonGeneration.LoadAsync(TargetPath, cancellationToken);
        _delayAsync = static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);
    }

    internal WorkspaceDaemonSession(
        string targetPath,
        Func<CancellationToken, Task<GitWorktreeFingerprintResolution>> captureFingerprintAsync,
        Func<CancellationToken, Task<WorkspaceDaemonGeneration>> loadGenerationAsync,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(captureFingerprintAsync);
        ArgumentNullException.ThrowIfNull(loadGenerationAsync);
        ArgumentNullException.ThrowIfNull(delayAsync);

        TargetPath = targetPath;
        _captureFingerprintAsync = captureFingerprintAsync;
        _loadGenerationAsync = loadGenerationAsync;
        _delayAsync = delayAsync;
    }

    public string TargetPath { get; }

    public WorkspaceDaemonSessionState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public long? Generation
    {
        get
        {
            lock (_stateGate)
            {
                return _generation;
            }
        }
    }

    public int ActiveRequests
    {
        get
        {
            lock (_stateGate)
            {
                return _activeRequests;
            }
        }
    }

    public int QueuedRequests
    {
        get
        {
            lock (_stateGate)
            {
                return _queuedRequests;
            }
        }
    }

    public string? LastInfrastructureDiagnostic
    {
        get
        {
            lock (_stateGate)
            {
                return _lastInfrastructureDiagnostic;
            }
        }
    }

    /// <summary>
    /// Captures workspace state under the session coordination lock so status responses cannot tear across fields.
    /// </summary>
    public WorkspaceDaemonSessionSnapshot CaptureSnapshot()
    {
        lock (_stateGate)
        {
            return new WorkspaceDaemonSessionSnapshot(
                _state,
                _generation,
                _activeRequests,
                _queuedRequests,
                _lastInfrastructureDiagnostic);
        }
    }

    internal GitWorktreeFingerprint? SuccessfulFingerprint
    {
        get
        {
            lock (_stateGate)
            {
                return _successfulFingerprint;
            }
        }
    }

    /// <summary>
    /// Reconciles the worktree once at request start and executes against a leased immutable generation.
    /// </summary>
    public async Task<WorkspaceDaemonSessionResult> ExecuteAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        RegisterQueuedRequest();
        var requestRegistered = true;

        try
        {
            var fingerprintResolution = await _captureFingerprintAsync(cancellationToken).ConfigureAwait(false);
            if (!fingerprintResolution.IsSuccessful)
            {
                requestRegistered = false;
                return RecordFingerprintFailure(fingerprintResolution);
            }

            var fingerprint = fingerprintResolution.Fingerprint!;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                WorkspaceDaemonGeneration? generation = null;
                long generationNumber = 0;
                Task? waitTask = null;
                var recaptureAfterWait = false;
                var ownsReload = false;

                lock (_stateGate)
                {
                    ThrowIfUnavailable();

                    if (_reloadPending)
                    {
                        waitTask = _reloadCompleted!.Task;
                        recaptureAfterWait = true;
                    }
                    else if (CanReuse(fingerprint))
                    {
                        if (_activeRequests < MaximumConcurrentRequests)
                        {
                            generation = _currentGeneration!;
                            generationNumber = _generation!.Value;
                            _lastInfrastructureDiagnostic = null;
                            MoveQueuedRequestToActive();
                            requestRegistered = false;
                        }
                        else
                        {
                            waitTask = _stateChanged.Task;
                            recaptureAfterWait = true;
                        }
                    }
                    else
                    {
                        BeginReload();
                        ownsReload = true;
                    }
                }

                if (generation is not null)
                {
                    return await ExecuteGenerationAsync(
                        generation,
                        generationNumber,
                        command,
                        cancellationToken).ConfigureAwait(false);
                }

                if (ownsReload)
                {
                    var acquisition = await ReloadAsync(fingerprint, cancellationToken).ConfigureAwait(false);
                    requestRegistered = false;
                    if (acquisition.Failure is not null)
                    {
                        return acquisition.Failure;
                    }

                    return await ExecuteGenerationAsync(
                        acquisition.Generation!,
                        acquisition.GenerationNumber,
                        command,
                        cancellationToken).ConfigureAwait(false);
                }

                await waitTask!.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (recaptureAfterWait)
                {
                    fingerprintResolution = await _captureFingerprintAsync(cancellationToken).ConfigureAwait(false);
                    if (!fingerprintResolution.IsSuccessful)
                    {
                        requestRegistered = false;
                        return RecordFingerprintFailure(fingerprintResolution);
                    }

                    fingerprint = fingerprintResolution.Fingerprint!;
                }
            }
        }
        finally
        {
            if (requestRegistered)
            {
                UnregisterQueuedRequest();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        WorkspaceDaemonGeneration? generation;

        while (true)
        {
            Task? waitTask;
            lock (_stateGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposeRequested = true;
                SignalStateChanged();
                if (_activeRequests == 0 && !_reloadPending)
                {
                    generation = _currentGeneration;
                    _currentGeneration = null;
                    _successfulFingerprint = null;
                    _generation = null;
                    _forceReload = false;
                    _disposed = true;
                    _state = WorkspaceDaemonSessionState.Disposed;
                    SignalStateChanged();
                    break;
                }

                waitTask = _stateChanged.Task;
            }

            await waitTask.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        generation?.Dispose();
    }

    private async Task<ReloadAcquisition> ReloadAsync(
        GitWorktreeFingerprint preLoadFingerprint,
        CancellationToken cancellationToken)
    {
        WorkspaceDaemonGeneration? candidate = null;
        var previousGenerationDetached = false;

        try
        {
            var previous = await DetachGenerationAfterActiveRequestsAsync(cancellationToken).ConfigureAwait(false);
            previousGenerationDetached = true;
            previous?.Dispose();

            candidate = await _loadGenerationAsync(cancellationToken).ConfigureAwait(false);
            var postLoad = await _captureFingerprintAsync(cancellationToken).ConfigureAwait(false);
            if (!postLoad.IsSuccessful)
            {
                candidate.Dispose();
                candidate = null;
                return CompleteReloadFailure(postLoad);
            }

            if (preLoadFingerprint.Equals(postLoad.Fingerprint))
            {
                var stableCandidate = candidate;
                candidate = null;
                return CompleteReloadOrRejectDisposal(
                    stableCandidate,
                    postLoad.Fingerprint,
                    forceReload: false);
            }

            candidate.Dispose();
            candidate = null;
            var quietFingerprint = postLoad.Fingerprint!;
            GitWorktreeFingerprintResolution retryPreLoad;
            while (true)
            {
                await _delayAsync(ReloadRetryDelay, cancellationToken).ConfigureAwait(false);
                retryPreLoad = await _captureFingerprintAsync(cancellationToken).ConfigureAwait(false);
                if (!retryPreLoad.IsSuccessful)
                {
                    return CompleteReloadFailure(retryPreLoad);
                }

                if (quietFingerprint.Equals(retryPreLoad.Fingerprint))
                {
                    break;
                }

                quietFingerprint = retryPreLoad.Fingerprint!;
            }

            candidate = await _loadGenerationAsync(cancellationToken).ConfigureAwait(false);
            var retryPostLoad = await _captureFingerprintAsync(cancellationToken).ConfigureAwait(false);
            if (!retryPostLoad.IsSuccessful)
            {
                candidate.Dispose();
                candidate = null;
                return CompleteReloadFailure(retryPostLoad);
            }

            var retryIsStable = retryPreLoad.Fingerprint!.Equals(retryPostLoad.Fingerprint);
            var retryCandidate = candidate;
            candidate = null;
            return CompleteReloadOrRejectDisposal(
                retryCandidate,
                retryIsStable ? retryPostLoad.Fingerprint : null,
                forceReload: !retryIsStable);
        }
        catch
        {
            candidate?.Dispose();
            AbortReload(previousGenerationDetached);
            throw;
        }
    }

    private async Task<WorkspaceDaemonGeneration?> DetachGenerationAfterActiveRequestsAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? waitTask;
            lock (_stateGate)
            {
                if (_disposeRequested)
                {
                    throw new ObjectDisposedException(nameof(WorkspaceDaemonSession));
                }

                if (_activeRequests == 0)
                {
                    var previous = _currentGeneration;
                    _currentGeneration = null;
                    _successfulFingerprint = null;
                    _generation = null;
                    _forceReload = false;
                    return previous;
                }

                waitTask = _stateChanged.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private ReloadAcquisition CompleteReload(
        WorkspaceDaemonGeneration generation,
        GitWorktreeFingerprint? successfulFingerprint,
        bool forceReload)
    {
        lock (_stateGate)
        {
            _currentGeneration = generation;
            _successfulFingerprint = successfulFingerprint;
            _forceReload = forceReload;
            _generation = ++_nextGeneration;
            _lastInfrastructureDiagnostic = null;
            _state = WorkspaceDaemonSessionState.Ready;
            MoveQueuedRequestToActive();
            CompleteReloadSignal();
            return new ReloadAcquisition(generation, _generation.Value, null);
        }
    }

    private ReloadAcquisition CompleteReloadOrRejectDisposal(
        WorkspaceDaemonGeneration generation,
        GitWorktreeFingerprint? successfulFingerprint,
        bool forceReload)
    {
        lock (_stateGate)
        {
            if (!_disposeRequested)
            {
                return CompleteReload(generation, successfulFingerprint, forceReload);
            }

            _currentGeneration = null;
            _successfulFingerprint = null;
            _generation = null;
            _forceReload = false;
            _state = WorkspaceDaemonSessionState.NotLoaded;
        }

        try
        {
            generation.Dispose();
        }
        finally
        {
            lock (_stateGate)
            {
                if (_reloadPending)
                {
                    CompleteReloadSignal();
                }
            }
        }

        throw new ObjectDisposedException(nameof(WorkspaceDaemonSession));
    }

    private ReloadAcquisition CompleteReloadFailure(GitWorktreeFingerprintResolution resolution)
    {
        lock (_stateGate)
        {
            _currentGeneration = null;
            _successfulFingerprint = null;
            _generation = null;
            _forceReload = false;
            _lastInfrastructureDiagnostic = resolution.Diagnostic;
            _state = WorkspaceDaemonSessionState.NotLoaded;
            _queuedRequests--;
            CompleteReloadSignal();
            return new ReloadAcquisition(
                null,
                0,
                WorkspaceDaemonSessionResult.InfrastructureFailure(
                    resolution.FailureKind!.Value,
                    resolution.Diagnostic!));
        }
    }

    private void AbortReload(bool previousGenerationDetached)
    {
        lock (_stateGate)
        {
            if (!_reloadPending)
            {
                return;
            }

            if (previousGenerationDetached)
            {
                _currentGeneration = null;
                _successfulFingerprint = null;
                _generation = null;
                _forceReload = false;
                _state = WorkspaceDaemonSessionState.NotLoaded;
            }
            else
            {
                _forceReload = true;
                _state = _currentGeneration is null
                    ? WorkspaceDaemonSessionState.NotLoaded
                    : WorkspaceDaemonSessionState.Ready;
            }

            CompleteReloadSignal();
        }
    }

    private async Task<WorkspaceDaemonSessionResult> ExecuteGenerationAsync(
        WorkspaceDaemonGeneration generation,
        long generationNumber,
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var processResult = await generation.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            return WorkspaceDaemonSessionResult.Successful(processResult, generationNumber);
        }
        finally
        {
            lock (_stateGate)
            {
                _activeRequests--;
                SignalStateChanged();
            }
        }
    }

    private WorkspaceDaemonSessionResult RecordFingerprintFailure(
        GitWorktreeFingerprintResolution resolution)
    {
        lock (_stateGate)
        {
            _lastInfrastructureDiagnostic = resolution.Diagnostic;
            _queuedRequests--;
            SignalStateChanged();
            return WorkspaceDaemonSessionResult.InfrastructureFailure(
                resolution.FailureKind!.Value,
                resolution.Diagnostic!);
        }
    }

    private void RegisterQueuedRequest()
    {
        lock (_stateGate)
        {
            ThrowIfUnavailable();
            _queuedRequests++;
            SignalStateChanged();
        }
    }

    private void UnregisterQueuedRequest()
    {
        lock (_stateGate)
        {
            _queuedRequests--;
            SignalStateChanged();
        }
    }

    private bool CanReuse(GitWorktreeFingerprint fingerprint)
    {
        return _currentGeneration is not null
            && !_forceReload
            && _successfulFingerprint is not null
            && _successfulFingerprint.Equals(fingerprint);
    }

    private void BeginReload()
    {
        _reloadPending = true;
        _reloadCompleted = CreateSignal();
        _state = WorkspaceDaemonSessionState.Reloading;
        SignalStateChanged();
    }

    private void CompleteReloadSignal()
    {
        var completed = _reloadCompleted;
        _reloadCompleted = null;
        _reloadPending = false;
        SignalStateChanged();
        completed!.TrySetResult();
    }

    private void MoveQueuedRequestToActive()
    {
        _queuedRequests--;
        _activeRequests++;
        SignalStateChanged();
    }

    private void SignalStateChanged()
    {
        var completed = _stateChanged;
        _stateChanged = CreateSignal();
        completed.TrySetResult();
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed || _disposeRequested, this);
    }

    private static TaskCompletionSource CreateSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record ReloadAcquisition(
        WorkspaceDaemonGeneration? Generation,
        long GenerationNumber,
        WorkspaceDaemonSessionResult? Failure);
}
