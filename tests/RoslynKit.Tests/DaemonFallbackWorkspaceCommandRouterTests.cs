namespace RoslynKit.Tests;

public sealed class DaemonFallbackWorkspaceCommandRouterTests
{
    private const string DaemonUnavailableWarning = "warning: daemon unavailable; executing standalone";

    [Fact]
    public async Task ExecuteAsync_ReturnsDaemonResultWithoutExecutingStandalone()
    {
        var daemonResult = new CliProcessResult(17, "daemon stdout", "daemon stderr");
        var standaloneCalls = 0;

        var result = await DaemonFallbackWorkspaceCommandRouter.ExecuteAsync(
            CreateWorkspaceCommand(),
            (_, _) => Task.FromResult(daemonResult),
            (_, _) =>
            {
                standaloneCalls++;
                return Task.FromResult(CliProcessResult.Success("standalone"));
            },
            TestContext.Current.CancellationToken);

        Assert.Same(daemonResult, result);
        Assert.Equal(0, standaloneCalls);
    }

    [Fact]
    public async Task ExecuteAsync_FallsBackOnceAndPrependsWarningToStandaloneStderr()
    {
        var daemonCalls = 0;
        var standaloneCalls = 0;
        var standaloneResult = new CliProcessResult(17, "standalone stdout", "standalone stderr");

        var result = await DaemonFallbackWorkspaceCommandRouter.ExecuteAsync(
            CreateWorkspaceCommand(),
            (_, _) =>
            {
                daemonCalls++;
                return Task.FromException<CliProcessResult>(new DaemonClientInfrastructureException("unavailable"));
            },
            (_, _) =>
            {
                standaloneCalls++;
                return Task.FromResult(standaloneResult);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, daemonCalls);
        Assert.Equal(1, standaloneCalls);
        Assert.Equal(standaloneResult.ExitCode, result.ExitCode);
        Assert.Equal(standaloneResult.Stdout, result.Stdout);
        Assert.Equal($"{DaemonUnavailableWarning}{Environment.NewLine}{standaloneResult.Stderr}", result.Stderr);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesDaemonCancellationWithoutExecutingStandalone()
    {
        var standaloneCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DaemonFallbackWorkspaceCommandRouter.ExecuteAsync(
                CreateWorkspaceCommand(),
                (_, _) => Task.FromException<CliProcessResult>(new OperationCanceledException()),
                (_, _) =>
                {
                    standaloneCalls++;
                    return Task.FromResult(CliProcessResult.Success("standalone"));
                },
                TestContext.Current.CancellationToken));

        Assert.Equal(0, standaloneCalls);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesUnexpectedDaemonExceptionWithoutExecutingStandalone()
    {
        var standaloneCalls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DaemonFallbackWorkspaceCommandRouter.ExecuteAsync(
                CreateWorkspaceCommand(),
                (_, _) => Task.FromException<CliProcessResult>(new InvalidOperationException("unexpected")),
                (_, _) =>
                {
                    standaloneCalls++;
                    return Task.FromResult(CliProcessResult.Success("standalone"));
                },
                TestContext.Current.CancellationToken));

        Assert.Equal(0, standaloneCalls);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesAlreadyCanceledCallerTokenAfterInfrastructureFailure()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var standaloneCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DaemonFallbackWorkspaceCommandRouter.ExecuteAsync(
                CreateWorkspaceCommand(),
                (_, _) => Task.FromException<CliProcessResult>(new DaemonClientInfrastructureException("unavailable")),
                (_, _) =>
                {
                    standaloneCalls++;
                    return Task.FromResult(CliProcessResult.Success("standalone"));
                },
                cancellation.Token));

        Assert.Equal(0, standaloneCalls);
    }

    [Fact]
    public async Task ExecuteAsync_BuffersStandaloneExceptionWithWarning()
    {
        var result = await DaemonFallbackWorkspaceCommandRouter.ExecuteAsync(
            CreateWorkspaceCommand(),
            (_, _) => Task.FromException<CliProcessResult>(new DaemonClientInfrastructureException("unavailable")),
            (_, _) => Task.FromException<CliProcessResult>(new InvalidOperationException("standalone failure")),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            $"error: InvalidOperationException\nmessage: standalone failure{Environment.NewLine}",
            result.Stdout);
        Assert.Equal(DaemonUnavailableWarning + Environment.NewLine, result.Stderr);
    }

    [Fact]
    public async Task ExecuteAsync_BuffersStandaloneCancellationWithWarning()
    {
        var result = await DaemonFallbackWorkspaceCommandRouter.ExecuteAsync(
            CreateWorkspaceCommand(),
            (_, _) => Task.FromException<CliProcessResult>(new DaemonClientInfrastructureException("unavailable")),
            (_, _) => Task.FromException<CliProcessResult>(new OperationCanceledException()),
            TestContext.Current.CancellationToken);

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(
            $"error: canceled\nmessage: Operation was canceled.{Environment.NewLine}",
            result.Stdout);
        Assert.Equal(DaemonUnavailableWarning + Environment.NewLine, result.Stderr);
    }

    private static ParsedCommand CreateWorkspaceCommand()
    {
        return CliParser.Parse(
            ["symbols", "--target", TestPaths.SolutionPath(), "--query", "Program"]);
    }
}
