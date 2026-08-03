namespace RoslynKit.Tests;

public sealed class NamedPipeDaemonTransportTests
{
    [Fact]
    public async Task Create_ConnectsLocalSameUserStreamsAndExchangesFramedMessages()
    {
        var endpointName = $"roslynkit-test-{Guid.NewGuid():N}";
        await using var server = DaemonNamedPipe.CreateServer(endpointName);
        await using var client = DaemonNamedPipe.CreateClient(endpointName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        var waiting = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await waiting;

        Assert.Equal(System.IO.Pipes.PipeTransmissionMode.Byte, server.TransmissionMode);
        Assert.Equal(System.IO.Pipes.PipeTransmissionMode.Byte, server.ReadMode);
        Assert.True(server.IsAsync);
        Assert.True(client.IsAsync);

        var request = new DaemonHandshakeRequest(RoslynKitBuildInfo.DaemonProtocolVersion, Guid.NewGuid());
        var readingRequest = DaemonProtocol.ReadRequestAsync(server, timeout.Token);
        await DaemonProtocol.WriteRequestAsync(client, request, timeout.Token);
        var receivedRequest = await readingRequest;
        Assert.Equal(request, receivedRequest);

        var response = new DaemonHandshakeResponse(
            RoslynKitBuildInfo.DaemonProtocolVersion,
            request.RequestId,
            Accepted: true,
            Diagnostic: null);
        var readingResponse = DaemonProtocol.ReadResponseAsync(client, timeout.Token);
        await DaemonProtocol.WriteResponseAsync(server, response, timeout.Token);
        var receivedResponse = await readingResponse;
        Assert.Equal(response, receivedResponse);
    }

    [Fact]
    public async Task ReadRequestAsync_CancelsWhileWaitingOnConnectedPipe()
    {
        var endpointName = $"roslynkit-test-{Guid.NewGuid():N}";
        await using var server = DaemonNamedPipe.CreateServer(endpointName);
        await using var client = DaemonNamedPipe.CreateClient(endpointName);
        using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        connectionTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        var waiting = server.WaitForConnectionAsync(connectionTimeout.Token);
        await client.ConnectAsync(connectionTimeout.Token);
        await waiting;
        using var cancellation = new CancellationTokenSource();

        var read = DaemonProtocol.ReadRequestAsync(server, cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }
}
