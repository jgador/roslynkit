using System.IO.Pipes;

namespace RoslynKit;

/// <summary>
/// Accepts same-user named-pipe connections and dispatches one handshaken operation per connection.
/// </summary>
internal sealed class WorkspaceDaemonServer
{
    private const int MaximumConcurrentConnections = 32;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly string _endpointName;
    private readonly WorkspaceDaemonHost _host;
    private readonly Func<string, NamedPipeServerStream> _createServer;
    private readonly object _connectionGate = new();
    private readonly HashSet<Task> _connectionTasks = [];
    private readonly CancellationTokenSource _stopAccepting = new();
    private readonly TaskCompletionSource<Exception> _fatalConnectionFailure =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WorkspaceDaemonServer(
        string endpointName,
        WorkspaceDaemonHost host)
        : this(endpointName, host, DaemonNamedPipe.CreateServer)
    {
    }

    internal WorkspaceDaemonServer(
        string endpointName,
        WorkspaceDaemonHost host,
        Func<string, NamedPipeServerStream> createServer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(createServer);

        _endpointName = endpointName;
        _host = host;
        _createServer = createServer;
    }

    /// <summary>
    /// Runs the bounded accept loop until the lifecycle host completes or process cancellation is requested.
    /// </summary>
    public async Task<WorkspaceDaemonStopReason> RunAsync(CancellationToken cancellationToken)
    {
        using var acceptCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stopAccepting.Token);
        using var connectionReadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var connectionSlots = new SemaphoreSlim(MaximumConcurrentConnections, MaximumConcurrentConnections);

        try
        {
            while (!acceptCancellation.IsCancellationRequested && !_host.Completion.IsCompleted)
            {
                if (!await WaitUnlessHostCompletesAsync(
                        connectionSlots.WaitAsync(acceptCancellation.Token),
                        acceptCancellation).ConfigureAwait(false))
                {
                    break;
                }

                NamedPipeServerStream? pipe = null;
                var slotTransferred = false;
                try
                {
                    pipe = _createServer(_endpointName);
                    if (!await WaitUnlessHostCompletesAsync(
                            pipe.WaitForConnectionAsync(acceptCancellation.Token),
                            acceptCancellation).ConfigureAwait(false))
                    {
                        break;
                    }

                    var connectionTask = HandleConnectionAndReleaseAsync(
                        pipe,
                        connectionSlots,
                        connectionReadCancellation.Token,
                        cancellationToken);
                    pipe = null;
                    slotTransferred = true;
                    TrackConnection(connectionTask);
                }
                finally
                {
                    if (pipe is not null)
                    {
                        await pipe.DisposeAsync().ConfigureAwait(false);
                    }

                    if (!slotTransferred)
                    {
                        connectionSlots.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (acceptCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            acceptCancellation.Cancel();
            connectionReadCancellation.Cancel();
        }

        if (cancellationToken.IsCancellationRequested || _fatalConnectionFailure.Task.IsCompleted)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
        }

        await AwaitConnectionsAsync().ConfigureAwait(false);
        if (_fatalConnectionFailure.Task.IsCompletedSuccessfully)
        {
            var failure = await _fatalConnectionFailure.Task.ConfigureAwait(false);
            throw new InvalidOperationException("A daemon connection failed unexpectedly.", failure);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await _host.Completion.ConfigureAwait(false);
    }

    private async Task<bool> WaitUnlessHostCompletesAsync(
        Task operation,
        CancellationTokenSource acceptCancellation)
    {
        var completed = await Task.WhenAny(operation, _host.Completion).ConfigureAwait(false);
        if (completed == _host.Completion)
        {
            acceptCancellation.Cancel();
            await IgnoreExpectedCancellationAsync(operation, acceptCancellation.Token).ConfigureAwait(false);
            return false;
        }

        await operation.ConfigureAwait(false);
        return true;
    }

    private async Task HandleConnectionAndReleaseAsync(
        NamedPipeServerStream pipe,
        SemaphoreSlim connectionSlots,
        CancellationToken readCancellationToken,
        CancellationToken writeCancellationToken)
    {
        try
        {
            await using (pipe.ConfigureAwait(false))
            {
                try
                {
                    await HandleConnectionAsync(
                        pipe,
                        readCancellationToken,
                        writeCancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (readCancellationToken.IsCancellationRequested)
                {
                }
                catch (DaemonProtocolException)
                {
                }
                catch (IOException)
                {
                }
            }
        }
        catch (Exception exception)
        {
            SignalFatalConnectionFailure(exception);
        }
        finally
        {
            try
            {
                connectionSlots.Release();
            }
            catch (Exception exception)
            {
                SignalFatalConnectionFailure(exception);
            }
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken readCancellationToken,
        CancellationToken writeCancellationToken)
    {
        var firstRequest = await DaemonProtocol.ReadRequestAsync(pipe, readCancellationToken).ConfigureAwait(false);
        if (firstRequest is not DaemonHandshakeRequest handshake)
        {
            throw new DaemonProtocolException(
                DaemonProtocolError.InvalidMessage,
                "A daemon connection must begin with a handshake request.");
        }

        var handshakeResponse = new DaemonHandshakeResponse(
            handshake.RequestId,
            Accepted: true,
            Diagnostic: null);

        await DaemonProtocol.WriteResponseAsync(
            pipe,
            handshakeResponse,
            writeCancellationToken).ConfigureAwait(false);
        if (!handshakeResponse.Accepted)
        {
            return;
        }

        DaemonRequest request;
        try
        {
            request = await DaemonProtocol.ReadRequestAsync(pipe, readCancellationToken).ConfigureAwait(false);
        }
        catch (DaemonProtocolException exception) when (exception.Error == DaemonProtocolError.EndOfStream)
        {
            return;
        }

        DaemonResponse? response = request switch
        {
            DaemonCommandRequest command => await ExecuteCommandAsync(
                pipe,
                command,
                writeCancellationToken).ConfigureAwait(false),
            DaemonStatusRequest status => _host.CreateStatusResponse(status),
            DaemonStopRequest stop => _host.BeginStop(stop),
            DaemonHandshakeRequest => throw new DaemonProtocolException(
                DaemonProtocolError.InvalidMessage,
                "A daemon connection accepts only one handshake."),
            _ => throw new DaemonProtocolException(
                DaemonProtocolError.InvalidMessage,
                $"Unsupported daemon request type '{request.GetType().Name}'."),
        };

        if (response is not null)
        {
            try
            {
                await DaemonProtocol.WriteResponseAsync(
                    pipe,
                    response,
                    writeCancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (request is DaemonStopRequest)
                {
                    _stopAccepting.Cancel();
                }
            }
        }
    }

    private async Task<DaemonCommandResponse?> ExecuteCommandAsync(
        NamedPipeServerStream pipe,
        DaemonCommandRequest request,
        CancellationToken processCancellationToken)
    {
        ValidateTarget(request);
        using var disconnected = new CancellationTokenSource();
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            processCancellationToken);
        var disconnectMonitor = MonitorDisconnectAsync(
            pipe,
            disconnected,
            monitorCancellation.Token);

        WorkspaceDaemonSessionResult? execution = null;
        CliProcessResult? processResult = null;
        try
        {
            execution = await _host.ExecuteAsync(request, disconnected.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (disconnected.IsCancellationRequested)
        {
        }
        catch (DaemonProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (!disconnected.IsCancellationRequested)
        {
            processResult = CliProcessResult.FromException(exception);
        }
        finally
        {
            monitorCancellation.Cancel();
            await IgnoreExpectedCancellationAsync(
                disconnectMonitor,
                monitorCancellation.Token).ConfigureAwait(false);
        }

        if (disconnected.IsCancellationRequested)
        {
            return null;
        }

        if (processResult is null)
        {
            if (execution?.ProcessResult is null)
            {
                throw new IOException(execution?.Diagnostic ?? "Daemon workspace infrastructure failed.");
            }

            processResult = execution.ProcessResult;
        }

        return DaemonCommandResponse.Create(request.RequestId, processResult);
    }

    private void ValidateTarget(DaemonCommandRequest request)
    {
        if (!request.Options.TryGetValue("target", out var targetPath))
        {
            throw new DaemonProtocolException(
                DaemonProtocolError.InvalidMessage,
                "Daemon command requests require a target path.");
        }

        string canonicalTarget;
        try
        {
            canonicalTarget = PathCanonicalizer.ResolveExistingPath(targetPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new DaemonProtocolException(
                DaemonProtocolError.InvalidMessage,
                "The daemon command target could not be canonicalized.",
                exception);
        }

        if (!PathComparer.Equals(canonicalTarget, _host.TargetPath))
        {
            throw new DaemonProtocolException(
                DaemonProtocolError.InvalidMessage,
                "The daemon command target does not match the hosted workspace identity.");
        }
    }

    private static async Task MonitorDisconnectAsync(
        Stream stream,
        CancellationTokenSource disconnected,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        try
        {
            _ = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            await TryCancelAsync(disconnected).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            await TryCancelAsync(disconnected).ConfigureAwait(false);
        }
    }

    private void TrackConnection(Task connectionTask)
    {
        lock (_connectionGate)
        {
            _connectionTasks.Add(connectionTask);
        }

        _ = RemoveConnectionWhenCompleteAsync(connectionTask);
    }

    private async Task RemoveConnectionWhenCompleteAsync(Task connectionTask)
    {
        try
        {
            await connectionTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_connectionGate)
            {
                _connectionTasks.Remove(connectionTask);
            }
        }
    }

    private void SignalFatalConnectionFailure(Exception exception)
    {
        _fatalConnectionFailure.TrySetResult(exception);
        _stopAccepting.Cancel();
    }

    private async Task AwaitConnectionsAsync()
    {
        while (true)
        {
            Task[] connections;
            lock (_connectionGate)
            {
                connections = [.. _connectionTasks];
            }

            if (connections.Length == 0)
            {
                return;
            }

            await Task.WhenAll(connections).ConfigureAwait(false);
        }
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

    private static async Task TryCancelAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (AggregateException)
        {
        }
    }
}
