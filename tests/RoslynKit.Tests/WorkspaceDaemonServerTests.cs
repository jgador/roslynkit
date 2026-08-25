using System.IO.Pipes;

namespace RoslynKit.Tests;

public sealed class WorkspaceDaemonServerTests
{
    [Fact]
    public async Task RunAsync_RequiresHandshakeAndDispatchesStatus()
    {
        await using var context = TestServerContext.Start();
        await using var client = await context.ConnectAndHandshakeAsync();
        var request = new DaemonStatusRequest(Guid.NewGuid());

        await DaemonProtocol.WriteRequestAsync(client, request, context.CancellationToken);
        var response = Assert.IsType<DaemonStatusResponse>(
            await DaemonProtocol.ReadResponseAsync(client, context.CancellationToken));

        Assert.Equal(request.RequestId, response.RequestId);
        Assert.True(response.Running);
        Assert.Equal(context.TargetPath, response.TargetPath);
    }

    [Fact]
    public async Task RunAsync_AcceptsAnotherConnectionWhileCommandIsRunning()
    {
        var executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var context = TestServerContext.Start(async (_, cancellationToken) =>
        {
            executionStarted.TrySetResult();
            await releaseExecution.Task.WaitAsync(cancellationToken);
            return WorkspaceDaemonSessionResult.Successful(CliProcessResult.Success("cached"), generation: 1);
        });
        await using var commandClient = await context.ConnectAndHandshakeAsync();
        var commandRequest = CreateCommandRequest(context.TargetPath);
        await DaemonProtocol.WriteRequestAsync(commandClient, commandRequest, context.CancellationToken);
        var commandResponse = DaemonProtocol.ReadResponseAsync(commandClient, context.CancellationToken);
        await executionStarted.Task.WaitAsync(context.CancellationToken);

        await using var statusClient = await context.ConnectAndHandshakeAsync();
        var statusRequest = new DaemonStatusRequest(Guid.NewGuid());
        await DaemonProtocol.WriteRequestAsync(statusClient, statusRequest, context.CancellationToken);
        var statusResponse = Assert.IsType<DaemonStatusResponse>(
            await DaemonProtocol.ReadResponseAsync(statusClient, context.CancellationToken));

        Assert.True(statusResponse.Running);
        releaseExecution.TrySetResult();
        var response = Assert.IsType<DaemonCommandResponse>(await commandResponse);
        Assert.Equal("cached" + Environment.NewLine, response.Stdout);
    }

    [Fact]
    public async Task RunAsync_DisconnectCancelsRunningCommandAndKeepsServerAvailable()
    {
        var executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var context = TestServerContext.Start(async (_, cancellationToken) =>
        {
            executionStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The command should have been canceled.");
            }
            catch (OperationCanceledException)
            {
                executionCanceled.TrySetResult();
                throw;
            }
        });
        var commandClient = await context.ConnectAndHandshakeAsync();
        await DaemonProtocol.WriteRequestAsync(
            commandClient,
            CreateCommandRequest(context.TargetPath),
            context.CancellationToken);
        await executionStarted.Task.WaitAsync(context.CancellationToken);

        await commandClient.DisposeAsync();

        await executionCanceled.Task.WaitAsync(context.CancellationToken);
        await using var statusClient = await context.ConnectAndHandshakeAsync();
        var statusRequest = new DaemonStatusRequest(Guid.NewGuid());
        await DaemonProtocol.WriteRequestAsync(statusClient, statusRequest, context.CancellationToken);
        Assert.IsType<DaemonStatusResponse>(
            await DaemonProtocol.ReadResponseAsync(statusClient, context.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_ProcessCancellationCancelsActiveCommand()
    {
        var executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var context = TestServerContext.Start(async (_, cancellationToken) =>
        {
            executionStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The command should have been canceled.");
            }
            catch (OperationCanceledException)
            {
                executionCanceled.TrySetResult();
                throw;
            }
        });
        await using var client = await context.ConnectAndHandshakeAsync();
        await DaemonProtocol.WriteRequestAsync(
            client,
            CreateCommandRequest(context.TargetPath),
            context.CancellationToken);
        await executionStarted.Task.WaitAsync(context.CancellationToken);

        context.CancelServer();

        await executionCanceled.Task.WaitAsync(context.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.ServerTask.WaitAsync(context.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_RejectsOperationBeforeHandshakeWithoutStoppingListener()
    {
        await using var context = TestServerContext.Start();
        await using (var invalidClient = await context.ConnectAsync())
        {
            await DaemonProtocol.WriteRequestAsync(
                invalidClient,
                new DaemonStatusRequest(Guid.NewGuid()),
                context.CancellationToken);
            var exception = await Assert.ThrowsAsync<DaemonProtocolException>(
                () => DaemonProtocol.ReadResponseAsync(invalidClient, context.CancellationToken));
            Assert.Equal(DaemonProtocolError.EndOfStream, exception.Error);
        }

        await using var validClient = await context.ConnectAndHandshakeAsync();
        var request = new DaemonStatusRequest(Guid.NewGuid());
        await DaemonProtocol.WriteRequestAsync(validClient, request, context.CancellationToken);
        Assert.IsType<DaemonStatusResponse>(
            await DaemonProtocol.ReadResponseAsync(validClient, context.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_RejectsCommandForDifferentTarget()
    {
        var executed = false;
        await using var context = TestServerContext.Start((_, _) =>
        {
            executed = true;
            return Task.FromResult(
                WorkspaceDaemonSessionResult.Successful(CliProcessResult.Success("unexpected"), generation: 1));
        });
        await using var client = await context.ConnectAndHandshakeAsync();
        await DaemonProtocol.WriteRequestAsync(
            client,
            CreateCommandRequest(TestPaths.FixtureProjectPath()),
            context.CancellationToken);

        var exception = await Assert.ThrowsAsync<DaemonProtocolException>(
            () => DaemonProtocol.ReadResponseAsync(client, context.CancellationToken));

        Assert.Equal(DaemonProtocolError.EndOfStream, exception.Error);
        Assert.False(executed);
    }

    [Fact]
    public async Task RunAsync_InfrastructureFailureClosesOnlyCommandConnection()
    {
        await using var context = TestServerContext.Start((_, _) => Task.FromResult(
            WorkspaceDaemonSessionResult.InfrastructureFailure(
                GitWorktreeFingerprintFailureKind.GitFailure,
                "fingerprint failed")));
        await using var commandClient = await context.ConnectAndHandshakeAsync();
        await DaemonProtocol.WriteRequestAsync(
            commandClient,
            CreateCommandRequest(context.TargetPath),
            context.CancellationToken);

        var exception = await Assert.ThrowsAsync<DaemonProtocolException>(
            () => DaemonProtocol.ReadResponseAsync(commandClient, context.CancellationToken));
        Assert.Equal(DaemonProtocolError.EndOfStream, exception.Error);

        await using var statusClient = await context.ConnectAndHandshakeAsync();
        var status = new DaemonStatusRequest(Guid.NewGuid());
        await DaemonProtocol.WriteRequestAsync(statusClient, status, context.CancellationToken);
        Assert.IsType<DaemonStatusResponse>(
            await DaemonProtocol.ReadResponseAsync(statusClient, context.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_StopAcknowledgesBeforeServerCompletes()
    {
        var executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var context = TestServerContext.Start(async (_, cancellationToken) =>
        {
            executionStarted.TrySetResult();
            await releaseExecution.Task.WaitAsync(cancellationToken);
            return WorkspaceDaemonSessionResult.Successful(CliProcessResult.Success("drained"), generation: 1);
        });
        await using var commandClient = await context.ConnectAndHandshakeAsync();
        await DaemonProtocol.WriteRequestAsync(
            commandClient,
            CreateCommandRequest(context.TargetPath),
            context.CancellationToken);
        var commandResponse = DaemonProtocol.ReadResponseAsync(commandClient, context.CancellationToken);
        await executionStarted.Task.WaitAsync(context.CancellationToken);
        await using var client = await context.ConnectAndHandshakeAsync();
        var request = new DaemonStopRequest(Guid.NewGuid());

        await DaemonProtocol.WriteRequestAsync(client, request, context.CancellationToken);
        var response = Assert.IsType<DaemonStopResponse>(
            await DaemonProtocol.ReadResponseAsync(client, context.CancellationToken));
        Assert.False(context.ServerTask.IsCompleted);

        releaseExecution.TrySetResult();
        Assert.IsType<DaemonCommandResponse>(await commandResponse);
        var reason = await context.ServerTask.WaitAsync(context.CancellationToken);

        Assert.True(response.Stopping);
        Assert.Equal(WorkspaceDaemonStopReason.StopRequested, reason);
    }

    [Fact]
    public async Task RunAsync_DisconnectDuringGracefulStopStillCancelsCommand()
    {
        var executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var context = TestServerContext.Start(async (_, cancellationToken) =>
        {
            executionStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The command should have been canceled.");
            }
            catch (OperationCanceledException)
            {
                executionCanceled.TrySetResult();
                throw;
            }
        });
        var commandClient = await context.ConnectAndHandshakeAsync();
        await DaemonProtocol.WriteRequestAsync(
            commandClient,
            CreateCommandRequest(context.TargetPath),
            context.CancellationToken);
        await executionStarted.Task.WaitAsync(context.CancellationToken);
        await using var stopClient = await context.ConnectAndHandshakeAsync();
        var stop = new DaemonStopRequest(Guid.NewGuid());
        await DaemonProtocol.WriteRequestAsync(stopClient, stop, context.CancellationToken);
        Assert.IsType<DaemonStopResponse>(
            await DaemonProtocol.ReadResponseAsync(stopClient, context.CancellationToken));

        await commandClient.DisposeAsync();

        await executionCanceled.Task.WaitAsync(context.CancellationToken);
        Assert.Equal(
            WorkspaceDaemonStopReason.StopRequested,
            await context.ServerTask.WaitAsync(context.CancellationToken));
    }

    private static DaemonCommandRequest CreateCommandRequest(string targetPath)
    {
        var command = CliParser.Parse(
            ["symbols", "--target", targetPath, "--query", "Program"]);
        return DaemonCommandRequest.Create(
            command,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1));
    }

    private sealed class TestServerContext : IAsyncDisposable
    {
        private readonly CancellationTokenSource _testTimeout;
        private readonly CancellationTokenSource _serverCancellation;
        private readonly WorkspaceDaemonHost _host;

        private TestServerContext(
            CancellationTokenSource testTimeout,
            CancellationTokenSource serverCancellation,
            WorkspaceDaemonHost host,
            string endpointName,
            string targetPath)
        {
            _testTimeout = testTimeout;
            _serverCancellation = serverCancellation;
            _host = host;
            EndpointName = endpointName;
            TargetPath = targetPath;
            ServerTask = new WorkspaceDaemonServer(endpointName, host).RunAsync(serverCancellation.Token);
        }

        public string EndpointName { get; }

        public string TargetPath { get; }

        public Task<WorkspaceDaemonStopReason> ServerTask { get; }

        public CancellationToken CancellationToken => _testTimeout.Token;

        public static TestServerContext Start(
            Func<ParsedCommand, CancellationToken, Task<WorkspaceDaemonSessionResult>>? executeAsync = null)
        {
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var targetPath = PathCanonicalizer.ResolveExistingPath(TestPaths.SolutionPath());
            var host = new WorkspaceDaemonHost(
                targetPath,
                Environment.ProcessId,
                executeAsync ?? ((_, _) => Task.FromResult(
                    WorkspaceDaemonSessionResult.Successful(CliProcessResult.Success("ok"), generation: 1))),
                () => new WorkspaceDaemonSessionSnapshot(
                    WorkspaceDaemonSessionState.Ready,
                    Generation: 1,
                    ActiveRequests: 0,
                    QueuedRequests: 0,
                    LastInfrastructureDiagnostic: null),
                () => ValueTask.CompletedTask,
                TimeProvider.System,
                static (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromSeconds(30));
            return new TestServerContext(
                timeout,
                serverCancellation,
                host,
                $"roslynkit-test-{Guid.NewGuid():N}",
                targetPath);
        }

        public void CancelServer()
        {
            _serverCancellation.Cancel();
        }

        public async Task<NamedPipeClientStream> ConnectAsync()
        {
            var client = DaemonNamedPipe.CreateClient(EndpointName);
            await client.ConnectAsync(CancellationToken);
            return client;
        }

        public async Task<NamedPipeClientStream> ConnectAndHandshakeAsync()
        {
            var client = await ConnectAsync();
            var request = new DaemonHandshakeRequest(Guid.NewGuid());
            await DaemonProtocol.WriteRequestAsync(client, request, CancellationToken);
            var response = Assert.IsType<DaemonHandshakeResponse>(
                await DaemonProtocol.ReadResponseAsync(client, CancellationToken));
            Assert.True(response.Accepted, response.Diagnostic);
            Assert.Equal(request.RequestId, response.RequestId);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            if (!ServerTask.IsCompleted)
            {
                _ = _host.BeginStop(
                    new DaemonStopRequest(Guid.NewGuid()));
            }

            try
            {
                await ServerTask.WaitAsync(CancellationToken);
            }
            catch (OperationCanceledException) when (_serverCancellation.IsCancellationRequested)
            {
            }
            finally
            {
                await _host.DisposeAsync();
                _serverCancellation.Dispose();
                _testTimeout.Dispose();
            }
        }
    }
}
