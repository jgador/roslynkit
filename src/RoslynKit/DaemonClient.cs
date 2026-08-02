using System.Net.Sockets;

namespace RoslynKit;

/// <summary>
/// Resolves a compatible daemon endpoint, coordinates on-demand startup, and exchanges command or control requests.
/// </summary>
internal sealed class DaemonClient
{
    private const string CommandInfrastructureFailureMessage =
        "The RoslynKit daemon command could not be completed because daemon infrastructure was unavailable.";

    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan CommandDeadline = TimeSpan.FromHours(1);

    private readonly Func<string, CancellationToken, Task<DaemonClientEndpoint>> _resolveEndpointAsync;
    private readonly Func<string, DaemonRequest, TimeSpan, CancellationToken, Task<DaemonResponse?>> _sendAsync;
    private readonly Func<string, TimeSpan, CancellationToken, Task<bool>> _probeAsync;
    private readonly Func<string, IDisposable?> _tryAcquireBootstrapLease;
    private readonly Action<string> _startDaemon;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public DaemonClient()
        : this(CreateDefaultDependencies())
    {
    }

    private DaemonClient(DaemonClientDependencies dependencies)
        : this(
            dependencies.ResolveEndpointAsync,
            dependencies.SendAsync,
            dependencies.ProbeAsync,
            dependencies.TryAcquireBootstrapLease,
            dependencies.StartDaemon,
            TimeProvider.System,
            Task.Delay)
    {
    }

    internal DaemonClient(
        Func<string, CancellationToken, Task<DaemonClientEndpoint>> resolveEndpointAsync,
        Func<string, DaemonRequest, TimeSpan, CancellationToken, Task<DaemonResponse?>> sendAsync,
        Func<string, TimeSpan, CancellationToken, Task<bool>> probeAsync,
        Func<string, IDisposable?> tryAcquireBootstrapLease,
        Action<string> startDaemon,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _resolveEndpointAsync = resolveEndpointAsync;
        _sendAsync = sendAsync;
        _probeAsync = probeAsync;
        _tryAcquireBootstrapLease = tryAcquireBootstrapLease;
        _startDaemon = startDaemon;
        _timeProvider = timeProvider;
        _delayAsync = delayAsync;
    }

    public static DaemonClient Shared { get; } = new();

    /// <summary>
    /// Sends a workspace command to a compatible server, starting that server when no endpoint is ready.
    /// </summary>
    public async Task<CliProcessResult> ExecuteAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            var endpoint = await ResolveEndpointAsync(command, cancellationToken).ConfigureAwait(false);
            var request = CreateCommandRequest(command);
            var response = await _sendAsync(
                endpoint.EndpointName,
                request,
                ConnectTimeout,
                cancellationToken).ConfigureAwait(false);
            if (response is DaemonCommandResponse available)
            {
                EnsureResponse(available, request.RequestId);
                return available.ToProcessResult();
            }

            if (response is not null)
            {
                throw UnexpectedResponse<DaemonCommandResponse>(response);
            }

            var startupDeadline = _timeProvider.GetUtcNow() + StartupTimeout;
            await EnsureRunningAsync(endpoint, startupDeadline, cancellationToken).ConfigureAwait(false);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                request = CreateCommandRequest(command);
                response = await _sendAsync(
                    endpoint.EndpointName,
                    request,
                    ConnectTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (response is not null || attempt > 0 || _timeProvider.GetUtcNow() >= startupDeadline)
                {
                    break;
                }

                await EnsureRunningAsync(endpoint, startupDeadline, cancellationToken).ConfigureAwait(false);
            }

            var commandResponse = RequireResponse<DaemonCommandResponse>(response, request.RequestId);
            return commandResponse.ToProcessResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DaemonClientInfrastructureException)
        {
            throw;
        }
        catch (DaemonProtocolException exception)
        {
            throw new DaemonClientInfrastructureException(CommandInfrastructureFailureMessage, exception);
        }
        catch (Exception exception) when (exception is
            IOException or
            SocketException or
            TimeoutException or
            UnauthorizedAccessException or
            PlatformNotSupportedException or
            InvalidOperationException)
        {
            throw new DaemonClientInfrastructureException(CommandInfrastructureFailureMessage, exception);
        }
    }

    /// <summary>
    /// Queries an existing compatible server without triggering startup.
    /// </summary>
    public async Task<DaemonStatusResponse?> GetStatusAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var endpoint = await TryResolveControlEndpointAsync(command, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return null;
        }

        var request = new DaemonStatusRequest(
            RoslynKitBuildInfo.DaemonProtocolVersion,
            Guid.NewGuid());
        var response = await _sendAsync(
            endpoint.EndpointName,
            request,
            ConnectTimeout,
            cancellationToken).ConfigureAwait(false);
        return response is null ? null : RequireResponse<DaemonStatusResponse>(response, request.RequestId);
    }

    /// <summary>
    /// Requests graceful shutdown from an existing compatible server without triggering startup.
    /// </summary>
    public async Task<DaemonStopResponse?> StopAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var endpoint = await TryResolveControlEndpointAsync(command, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return null;
        }

        var request = new DaemonStopRequest(
            RoslynKitBuildInfo.DaemonProtocolVersion,
            Guid.NewGuid());
        var response = await _sendAsync(
            endpoint.EndpointName,
            request,
            ConnectTimeout,
            cancellationToken).ConfigureAwait(false);
        return response is null ? null : RequireResponse<DaemonStopResponse>(response, request.RequestId);
    }

    private async Task EnsureRunningAsync(
        DaemonClientEndpoint endpoint,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _probeAsync(
                endpoint.EndpointName,
                ConnectTimeout,
                cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            using var bootstrapLease = _tryAcquireBootstrapLease(endpoint.EndpointName);
            if (bootstrapLease is not null)
            {
                if (await _probeAsync(
                    endpoint.EndpointName,
                    ConnectTimeout,
                    cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                try
                {
                    _startDaemon(endpoint.TargetPath);
                }
                catch (Exception exception)
                {
                    throw new DaemonClientInfrastructureException(
                        "The RoslynKit daemon process could not be started.",
                        exception);
                }

                while (_timeProvider.GetUtcNow() < deadline)
                {
                    if (await _probeAsync(
                        endpoint.EndpointName,
                        ConnectTimeout,
                        cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    await DelayUntilNextProbeAsync(deadline, cancellationToken).ConfigureAwait(false);
                }

                break;
            }

            await DelayUntilNextProbeAsync(deadline, cancellationToken).ConfigureAwait(false);
        }

        throw new DaemonClientInfrastructureException(
            $"The RoslynKit daemon endpoint '{endpoint.EndpointName}' did not become ready within {StartupTimeout.TotalSeconds:0} seconds.");
    }

    private async Task DelayUntilNextProbeAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var remaining = deadline - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        await _delayAsync(
            remaining < StartupPollInterval ? remaining : StartupPollInterval,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DaemonClientEndpoint> ResolveEndpointAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.Options.TryGetValue("target", out var targetPath))
        {
            throw new InvalidOperationException($"Command '{command.Name}' does not define a target.");
        }

        return await _resolveEndpointAsync(targetPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DaemonClientEndpoint?> TryResolveControlEndpointAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ResolveEndpointAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (DaemonClientInfrastructureException)
        {
            return null;
        }
    }

    private static DaemonCommandRequest CreateCommandRequest(ParsedCommand command)
    {
        return DaemonCommandRequest.Create(
            command,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow + CommandDeadline);
    }

    private static TResponse RequireResponse<TResponse>(DaemonResponse? response, Guid requestId)
        where TResponse : DaemonResponse
    {
        if (response is null)
        {
            throw new DaemonClientInfrastructureException(
                "The RoslynKit daemon connection closed before returning a complete response.");
        }

        if (response is not TResponse typedResponse)
        {
            throw UnexpectedResponse<TResponse>(response);
        }

        EnsureResponse(typedResponse, requestId);
        return typedResponse;
    }

    private static void EnsureResponse(DaemonResponse response, Guid requestId)
    {
        DaemonProtocol.EnsureCompatible(response);
        if (response.RequestId != requestId)
        {
            throw new DaemonProtocolException(
                DaemonProtocolError.InvalidMessage,
                "The daemon response request ID did not match the request.");
        }
    }

    private static DaemonProtocolException UnexpectedResponse<TResponse>(DaemonResponse response)
    {
        return new DaemonProtocolException(
            DaemonProtocolError.InvalidMessage,
            $"The daemon returned '{response.GetType().Name}' where '{typeof(TResponse).Name}' was required.");
    }

    private static DaemonClientDependencies CreateDefaultDependencies()
    {
        var pipeClient = new DaemonPipeClient();
        return new DaemonClientDependencies(
            ResolveDefaultEndpointAsync,
            pipeClient.SendAsync,
            pipeClient.ProbeAsync,
            DaemonBootstrapLease.TryAcquire,
            DaemonProcessStarter.Start);
    }

    private static async Task<DaemonClientEndpoint> ResolveDefaultEndpointAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await new GitWorkspaceIdentityResolver()
                .ResolveAsync(targetPath, cancellationToken).ConfigureAwait(false);
            if (resolution.Identity is not { } workspaceIdentity)
            {
                throw new DaemonClientInfrastructureException(
                    resolution.Diagnostic ?? "The target does not have a supported Git workspace identity.");
            }

            var identity = new DaemonIdentityResolver().Resolve(workspaceIdentity);
            return new DaemonClientEndpoint(
                DaemonEndpointName.Create(identity),
                workspaceIdentity.TargetPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DaemonClientInfrastructureException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DaemonClientInfrastructureException(
                "The daemon endpoint identity could not be resolved.",
                exception);
        }
    }

    private sealed record DaemonClientDependencies(
        Func<string, CancellationToken, Task<DaemonClientEndpoint>> ResolveEndpointAsync,
        Func<string, DaemonRequest, TimeSpan, CancellationToken, Task<DaemonResponse?>> SendAsync,
        Func<string, TimeSpan, CancellationToken, Task<bool>> ProbeAsync,
        Func<string, IDisposable?> TryAcquireBootstrapLease,
        Action<string> StartDaemon);
}

/// <summary>
/// Couples one compatible pipe endpoint with the canonical target passed to a new daemon process.
/// </summary>
internal sealed record DaemonClientEndpoint(string EndpointName, string TargetPath);

/// <summary>
/// Classifies daemon-client startup or transport failures for the later standalone fallback boundary.
/// </summary>
internal sealed class DaemonClientInfrastructureException : IOException
{
    public DaemonClientInfrastructureException(string message)
        : base(message)
    {
    }

    public DaemonClientInfrastructureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
