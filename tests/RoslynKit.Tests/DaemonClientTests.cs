namespace RoslynKit.Tests;

public sealed class DaemonClientTests
{
    [Fact]
    public async Task ExecuteAsync_UsesRunningDaemonWithoutStartingAnotherProcess()
    {
        var launches = 0;
        var client = CreateClient(
            sendAsync: (_, request, _, _) => Task.FromResult<DaemonResponse?>(
                DaemonCommandResponse.Create(request.RequestId, CliProcessResult.Success("cached"))),
            probeAsync: (_, _, _) => Task.FromResult(true),
            tryAcquireBootstrapLease: _ => throw new InvalidOperationException("Bootstrap was not expected."),
            startDaemon: _ => launches++);

        var result = await client.ExecuteAsync(
            CreateWorkspaceCommand(),
            TestContext.Current.CancellationToken);

        Assert.Equal($"cached{Environment.NewLine}", result.Stdout);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task ExecuteAsync_StartsOnceAfterLeaseAndWaitsForHandshakeReadiness()
    {
        var sendCalls = 0;
        var launches = 0;
        var probesAfterLaunch = 0;
        var readinessDelays = 0;
        var lease = new TestLease();
        var client = CreateClient(
            sendAsync: (_, request, _, _) =>
            {
                sendCalls++;
                return Task.FromResult<DaemonResponse?>(sendCalls == 1
                    ? null
                    : DaemonCommandResponse.Create(request.RequestId, CliProcessResult.Success("started")));
            },
            probeAsync: (_, _, _) => Task.FromResult(
                launches > 0 && ++probesAfterLaunch >= 2),
            tryAcquireBootstrapLease: _ => lease,
            startDaemon: _ => launches++,
            delayAsync: (_, _) =>
            {
                readinessDelays++;
                return Task.CompletedTask;
            });

        var result = await client.ExecuteAsync(
            CreateWorkspaceCommand(),
            TestContext.Current.CancellationToken);

        Assert.Equal($"started{Environment.NewLine}", result.Stdout);
        Assert.Equal(1, launches);
        Assert.Equal(1, readinessDelays);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_RechecksReadinessAfterAcquiringBootstrapLease()
    {
        var sendCalls = 0;
        var probeCalls = 0;
        var launches = 0;
        var lease = new TestLease();
        var client = CreateClient(
            sendAsync: (_, request, _, _) =>
            {
                sendCalls++;
                return Task.FromResult<DaemonResponse?>(sendCalls == 1
                    ? null
                    : DaemonCommandResponse.Create(request.RequestId, CliProcessResult.Success("reused")));
            },
            probeAsync: (_, _, _) => Task.FromResult(++probeCalls >= 2),
            tryAcquireBootstrapLease: _ => lease,
            startDaemon: _ => launches++);

        var result = await client.ExecuteAsync(
            CreateWorkspaceCommand(),
            TestContext.Current.CancellationToken);

        Assert.Equal($"reused{Environment.NewLine}", result.Stdout);
        Assert.Equal(0, launches);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_RetriesOnceWhenDaemonDisappearsAfterReadiness()
    {
        var sendCalls = 0;
        var readinessChecks = 0;
        var client = CreateClient(
            sendAsync: (_, request, _, _) =>
            {
                sendCalls++;
                return Task.FromResult<DaemonResponse?>(sendCalls < 3
                    ? null
                    : DaemonCommandResponse.Create(request.RequestId, CliProcessResult.Success("recovered")));
            },
            probeAsync: (_, _, _) =>
            {
                readinessChecks++;
                return Task.FromResult(true);
            },
            tryAcquireBootstrapLease: _ => throw new InvalidOperationException("Bootstrap was not expected."),
            startDaemon: _ => throw new InvalidOperationException("Launch was not expected."));

        var result = await client.ExecuteAsync(
            CreateWorkspaceCommand(),
            TestContext.Current.CancellationToken);

        Assert.Equal($"recovered{Environment.NewLine}", result.Stdout);
        Assert.Equal(3, sendCalls);
        Assert.Equal(2, readinessChecks);
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrentFirstCallsShareOneStartup()
    {
        var endpointName = $"roslynkit-test-{Guid.NewGuid():N}";
        var initialSends = 0;
        var ready = 0;
        var launches = 0;
        var bothInitialSends = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothReadinessWaiters = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readinessWaiters = 0;
        var client = new DaemonClient(
            (targetPath, _) => Task.FromResult(new DaemonClientEndpoint(endpointName, targetPath)),
            async (_, request, _, cancellationToken) =>
            {
                if (Volatile.Read(ref ready) != 0)
                {
                    return DaemonCommandResponse.Create(
                        request.RequestId,
                        CliProcessResult.Success("shared"));
                }

                if (Interlocked.Increment(ref initialSends) == 2)
                {
                    bothInitialSends.SetResult();
                }

                await bothInitialSends.Task.WaitAsync(cancellationToken);
                return null;
            },
            (_, _, _) => Task.FromResult(Volatile.Read(ref ready) != 0),
            DaemonBootstrapLease.TryAcquire,
            _ =>
            {
                Interlocked.Increment(ref launches);
            },
            TimeProvider.System,
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref readinessWaiters) == 2)
                {
                    Volatile.Write(ref ready, 1);
                    bothReadinessWaiters.SetResult();
                }

                await bothReadinessWaiters.Task.WaitAsync(cancellationToken);
            });

        var first = client.ExecuteAsync(CreateWorkspaceCommand(), TestContext.Current.CancellationToken);
        var second = client.ExecuteAsync(CreateWorkspaceCommand(), TestContext.Current.CancellationToken);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal($"shared{Environment.NewLine}", result.Stdout));
        Assert.Equal(1, launches);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsAfterBoundedReadinessTimeout()
    {
        var timeProvider = new AdvancingTimeProvider();
        var lease = new TestLease();
        var client = new DaemonClient(
            (targetPath, _) => Task.FromResult(new DaemonClientEndpoint("test-endpoint", targetPath)),
            (_, _, _, _) => Task.FromResult<DaemonResponse?>(null),
            (_, _, _) => Task.FromResult(false),
            _ => lease,
            _ => { },
            timeProvider,
            (delay, _) =>
            {
                timeProvider.Advance(delay);
                return Task.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<DaemonClientInfrastructureException>(() => client.ExecuteAsync(
            CreateWorkspaceCommand(),
            TestContext.Current.CancellationToken));

        Assert.Contains("did not become ready within 5 seconds", exception.Message, StringComparison.Ordinal);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringReadinessReleasesBootstrapLease()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var lease = new TestLease();
        var client = CreateClient(
            sendAsync: (_, _, _, _) => Task.FromResult<DaemonResponse?>(null),
            probeAsync: (_, _, _) => Task.FromResult(false),
            tryAcquireBootstrapLease: _ => lease,
            startDaemon: _ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteAsync(
            CreateWorkspaceCommand(),
            cancellation.Token));

        Assert.True(lease.Disposed);
    }

    [Fact]
    public async Task GetStatusAsync_DoesNotAcquireLeaseOrStartAbsentDaemon()
    {
        var client = CreateClient(
            sendAsync: (_, request, _, _) =>
            {
                Assert.IsType<DaemonStatusRequest>(request);
                return Task.FromResult<DaemonResponse?>(null);
            },
            probeAsync: (_, _, _) => throw new InvalidOperationException("Readiness was not expected."),
            tryAcquireBootstrapLease: _ => throw new InvalidOperationException("Bootstrap was not expected."),
            startDaemon: _ => throw new InvalidOperationException("Launch was not expected."));

        var response = await client.GetStatusAsync(
            CreateDaemonCommand("status"),
            TestContext.Current.CancellationToken);

        Assert.Null(response);
    }

    [Fact]
    public async Task StopAsync_SendsStopWithoutStartingDaemon()
    {
        var client = CreateClient(
            sendAsync: (_, request, _, _) =>
            {
                Assert.IsType<DaemonStopRequest>(request);
                return Task.FromResult<DaemonResponse?>(
                    new DaemonStopResponse(
                        RoslynKitBuildInfo.DaemonProtocolVersion,
                        request.RequestId,
                        Stopping: true));
            },
            probeAsync: (_, _, _) => throw new InvalidOperationException("Readiness was not expected."),
            tryAcquireBootstrapLease: _ => throw new InvalidOperationException("Bootstrap was not expected."),
            startDaemon: _ => throw new InvalidOperationException("Launch was not expected."));

        var response = await client.StopAsync(
            CreateDaemonCommand("stop"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.True(response.Stopping);
    }

    [Fact]
    public async Task LifecycleControls_ReportAbsentWhenEndpointIdentityIsUnsupported()
    {
        var client = new DaemonClient(
            (_, _) => throw new DaemonClientInfrastructureException("unsupported"),
            (_, _, _, _) => throw new InvalidOperationException("Send was not expected."),
            (_, _, _) => throw new InvalidOperationException("Readiness was not expected."),
            _ => throw new InvalidOperationException("Bootstrap was not expected."),
            _ => throw new InvalidOperationException("Launch was not expected."),
            TimeProvider.System,
            Task.Delay);

        var status = await client.GetStatusAsync(
            CreateDaemonCommand("status"),
            TestContext.Current.CancellationToken);
        var stop = await client.StopAsync(
            CreateDaemonCommand("stop"),
            TestContext.Current.CancellationToken);

        Assert.Null(status);
        Assert.Null(stop);
    }

    [Fact]
    public async Task DaemonCommandExecutor_FormatsRunningStatusResponse()
    {
        var targetPath = TestPaths.SolutionPath();
        var client = CreateClient(
            sendAsync: (_, request, _, _) => Task.FromResult<DaemonResponse?>(
                new DaemonStatusResponse(
                    RoslynKitBuildInfo.DaemonProtocolVersion,
                    request.RequestId,
                    Running: true,
                    TargetPath: targetPath,
                    ProcessId: 1234,
                    WorkspaceState: "ready",
                    Generation: 7,
                    ActiveRequests: 2,
                    QueuedRequests: 1,
                    Diagnostic: "first\r\nsecond")),
            probeAsync: (_, _, _) => throw new InvalidOperationException("Readiness was not expected."),
            tryAcquireBootstrapLease: _ => throw new InvalidOperationException("Bootstrap was not expected."),
            startDaemon: _ => throw new InvalidOperationException("Launch was not expected."));

        var result = await DaemonCommandExecutor.ExecuteAsync(
            CreateDaemonCommand("status"),
            client,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            $"command: daemon status\n"
            + "state: running\n"
            + $"target: `{targetPath}`\n"
            + "pid: 1234\n"
            + "workspace: ready\n"
            + "generation: 7\n"
            + "active-requests: 2\n"
            + "queued-requests: 1\n"
            + $"diagnostic: first second{Environment.NewLine}",
            result.Stdout);
    }

    private static DaemonClient CreateClient(
        Func<string, DaemonRequest, TimeSpan, CancellationToken, Task<DaemonResponse?>> sendAsync,
        Func<string, TimeSpan, CancellationToken, Task<bool>> probeAsync,
        Func<string, IDisposable?> tryAcquireBootstrapLease,
        Action<string> startDaemon,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        return new DaemonClient(
            (targetPath, _) => Task.FromResult(new DaemonClientEndpoint("test-endpoint", targetPath)),
            sendAsync,
            probeAsync,
            tryAcquireBootstrapLease,
            startDaemon,
            TimeProvider.System,
            delayAsync ?? Task.Delay);
    }

    private static ParsedCommand CreateWorkspaceCommand()
    {
        return CliParser.Parse(
            ["symbols", "--target", TestPaths.SolutionPath(), "--query", "Program"]);
    }

    private static ParsedCommand CreateDaemonCommand(string subcommand)
    {
        return CliParser.Parse(
            ["daemon", subcommand, "--target", TestPaths.SolutionPath()]);
    }

    private sealed class TestLease : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
        }
    }
}
