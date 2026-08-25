namespace RoslynKit.Benchmarking;

/// <summary>
/// Renders the identical tool-free judge instruction around each retrieval condition.
/// </summary>
internal static class BenchmarkPrompt
{
    public static string Render(string condition, BenchmarkCase benchmarkCase, string evidence)
    {
        if (!BenchmarkConditions.Ordered.Contains(condition, StringComparer.Ordinal))
        {
            throw new BenchmarkException($"Unsupported benchmark condition: '{condition}'.");
        }

        return string.Join(
            '\n',
            $"Search-retrieval benchmark condition: {condition}.",
            "Do not use tools or outside knowledge. Judge only the supplied search text.",
            "Return at most six declarations as `path:line — declaration — relevance`; include production and focused test evidence.",
            "Cover the orchestration entry point, supporting implementation, and focused tests when those distinct roles appear in the intent.",
            "Choose only relevant evidence and stop when those roles are covered.",
            string.Empty,
            $"Intent: {benchmarkCase.Intent}",
            string.Empty,
            "Search text:",
            evidence);
    }
}
