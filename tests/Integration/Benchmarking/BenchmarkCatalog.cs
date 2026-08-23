using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoslynKit.Benchmarking;

/// <summary>
/// Loads and validates the checked-in benchmark case catalog.
/// </summary>
internal static partial class BenchmarkCatalog
{
    public static BenchmarkCase[] Load(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, "tests", "Integration", "Benchmarking", "cases.json");
        BenchmarkCatalogDocument document;
        try
        {
            var json = File.ReadAllText(path);
            document = JsonSerializer.Deserialize<BenchmarkCatalogDocument>(json, BenchmarkJson.Options)
                ?? throw new BenchmarkException($"Benchmark catalog was empty: '{path}'.");
        }
        catch (IOException exception)
        {
            throw new BenchmarkException($"Could not read benchmark catalog '{path}'.", exception);
        }
        catch (JsonException exception)
        {
            throw new BenchmarkException($"Benchmark catalog was not valid strict JSON: {exception.Message}", exception);
        }

        if (document.Cases is null)
        {
            throw new BenchmarkException("Benchmark catalog property 'cases' must be an array.");
        }

        ValidateCases(repositoryRoot, document.Cases);
        if (!document.Cases.Any(benchmarkCase => benchmarkCase.IsDefault))
        {
            throw new BenchmarkException("Benchmark catalog must contain at least one default case.");
        }

        return document.Cases;
    }

    public static BenchmarkCase[] Select(IReadOnlyList<BenchmarkCase> cases, string caseId)
    {
        if (caseId == "all")
        {
            return [.. cases];
        }

        if (caseId == "default")
        {
            var defaultCases = cases.Where(candidate => candidate.IsDefault).ToArray();
            if (defaultCases.Length == 0)
            {
                throw new BenchmarkException("Benchmark catalog does not define any default cases.");
            }

            return defaultCases;
        }

        var selected = cases.Where(candidate => candidate.Id == caseId).ToArray();
        if (selected.Length != 1)
        {
            throw new BenchmarkException($"Unknown benchmark case: '{caseId}'.");
        }

        return selected;
    }

    public static void ValidateCases(
        string repositoryRoot,
        IReadOnlyList<BenchmarkCase> cases,
        bool requireExistingEvidence = true)
    {
        if (cases is null || cases.Count == 0)
        {
            throw new BenchmarkException("Benchmark catalog must contain one or more cases.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var benchmarkCase in cases)
        {
            if (benchmarkCase is null)
            {
                throw new BenchmarkException("Benchmark catalog cases must be objects.");
            }

            if (!CaseIdPattern().IsMatch(benchmarkCase.Id))
            {
                throw new BenchmarkException($"Benchmark case ID '{benchmarkCase.Id}' must be a lowercase kebab-case identifier.");
            }

            if (!ids.Add(benchmarkCase.Id))
            {
                throw new BenchmarkException($"Benchmark case ID '{benchmarkCase.Id}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(benchmarkCase.Intent) || string.IsNullOrWhiteSpace(benchmarkCase.Query))
            {
                throw new BenchmarkException($"Benchmark case '{benchmarkCase.Id}' must contain non-empty intent and query text.");
            }

            if (benchmarkCase.RequiredEvidenceGroups is null || benchmarkCase.RequiredEvidenceGroups.Length == 0)
            {
                throw new BenchmarkException($"Benchmark case '{benchmarkCase.Id}' must contain at least one evidence group.");
            }

            foreach (var group in benchmarkCase.RequiredEvidenceGroups)
            {
                if (group is null || group.Length == 0)
                {
                    throw new BenchmarkException($"Benchmark case '{benchmarkCase.Id}' contains an empty evidence group.");
                }

                foreach (var path in group)
                {
                    if (path is null)
                    {
                        throw new BenchmarkException($"Benchmark case '{benchmarkCase.Id}' contains a null evidence path.");
                    }

                    ValidateEvidencePath(repositoryRoot, benchmarkCase.Id, path, requireExistingEvidence);
                }
            }
        }
    }

    private static void ValidateEvidencePath(
        string repositoryRoot,
        string caseId,
        string path,
        bool requireExistingEvidence)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains('\\', StringComparison.Ordinal)
            || !path.EndsWith(".cs", StringComparison.Ordinal)
            || !(path.StartsWith("src/RoslynKit/", StringComparison.Ordinal)
                || path.StartsWith("tests/RoslynKit.Tests/", StringComparison.Ordinal)))
        {
            throw new BenchmarkException($"Benchmark case '{caseId}' contains invalid evidence path '{path}'.");
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new BenchmarkException($"Benchmark case '{caseId}' contains invalid evidence path '{path}'.");
        }

        var repositoryPath = Path.GetFullPath(repositoryRoot);
        var fullPath = Path.GetFullPath(Path.Combine(repositoryPath, path));
        var relativePath = Path.GetRelativePath(repositoryPath, fullPath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal)
            || (requireExistingEvidence && !File.Exists(fullPath)))
        {
            throw new BenchmarkException($"Benchmark case '{caseId}' references missing or external evidence path '{path}'.");
        }
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CaseIdPattern();
}
