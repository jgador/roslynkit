namespace RoslynKit.Tests;

public sealed class ProgramTests
{
    [Fact]
    public async Task RunAsync_InternalDaemonModeBypassesPublicCliParser()
    {
        string? daemonTarget = null;
        var publicCliCalled = false;

        var exitCode = await Program.RunAsync(
            [DaemonServerRunner.InternalModeToken, "--target", "target.slnx"],
            (targetPath, _) =>
            {
                daemonTarget = targetPath;
                return Task.FromResult(17);
            },
            (_, _) =>
            {
                publicCliCalled = true;
                return Task.FromResult(0);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(17, exitCode);
        Assert.Equal("target.slnx", daemonTarget);
        Assert.False(publicCliCalled);
    }

    [Fact]
    public async Task RunAsync_OrdinaryArgumentsUsePublicCli()
    {
        IReadOnlyList<string>? publicArguments = null;

        var exitCode = await Program.RunAsync(
            ["help"],
            (_, _) => Task.FromResult(1),
            (arguments, _) =>
            {
                publicArguments = arguments;
                return Task.FromResult(23);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(23, exitCode);
        Assert.Equal(["help"], publicArguments);
    }

    [Fact]
    public async Task RunAsync_InvalidInternalArgumentsDoNotUseEitherRunner()
    {
        var daemonCalled = false;
        var publicCliCalled = false;

        var exitCode = await Program.RunAsync(
            [DaemonServerRunner.InternalModeToken, "--wrong", "target.slnx"],
            (_, _) =>
            {
                daemonCalled = true;
                return Task.FromResult(0);
            },
            (_, _) =>
            {
                publicCliCalled = true;
                return Task.FromResult(0);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.False(daemonCalled);
        Assert.False(publicCliCalled);
    }

    [Fact]
    public async Task CreateCliApplication_RoutesDaemonInfrastructureFailureToStandalone()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var missingTarget = Path.Combine(
            Path.GetTempPath(),
            $"roslynkit-phase12-{Guid.NewGuid():N}",
            "Missing.csproj");
        var application = Program.CreateCliApplication(stdout, stderr);

        var exitCode = await application.RunAsync(
            ["symbols", "--target", missingTarget, "--query", "Missing"],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Equal(
            $"error: usage\nmessage: Target file '{missingTarget}' does not exist.{Environment.NewLine}",
            stdout.ToString());
        Assert.Equal(
            $"warning: daemon unavailable; executing standalone{Environment.NewLine}",
            stderr.ToString());
    }
}
