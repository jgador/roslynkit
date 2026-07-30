namespace RoslynKit;

internal enum WorkspaceDaemonHostState
{
    Running,
    Stopping,
    Stopped,
}

internal enum WorkspaceDaemonStopReason
{
    IdleTimeout,
    StopRequested,
    Disposed,
}

/// <summary>
/// Captures transport-independent daemon lifecycle and workspace status.
/// </summary>
internal sealed record WorkspaceDaemonHostSnapshot(
    WorkspaceDaemonHostState State,
    string TargetPath,
    int ProcessId,
    WorkspaceDaemonSessionState WorkspaceState,
    long? Generation,
    int ActiveRequests,
    int QueuedRequests,
    string? Diagnostic);

/// <summary>
/// Coordinates request cancellation, idle shutdown, graceful stop, and status around one workspace session.
/// </summary>
internal sealed class WorkspaceDaemonHost : IAsyncDisposable
{
    private const int MaximumDiagnosticLength = 4096;
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultStopGracePeriod = TimeSpan.FromSeconds(30);

    private readonly object _stateGate = new();
    private readonly int _processId;
    private readonly Func<ParsedCommand, CancellationToken, Task<WorkspaceDaemonSessionResult>> _executeAsync;
    private readonly Func<WorkspaceDaemonSessionSnapshot> _captureSessionSnapshot;
    private readonly Func<ValueTask> _disposeSessionAsync;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _stopGracePeriod;
    private readonly Dictionary<Guid, TrackedRequest> _activeRequests = [];
    private readonly TaskCompletionSource<WorkspaceDaemonStopReason> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _forceStop = CreateSignal();
    private TaskCompletionSource _requestsDrained = CreateCompletedSignal();
    private CancellationTokenSource? _idleCancellation;
    private Task? _stopTask;
    private WorkspaceDaemonHostState _state = WorkspaceDaemonHostState.Running;
    private long _activityVersion;

    internal WorkspaceDaemonHost(
        WorkspaceDaemonSession session,
        TimeProvider? timeProvider = null)
        : this(
            session.TargetPath,
            Environment.ProcessId,
            session.ExecuteAsync,
            session.CaptureSnapshot,
            session.DisposeAsync,
            timeProvider ?? TimeProvider.System,
            CreateDelay(timeProvider ?? TimeProvider.System),
            DefaultIdleTimeout,
            DefaultStopGracePeriod)
    {
    }

    internal WorkspaceDaemonHost(
        string targetPath,
        int processId,
        Func<ParsedCommand, CancellationToken, Task<WorkspaceDaemonSessionResult>> executeAsync,
        Func<WorkspaceDaemonSessionSnapshot> captureSessionSnapshot,
        Func<ValueTask> disposeSessionAsync,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        TimeSpan idleTimeout,
        TimeSpan stopGracePeriod)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentNullException.ThrowIfNull(executeAsync);
        ArgumentNullException.ThrowIfNull(captureSessionSnapshot);
        ArgumentNullException.ThrowIfNull(disposeSessionAsync);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(delayAsync);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stopGracePeriod, TimeSpan.Zero);

        TargetPath = targetPath;
        _processId = processId;
        _executeAsync = executeAsync;
        _captureSessionSnapshot = captureSessionSnapshot;
        _disposeSessionAsync = disposeSessionAsync;
        _timeProvider = timeProvider;
        _delayAsync = delayAsync;
        _idleTimeout = idleTimeout;
        _stopGracePeriod = stopGracePeriod;

        lock (_stateGate)
        {
            StartIdleWait();
        }
    }

    public string TargetPath { get; }

    public Task<WorkspaceDaemonStopReason> Completion => _completion.Task;

    /// <summary>
    /// Executes one validated command while linking its deadline and client lifetime to workspace cancellation.
    /// </summary>
    public async Task<WorkspaceDaemonSessionResult> ExecuteAsync(
        DaemonCommandRequest request,
        CancellationToken clientCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var command = request.ToParsedCommand();
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(clientCancellationToken);
        var deadlineCancellation = new CancellationTokenSource();
        var trackedRequest = new TrackedRequest(requestCancellation, deadlineCancellation);
        var registered = false;

        try
        {
            lock (_stateGate)
            {
                EnsureAcceptingRequests();
                if (_activeRequests.ContainsKey(request.RequestId))
                {
                    throw new DaemonProtocolException(
                        DaemonProtocolError.InvalidMessage,
                        $"Daemon request ID '{request.RequestId}' is already active.");
                }

                if (_activeRequests.Count == 0)
                {
                    _requestsDrained = CreateSignal();
                }

                _activeRequests.Add(request.RequestId, trackedRequest);
                registered = true;
                _activityVersion++;
                CancelIdleWait();
            }

            var deadlineTask = CancelAtDeadlineAsync(
                request.DeadlineUtc,
                requestCancellation,
                deadlineCancellation.Token);
            try
            {
                return await _executeAsync(command, requestCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                deadlineCancellation.Cancel();
                await IgnoreExpectedCancellationAsync(deadlineTask, deadlineCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (registered)
            {
                lock (_stateGate)
                {
                    if (_activeRequests.Remove(request.RequestId) && _activeRequests.Count == 0)
                    {
                        _requestsDrained.TrySetResult();
                        if (_state == WorkspaceDaemonHostState.Running)
                        {
                            _activityVersion++;
                            StartIdleWait();
                        }
                    }
                }
            }

            trackedRequest.Dispose();
        }
    }

    /// <summary>
    /// Captures lifecycle and workspace state without extending the daemon idle lifetime.
    /// </summary>
    public WorkspaceDaemonHostSnapshot CaptureStatus()
    {
        WorkspaceDaemonHostState hostState;
        WorkspaceDaemonSessionSnapshot session;
        lock (_stateGate)
        {
            hostState = _state;
            session = _captureSessionSnapshot();
        }

        return new WorkspaceDaemonHostSnapshot(
            hostState,
            TargetPath,
            _processId,
            session.State,
            session.Generation,
            session.ActiveRequests,
            session.QueuedRequests,
            BoundDiagnostic(session.LastInfrastructureDiagnostic));
    }

    public DaemonStatusResponse CreateStatusResponse(DaemonStatusRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DaemonProtocol.EnsureCompatible(request);
        var status = CaptureStatus();
        return new DaemonStatusResponse(
            RoslynKitBuildInfo.DaemonProtocolVersion,
            request.RequestId,
            Running: status.State != WorkspaceDaemonHostState.Stopped,
            status.TargetPath,
            status.ProcessId,
            status.WorkspaceState.ToString().ToLowerInvariant(),
            status.Generation,
            status.ActiveRequests,
            status.QueuedRequests,
            status.Diagnostic);
    }

    /// <summary>
    /// Closes admission immediately and begins a bounded graceful drain without delaying the stop response.
    /// </summary>
    public DaemonStopResponse BeginStop(DaemonStopRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DaemonProtocol.EnsureCompatible(request);
        lock (_stateGate)
        {
            _ = EnsureStopStarted(WorkspaceDaemonStopReason.StopRequested);
        }

        return new DaemonStopResponse(
            RoslynKitBuildInfo.DaemonProtocolVersion,
            request.RequestId,
            Stopping: true);
    }

    public async ValueTask DisposeAsync()
    {
        Task stopTask;
        lock (_stateGate)
        {
            stopTask = EnsureStopStarted(WorkspaceDaemonStopReason.Disposed);
            _forceStop.TrySetResult();
        }

        await stopTask.ConfigureAwait(false);
        _ = await _completion.Task.ConfigureAwait(false);
    }

    private async Task CancelAtDeadlineAsync(
        DateTimeOffset deadlineUtc,
        CancellationTokenSource requestCancellation,
        CancellationToken timerCancellationToken)
    {
        var remaining = deadlineUtc - _timeProvider.GetUtcNow();
        if (remaining > TimeSpan.Zero)
        {
            try
            {
                await _delayAsync(remaining, timerCancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timerCancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        requestCancellation.Cancel();
    }

    private async Task ObserveIdleAsync(long activityVersion, CancellationTokenSource cancellation)
    {
        try
        {
            try
            {
                await _delayAsync(_idleTimeout, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }

            Task? stopTask = null;
            lock (_stateGate)
            {
                if (_state == WorkspaceDaemonHostState.Running &&
                    _activeRequests.Count == 0 &&
                    _activityVersion == activityVersion &&
                    ReferenceEquals(_idleCancellation, cancellation))
                {
                    _idleCancellation = null;
                    stopTask = EnsureStopStarted(WorkspaceDaemonStopReason.IdleTimeout);
                }
            }

            if (stopTask is not null)
            {
                await stopTask.ConfigureAwait(false);
            }
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task StopCoreAsync(WorkspaceDaemonStopReason reason)
    {
        try
        {
            IReadOnlyList<Exception> cancellationExceptions = [];
            Task drained;
            lock (_stateGate)
            {
                drained = _requestsDrained.Task;
            }

            if (!drained.IsCompleted)
            {
                using var graceCancellation = new CancellationTokenSource();
                var graceDelay = _delayAsync(_stopGracePeriod, graceCancellation.Token);
                var completed = await Task.WhenAny(drained, graceDelay, _forceStop.Task).ConfigureAwait(false);
                if (completed != drained)
                {
                    IReadOnlyList<CancellationTokenSource> requestsToCancel;
                    lock (_stateGate)
                    {
                        requestsToCancel = _activeRequests.Values
                            .Select(static request => request.Cancellation)
                            .ToArray();
                    }

                    cancellationExceptions = CancelRequests(requestsToCancel);
                }

                graceCancellation.Cancel();
                await IgnoreExpectedCancellationAsync(graceDelay, graceCancellation.Token).ConfigureAwait(false);
                await drained.ConfigureAwait(false);
            }

            await _disposeSessionAsync().ConfigureAwait(false);
            if (cancellationExceptions.Count > 0)
            {
                throw new AggregateException(
                    "One or more daemon request cancellation callbacks failed.",
                    cancellationExceptions);
            }

            lock (_stateGate)
            {
                _state = WorkspaceDaemonHostState.Stopped;
            }

            _completion.TrySetResult(reason);
        }
        catch (Exception exception)
        {
            lock (_stateGate)
            {
                _state = WorkspaceDaemonHostState.Stopped;
            }

            _completion.TrySetException(exception);
        }
    }

    private Task EnsureStopStarted(WorkspaceDaemonStopReason reason)
    {
        if (_stopTask is not null)
        {
            return _stopTask;
        }

        _state = WorkspaceDaemonHostState.Stopping;
        _activityVersion++;
        CancelIdleWait();
        _stopTask = StopCoreAsync(reason);
        return _stopTask;
    }

    private void StartIdleWait()
    {
        CancelIdleWait();
        var cancellation = new CancellationTokenSource();
        _idleCancellation = cancellation;
        _ = ObserveIdleAsync(_activityVersion, cancellation);
    }

    private void CancelIdleWait()
    {
        var cancellation = _idleCancellation;
        _idleCancellation = null;
        cancellation?.Cancel();
    }

    private void EnsureAcceptingRequests()
    {
        if (_state != WorkspaceDaemonHostState.Running)
        {
            throw new InvalidOperationException("The workspace daemon is stopping and is not accepting new requests.");
        }
    }

    private static IReadOnlyList<Exception> CancelRequests(
        IEnumerable<CancellationTokenSource> cancellations)
    {
        List<Exception>? exceptions = null;
        foreach (var cancellation in cancellations)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (AggregateException exception)
            {
                exceptions ??= [];
                exceptions.AddRange(exception.InnerExceptions);
            }
        }

        return exceptions ?? [];
    }

    private static async Task IgnoreExpectedCancellationAsync(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string? BoundDiagnostic(string? diagnostic)
    {
        if (diagnostic is null || diagnostic.Length <= MaximumDiagnosticLength)
        {
            return diagnostic;
        }

        return diagnostic[..MaximumDiagnosticLength];
    }

    private static Func<TimeSpan, CancellationToken, Task> CreateDelay(TimeProvider timeProvider)
    {
        return (delay, cancellationToken) => Task.Delay(delay, timeProvider, cancellationToken);
    }

    private static TaskCompletionSource CreateSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static TaskCompletionSource CreateCompletedSignal()
    {
        var signal = CreateSignal();
        signal.SetResult();
        return signal;
    }

    private sealed class TrackedRequest(
        CancellationTokenSource cancellation,
        CancellationTokenSource deadlineCancellation) : IDisposable
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public void Dispose()
        {
            deadlineCancellation.Dispose();
            Cancellation.Dispose();
        }
    }
}
