using System.Text.Json;

namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Creates compact benchmark records and isolated repositories for focused tests.
/// </summary>
internal static class BenchmarkTestData
{
    public static BenchmarkCase Case(
        string id = "sample-case",
        string query = "alpha beta",
        string[][]? evidenceGroups = null)
    {
        return new BenchmarkCase
        {
            Id = id,
            Intent = "Find the relevant production and test declarations.",
            Query = query,
            RequiredEvidenceGroups = evidenceGroups ?? [["src/RoslynKit/Alpha.cs"], ["tests/RoslynKit.Tests/AlphaTests.cs"]],
        };
    }

    public static BenchmarkRunDocument Document(
        int trials = 1,
        IEnumerable<BenchmarkSessionResult>? sessions = null,
        BenchmarkCase[]? cases = null)
    {
        return new BenchmarkRunDocument
        {
            SchemaVersion = BenchmarkRunStore.SchemaVersion,
            CreatedAtUtc = DateTimeOffset.Parse("2026-08-22T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            UpdatedAtUtc = DateTimeOffset.Parse("2026-08-22T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            Configuration = new BenchmarkRunConfiguration
            {
                Model = "gpt-5.6-sol",
                ReasoningEffort = "high",
                Trials = trials,
                Case = "all",
                MaximumResults = 10,
                IndexPath = "./artifacts/test.db",
                RoslynKitPath = "/tmp/RoslynKit",
                BuildRoslynKit = true,
            },
            Cases = cases ?? [Case()],
            Sessions = sessions?.ToList() ?? [],
        };
    }

    public static BenchmarkSessionResult Session(
        string condition,
        int trial,
        long inputTokens,
        bool valid = true,
        bool correct = true)
    {
        var key = new BenchmarkSessionKey("sample-case", condition, trial);
        return new BenchmarkSessionResult
        {
            RunId = key.RunId,
            CaseId = key.CaseId,
            Condition = key.Condition,
            Trial = trial,
            Model = "gpt-5.6-sol",
            ReasoningEffort = "high",
            Valid = valid,
            Correct = correct,
            RetrievalCommand = "retrieval",
            RetrievalBytes = 100,
            ExitCode = 0,
            DurationSeconds = 1,
            Usage = new TokenUsage
            {
                InputTokens = inputTokens,
                CachedInputTokens = 0,
                UncachedInputTokens = inputTokens,
                OutputTokens = 10,
                ReasoningOutputTokens = 2,
            },
            AnswerPath = $"answers/{key.RunId}.md",
            EvidencePath = $"evidence/{key.RunId}.txt",
            EventPath = $"events/{key.RunId}.jsonl",
            StderrPath = $"stderr/{key.RunId}.txt",
        };
    }

    public static string CatalogJson(BenchmarkCase benchmarkCase, string extraProperty = "")
    {
        var document = new BenchmarkCatalogDocument
        {
            SchemaVersion = BenchmarkCatalog.SchemaVersion,
            Cases = [benchmarkCase],
        };
        var json = JsonSerializer.Serialize(document, BenchmarkJson.Options);
        return string.IsNullOrEmpty(extraProperty)
            ? json
            : json.Insert(json.LastIndexOf('}'), $",\n  \"{extraProperty}\": true\n");
    }
}

/// <summary>
/// Owns an isolated disposable directory shaped like the benchmark repository root.
/// </summary>
internal sealed class TemporaryBenchmarkRepository : IDisposable
{
    public TemporaryBenchmarkRepository()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "roslynkit-benchmark-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        Write("RoslynKit.slnx", "<Solution />\n");
    }

    public string RootPath { get; }

    public void Write(string relativePath, string contents)
    {
        var path = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
