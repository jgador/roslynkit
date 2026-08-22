namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Verifies direct apphost and isolated Codex command construction.
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
    public void Codex_UsesHostConfigurationJsonEphemeralStdinAndRemovesParentThread()
    {
        var invocation = BenchmarkCommands.Codex("/repo", "model", "high", "/answer.md", "judge this");

        Assert.Equal("codex", invocation.FileName);
        Assert.Equal("judge this", invocation.StandardInput);
        Assert.Equal("-", invocation.Arguments[^1]);
        Assert.Contains("--json", invocation.Arguments);
        Assert.Contains("--ephemeral", invocation.Arguments);
        Assert.DoesNotContain("--ignore-user-config", invocation.Arguments);
        Assert.Contains("--ignore-rules", invocation.Arguments);
        Assert.Contains("shell_tool", invocation.Arguments);
        Assert.Contains("multi_agent_v2", invocation.Arguments);
        var removedVariables = Assert.IsAssignableFrom<IReadOnlyList<string>>(invocation.RemovedEnvironmentVariables);
        Assert.Equal(["CODEX_THREAD_ID"], removedVariables);
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
