namespace RoslynKit.Tests;

public sealed class ProgramTests
{
    [Fact]
    public async Task CreateCliApplication_ExecutesOrdinaryWorkspaceCommandWithoutInfrastructureWarning()
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
            $"error: usage\nmessage: The '--target' path '{missingTarget}' does not exist. Pass an existing solution, project, or repository directory.{Environment.NewLine}",
            stdout.ToString());
        Assert.Empty(stderr.ToString());
    }
}
