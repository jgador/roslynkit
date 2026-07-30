using System.IO.Pipes;

namespace RoslynKit;

/// <summary>
/// Opens one short-lived daemon connection, completes the compatibility handshake, and exchanges one operation.
/// </summary>
internal sealed class DaemonPipeClient
{
    private static readonly TimeSpan ControlResponseTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<string, NamedPipeClientStream> _createClient;

    public DaemonPipeClient()
        : this(DaemonNamedPipe.CreateClient)
    {
    }

    internal DaemonPipeClient(Func<string, NamedPipeClientStream> createClient)
    {
        _createClient = createClient;
    }

    /// <summary>
    /// Connects, negotiates compatibility, and returns one complete correlated operation response.
    /// </summary>
    public async Task<DaemonResponse?> SendAsync(
        string endpointName,
        DaemonRequest request,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        ArgumentNullException.ThrowIfNull(request);
        ValidateTimeout(connectTimeout);

        await using var client = _createClient(endpointName);
        if (!await TryConnectAndHandshakeAsync(
            client,
            connectTimeout,
            cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        using var responseTimeout = CreateResponseTimeout(request, cancellationToken);
        try
        {
            await DaemonProtocol.WriteRequestAsync(client, request, responseTimeout.Token).ConfigureAwait(false);
            var response = await DaemonProtocol.ReadResponseAsync(client, responseTimeout.Token).ConfigureAwait(false);
            ValidateResponse(response, request.RequestId);
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (DaemonProtocolException exception) when (IsTransientDisconnect(exception))
        {
            return null;
        }
        catch (IOException exception) when (exception is not DaemonProtocolException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reports readiness only after a compatible handshake completes successfully.
    /// </summary>
    public async Task<bool> ProbeAsync(
        string endpointName,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        ValidateTimeout(connectTimeout);

        await using var client = _createClient(endpointName);
        return await TryConnectAndHandshakeAsync(
            client,
            connectTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TryConnectAndHandshakeAsync(
        NamedPipeClientStream client,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectTimeout);
        try
        {
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await CompleteHandshakeAsync(client, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (DaemonProtocolException exception) when (IsTransientDisconnect(exception))
        {
            return false;
        }
        catch (IOException exception) when (exception is not DaemonProtocolException)
        {
            return false;
        }
    }

    private static async Task CompleteHandshakeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var request = new DaemonHandshakeRequest(
            RoslynKitBuildInfo.DaemonProtocolVersion,
            Guid.NewGuid());
        await DaemonProtocol.WriteRequestAsync(stream, request, cancellationToken).ConfigureAwait(false);
        var response = await DaemonProtocol.ReadResponseAsync(stream, cancellationToken).ConfigureAwait(false);
        ValidateResponse(response, request.RequestId);
        if (response is not DaemonHandshakeResponse handshake)
        {
            throw InvalidMessage("The daemon returned a non-handshake response during readiness negotiation.");
        }

        if (!handshake.Accepted)
        {
            throw InvalidMessage(handshake.Diagnostic ?? "The daemon rejected the compatibility handshake.");
        }
    }

    private static void ValidateResponse(DaemonResponse response, Guid requestId)
    {
        DaemonProtocol.EnsureCompatible(response);
        if (response.RequestId != requestId)
        {
            throw InvalidMessage("The daemon response request ID did not match the request.");
        }
    }

    private static CancellationTokenSource CreateResponseTimeout(
        DaemonRequest request,
        CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = request is DaemonCommandRequest command
            ? command.DeadlineUtc - DateTimeOffset.UtcNow
            : ControlResponseTimeout;
        timeout.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        return timeout;
    }

    private static bool IsTransientDisconnect(DaemonProtocolException exception)
    {
        return exception.Error is DaemonProtocolError.EndOfStream
            or DaemonProtocolError.UnexpectedEndOfStream;
    }

    private static DaemonProtocolException InvalidMessage(string message)
    {
        return new DaemonProtocolException(DaemonProtocolError.InvalidMessage, message);
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The connection timeout must be positive.");
        }
    }
}
