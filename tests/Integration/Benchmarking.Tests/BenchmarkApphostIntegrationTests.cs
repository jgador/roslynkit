namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Verifies the Release apphost performs text-only index and search as short-lived CLI commands.
/// </summary>
[Collection("Benchmark apphost integration")]
public sealed class BenchmarkApphostIntegrationTests
{
    [Fact]
    public async Task ReleaseApphost_DefaultTextOnlySearchesUseShortLivedCommands()
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

            var index = await RunApphostAsync(
                processRunner,
                apphost,
                repositoryRoot,
                ["index", "--target", targetPath, "--index-path", indexPath, "--text-only"],
                timeout.Token);
            AssertSuccess(index, "text-only index");
            Assert.Contains("command: index", index.StandardOutput, StringComparison.Ordinal);

            var defaultCases = BenchmarkCatalog.Select(BenchmarkCatalog.Load(repositoryRoot), "default");
            foreach (var benchmarkCase in defaultCases)
            {
                var search = await RunApphostAsync(
                    processRunner,
                    apphost,
                    repositoryRoot,
                    [
                        "search", "--target", targetPath, "--index-path", indexPath,
                        "--query", benchmarkCase.Query, "--max-results", "10",
                        "--text-only", "--compact", "--balanced",
                    ],
                    timeout.Token);
                AssertSuccess(search, $"text-only search for '{benchmarkCase.Id}'");
                Assert.StartsWith("results:", search.StandardOutput, StringComparison.Ordinal);
                Assert.Empty(search.StandardError);
                foreach (var evidenceGroup in benchmarkCase.RequiredEvidenceGroups)
                {
                    Assert.True(
                        evidenceGroup.Any(path => search.StandardOutput.Contains(path, StringComparison.Ordinal)),
                        $"Text-only search for '{benchmarkCase.Id}' did not return any path from required evidence group: {string.Join(", ", evidenceGroup)}.\nOutput:\n{search.StandardOutput}");
                }
            }
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
