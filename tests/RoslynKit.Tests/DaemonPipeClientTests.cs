namespace RoslynKit.Tests;

public sealed class DaemonPipeClientTests
{
    [Fact]
    public async Task SendAsync_PerformsHandshakeAndReturnsCorrelatedResponse()
    {
        var endpointName = $"roslynkit-test-{Guid.NewGuid():N}";
        await using var server = DaemonNamedPipe.CreateServer(endpointName);
        var serverTask = ServeCommandAsync(server, responseRequestId: null);
        var command = CliParser.Parse(
            ["symbols", "--target", TestPaths.SolutionPath(), "--query", "Program"]);
        var request = DaemonCommandRequest.Create(
            command,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddHours(1));

        var response = await new DaemonPipeClient().SendAsync(
            endpointName,
            request,
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        var commandResponse = Assert.IsType<DaemonCommandResponse>(response);
        Assert.Equal(request.RequestId, commandResponse.RequestId);
        Assert.Equal($"daemon result{Environment.NewLine}", commandResponse.Stdout);
        await serverTask;
    }

    [Fact]
    public async Task SendAsync_ReturnsNullWhenEndpointIsAbsent()
    {
        var response = await new DaemonPipeClient().SendAsync(
            $"roslynkit-test-{Guid.NewGuid():N}",
            new DaemonStatusRequest(RoslynKitBuildInfo.DaemonProtocolVersion, Guid.NewGuid()),
            TimeSpan.FromMilliseconds(25),
            TestContext.Current.CancellationToken);

        Assert.Null(response);
    }

    [Fact]
    public async Task SendAsync_RejectsMismatchedResponseRequestId()
    {
        var endpointName = $"roslynkit-test-{Guid.NewGuid():N}";
        await using var server = DaemonNamedPipe.CreateServer(endpointName);
        var serverTask = ServeCommandAsync(server, Guid.NewGuid());
        var request = new DaemonStatusRequest(
            RoslynKitBuildInfo.DaemonProtocolVersion,
            Guid.NewGuid());

        await Assert.ThrowsAsync<DaemonProtocolException>(() => new DaemonPipeClient().SendAsync(
            endpointName,
            request,
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));

        await serverTask;
    }

    [Fact]
    public async Task ProbeAsync_TimesOutWhenConnectedServerDoesNotHandshake()
    {
        var endpointName = $"roslynkit-test-{Guid.NewGuid():N}";
        await using var server = DaemonNamedPipe.CreateServer(endpointName);
        var accepted = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        var ready = await new DaemonPipeClient().ProbeAsync(
            endpointName,
            TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken);

        await accepted;
        Assert.False(ready);
    }

    [Fact]
    public async Task SendAsync_ReturnsNullWhenServerDisconnectsDuringHandshake()
    {
        var endpointName = $"roslynkit-test-{Guid.NewGuid():N}";
        await using var server = DaemonNamedPipe.CreateServer(endpointName);
        var request = new DaemonStatusRequest(
            RoslynKitBuildInfo.DaemonProtocolVersion,
            Guid.NewGuid());
        var clientTask = new DaemonPipeClient().SendAsync(
            endpointName,
            request,
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        server.Disconnect();

        var response = await clientTask;

        Assert.Null(response);
    }

    private static async Task ServeCommandAsync(
        System.IO.Pipes.NamedPipeServerStream server,
        Guid? responseRequestId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await server.WaitForConnectionAsync(cancellationToken);
        var handshake = Assert.IsType<DaemonHandshakeRequest>(
            await DaemonProtocol.ReadRequestAsync(server, cancellationToken));
        await DaemonProtocol.WriteResponseAsync(
            server,
            new DaemonHandshakeResponse(
                RoslynKitBuildInfo.DaemonProtocolVersion,
                handshake.RequestId,
                Accepted: true,
                Diagnostic: null),
            cancellationToken);

        var operation = await DaemonProtocol.ReadRequestAsync(server, cancellationToken);
        DaemonResponse response = operation switch
        {
            DaemonCommandRequest command => DaemonCommandResponse.Create(
                responseRequestId ?? command.RequestId,
                CliProcessResult.Success("daemon result")),
            DaemonStatusRequest status => new DaemonStatusResponse(
                RoslynKitBuildInfo.DaemonProtocolVersion,
                responseRequestId ?? status.RequestId,
                Running: true,
                TargetPath: TestPaths.SolutionPath(),
                ProcessId: Environment.ProcessId,
                WorkspaceState: "ready",
                Generation: 1,
                ActiveRequests: 0,
                QueuedRequests: 0,
                Diagnostic: null),
            _ => throw new InvalidOperationException("Unexpected request type."),
        };
        await DaemonProtocol.WriteResponseAsync(server, response, cancellationToken);
    }
}
