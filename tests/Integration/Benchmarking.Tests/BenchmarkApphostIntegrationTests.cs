namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Verifies the Release apphost performs text-only index and search without starting a daemon.
/// </summary>
[Collection("Benchmark apphost integration")]
public sealed class BenchmarkApphostIntegrationTests
{
    [Fact]
    public async Task ReleaseApphost_TextOnlyIndexAndSearchDoNotStartDaemon()
    {
        var repositoryRoot = BenchmarkPaths.FindRepositoryRoot(AppContext.BaseDirectory);
        var processRunner = new ProcessRunner();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var build = await processRunner.RunAsync(
            BenchmarkCommands.BuildRoslynKit(repositoryRoot),
            timeout.Token);
        AssertSuccess(build, "Release RoslynKit build");

        var apphost = BenchmarkPaths.ResolveAppHost(repositoryRoot, null);
        BenchmarkPaths.ValidateAppHost(apphost);
        var temporaryRoot = Path.Combine(
            repositoryRoot,
            "artifacts",
            "benchmark-integration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var targetPath = Path.Combine(temporaryRoot, "BenchmarkTarget.slnx");
            var indexPath = Path.Combine(temporaryRoot, "index.db");
            File.Copy(Path.Combine(repositoryRoot, "RoslynKit.slnx"), targetPath);
            var initialStatus = await RunApphostAsync(
                processRunner,
                apphost,
                repositoryRoot,
                ["daemon", "status", "--target", targetPath],
                timeout.Token);
            AssertSuccess(initialStatus, "initial daemon status");
            Assert.Contains("state: not-running", initialStatus.StandardOutput, StringComparison.Ordinal);

            var index = await RunApphostAsync(
                processRunner,
                apphost,
                repositoryRoot,
                ["index", "--target", targetPath, "--index-path", indexPath, "--text-only"],
                timeout.Token);
            AssertSuccess(index, "text-only index");
            Assert.Contains("command: index", index.StandardOutput, StringComparison.Ordinal);

            var search = await RunApphostAsync(
                processRunner,
                apphost,
                repositoryRoot,
                [
                    "search", "--target", targetPath, "--index-path", indexPath,
                    "--query", "search query tokenizer", "--max-results", "4",
                    "--text-only", "--compact", "--balanced",
                ],
                timeout.Token);
            AssertSuccess(search, "text-only search");
            Assert.StartsWith("results:", search.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("workspace daemon", search.StandardError, StringComparison.OrdinalIgnoreCase);

            var finalStatus = await RunApphostAsync(
                processRunner,
                apphost,
                repositoryRoot,
                ["daemon", "status", "--target", targetPath],
                timeout.Token);
            AssertSuccess(finalStatus, "final daemon status");
            Assert.Contains("state: not-running", finalStatus.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static Task<ProcessResult> RunApphostAsync(
        IProcessRunner processRunner,
        string apphost,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        return processRunner.RunAsync(
            new ProcessInvocation(apphost, workingDirectory, arguments),
            cancellationToken);
    }

    private static void AssertSuccess(ProcessResult result, string operation)
    {
        Assert.True(
            result.ExitCode == 0,
            $"{operation} failed with exit code {result.ExitCode}.\nstdout:\n{result.StandardOutput}\nstderr:\n{result.StandardError}");
    }
}

/// <summary>
/// Prevents concurrent Release apphost builds within this test assembly.
/// </summary>
[CollectionDefinition("Benchmark apphost integration", DisableParallelization = true)]
public sealed class BenchmarkApphostIntegrationCollection;
