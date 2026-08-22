using System.Text.Json;

namespace RoslynKit.Benchmarking;

/// <summary>
/// Persists and hydrates the canonical schema-versioned benchmark run document.
/// </summary>
internal static class BenchmarkRunStore
{
    public const string FileName = "run.json";

    public const int SchemaVersion = 1;

    public static async Task SaveAsync(
        string runRoot,
        BenchmarkRunDocument document,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(runRoot);
        var destination = Path.Combine(runRoot, FileName);
        var temporary = Path.Combine(runRoot, $".{FileName}.tmp");
        var json = JsonSerializer.Serialize(document, BenchmarkJson.Options) + "\n";
        await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, destination, overwrite: true);
    }

    public static BenchmarkRunDocument Load(string repositoryRoot, string runRoot)
    {
        var path = Path.Combine(runRoot, FileName);
        BenchmarkRunDocument document;
        try
        {
            var json = File.ReadAllText(path);
            document = JsonSerializer.Deserialize<BenchmarkRunDocument>(json, BenchmarkJson.Options)
                ?? throw new BenchmarkException($"Benchmark run document was empty: '{path}'.");
        }
        catch (IOException exception)
        {
            throw new BenchmarkException($"Could not read benchmark run document '{path}'.", exception);
        }
        catch (JsonException exception)
        {
            throw new BenchmarkException($"Benchmark run document was not valid strict JSON: {exception.Message}", exception);
        }

        Validate(repositoryRoot, runRoot, document);
        return document;
    }

    private static void Validate(string repositoryRoot, string runRoot, BenchmarkRunDocument document)
    {
        if (document.SchemaVersion != SchemaVersion)
        {
            throw new BenchmarkException($"Benchmark run document must use schemaVersion {SchemaVersion}.");
        }

        if (document.Cases is null || document.Configuration is null || document.Sessions is null)
        {
            throw new BenchmarkException("Benchmark run document contains a null cases, configuration, or sessions property.");
        }

        BenchmarkCatalog.ValidateCases(repositoryRoot, document.Cases, requireExistingEvidence: false);
        var configuration = document.Configuration;
        if (string.IsNullOrWhiteSpace(configuration.Model)
            || string.IsNullOrWhiteSpace(configuration.ReasoningEffort)
            || string.IsNullOrWhiteSpace(configuration.Case)
            || configuration.Trials is < 1 or > 100
            || configuration.MaximumResults is < 2 or > 50
            || string.IsNullOrWhiteSpace(configuration.RoslynKitPath)
            || document.CreatedAtUtc == default
            || document.UpdatedAtUtc < document.CreatedAtUtc)
        {
            throw new BenchmarkException("Benchmark run document contains invalid configuration.");
        }

        _ = BenchmarkOptionsParser.NormalizeIndexPath(configuration.IndexPath);
        var caseIds = document.Cases.Select(benchmarkCase => benchmarkCase.Id).ToHashSet(StringComparer.Ordinal);
        var keys = new HashSet<BenchmarkSessionKey>();
        foreach (var session in document.Sessions)
        {
            if (session is null)
            {
                throw new BenchmarkException("Benchmark run document contains a null session.");
            }

            var key = new BenchmarkSessionKey(session.CaseId, session.Condition, session.Trial);
            if (!caseIds.Contains(session.CaseId)
                || !BenchmarkConditions.Ordered.Contains(session.Condition, StringComparer.Ordinal)
                || session.Trial < 1
                || session.Trial > configuration.Trials
                || session.RunId != key.RunId
                || session.Model != configuration.Model
                || session.ReasoningEffort != configuration.ReasoningEffort
                || !keys.Add(key))
            {
                throw new BenchmarkException($"Benchmark run document contains invalid or duplicate session '{session.RunId}'.");
            }

            ValidateArtifactPath(runRoot, session.AnswerPath);
            ValidateArtifactPath(runRoot, session.EvidencePath);
            ValidateArtifactPath(runRoot, session.EventPath);
            ValidateArtifactPath(runRoot, session.StderrPath);
        }
    }

    private static void ValidateArtifactPath(string runRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new BenchmarkException($"Benchmark run document contains invalid artifact path '{relativePath}'.");
        }

        var fullRunRoot = Path.GetFullPath(runRoot);
        var fullPath = Path.GetFullPath(Path.Combine(fullRunRoot, relativePath));
        var relative = Path.GetRelativePath(fullRunRoot, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            throw new BenchmarkException($"Benchmark run document contains external artifact path '{relativePath}'.");
        }
    }
}
