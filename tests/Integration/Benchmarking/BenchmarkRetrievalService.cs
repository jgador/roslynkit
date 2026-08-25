namespace RoslynKit.Benchmarking;

/// <summary>
/// Retrieves either bounded raw text or one compact text-only RoslynKit result.
/// </summary>
internal sealed class BenchmarkRetrievalService(IProcessRunner processRunner)
{
    public async Task<RetrievalResult> RetrieveAsync(
        string condition,
        string repositoryRoot,
        BenchmarkRunConfiguration configuration,
        BenchmarkCase benchmarkCase,
        CancellationToken cancellationToken)
    {
        if (condition == BenchmarkConditions.RawText)
        {
            return new RetrievalResult(
                RawTextRetriever.Retrieve(repositoryRoot, benchmarkCase),
                "controller plain-text ranked excerpt search");
        }

        if (condition != BenchmarkConditions.RoslynKitSearch)
        {
            throw new BenchmarkException($"Unsupported benchmark condition: '{condition}'.");
        }

        var invocation = BenchmarkCommands.Search(
            repositoryRoot,
            configuration.RoslynKitPath,
            configuration.IndexPath,
            benchmarkCase,
            configuration.MaximumResults);
        var result = await processRunner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new BenchmarkException(
                $"Direct RoslynKit search failed ({result.ExitCode}): {result.StandardError.Trim()}");
        }

        if (!result.StandardOutput.StartsWith("results:", StringComparison.Ordinal))
        {
            throw new BenchmarkException("Direct RoslynKit search did not return compact ranked search text.");
        }

        return new RetrievalResult(result.StandardOutput.TrimEnd(), BenchmarkCommands.Display(invocation));
    }
}

/// <summary>
/// Contains retrieved evidence and its auditable controller command.
/// </summary>
internal sealed record RetrievalResult(string Evidence, string Command);
