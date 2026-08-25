namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Verifies direct RoslynKit command construction.
/// </summary>
public sealed class BenchmarkCommandTests
{
    [Fact]
    public void Search_InvokesAppHostDirectlyWithTextOnlyArguments()
    {
        var invocation = BenchmarkCommands.Search(
            "/repo",
            "/repo/artifacts/bin/RoslynKit/release/RoslynKit",
            "./artifacts/index.db",
            BenchmarkTestData.Case(query: "daemon transport"),
            12);

        Assert.Equal("/repo/artifacts/bin/RoslynKit/release/RoslynKit", invocation.FileName);
        Assert.Equal(
            [
                "search", "--target", "./RoslynKit.slnx", "--index-path", "./artifacts/index.db",
                "--query", "daemon transport", "--max-results", "12", "--text-only", "--compact", "--balanced",
            ],
            invocation.Arguments);
        Assert.DoesNotContain("dotnet", invocation.Arguments);
    }

    [Fact]
    public void BenchmarkCommands_DoesNotExposeCodexConstruction()
    {
        var commandMembers = typeof(BenchmarkCommands)
            .GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.DoesNotContain(commandMembers, member => member.Name.Contains("Codex", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveAppHost_UsesReleaseArtifactsLayout()
    {
        var path = BenchmarkPaths.ResolveAppHost("/repo", null).Replace('\\', '/');

        Assert.EndsWith(
            OperatingSystem.IsWindows()
                ? "/artifacts/bin/RoslynKit/release/RoslynKit.exe"
                : "/artifacts/bin/RoslynKit/release/RoslynKit",
            path,
            StringComparison.Ordinal);
    }
}
