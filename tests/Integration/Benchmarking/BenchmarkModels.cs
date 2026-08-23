namespace RoslynKit.Benchmarking;

/// <summary>
/// Names the two retrieval conditions compared by the benchmark.
/// </summary>
internal static class BenchmarkConditions
{
    public const string RawText = "raw-text";

    public const string RoslynKitSearch = "roslynkit-search";

    public static readonly IReadOnlyList<string> Ordered = [RawText, RoslynKitSearch];
}

/// <summary>
/// Describes the checked-in benchmark case catalog.
/// </summary>
internal sealed record BenchmarkCatalogDocument
{
    public int SchemaVersion { get; init; }

    public BenchmarkCase[] Cases { get; init; } = [];
}

/// <summary>
/// Describes one retrieval intent and the source evidence required from its answer.
/// </summary>
internal sealed record BenchmarkCase
{
    public string Id { get; init; } = string.Empty;

    public bool IsDefault { get; init; }

    public string Intent { get; init; } = string.Empty;

    public string Query { get; init; } = string.Empty;

    public string[][] RequiredEvidenceGroups { get; init; } = [];
}

/// <summary>
/// Captures the immutable settings used for one benchmark run.
/// </summary>
internal sealed record BenchmarkRunConfiguration
{
    public string Model { get; init; } = string.Empty;

    public string ReasoningEffort { get; init; } = string.Empty;

    public int Trials { get; init; }

    public string Case { get; init; } = string.Empty;

    public int MaximumResults { get; init; }

    public string IndexPath { get; init; } = string.Empty;

    public string RoslynKitPath { get; init; } = string.Empty;

    public bool BuildRoslynKit { get; init; }
}

/// <summary>
/// Stores the canonical, resumable benchmark state.
/// </summary>
internal sealed record BenchmarkRunDocument
{
    public int SchemaVersion { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public BenchmarkRunConfiguration Configuration { get; init; } = new();

    public BenchmarkCase[] Cases { get; init; } = [];

    public List<BenchmarkSessionResult> Sessions { get; init; } = [];
}

/// <summary>
/// Holds validated token accounting from one terminal Codex event.
/// </summary>
internal sealed record TokenUsage
{
    public long InputTokens { get; init; }

    public long CachedInputTokens { get; init; }

    public long? CacheWriteInputTokens { get; init; }

    public long UncachedInputTokens { get; init; }

    public long? RegularUncachedInputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long ReasoningOutputTokens { get; init; }
}

/// <summary>
/// Records one measured judge session and its audit artifacts.
/// </summary>
internal sealed record BenchmarkSessionResult
{
    public string RunId { get; init; } = string.Empty;

    public string CaseId { get; init; } = string.Empty;

    public string Condition { get; init; } = string.Empty;

    public int Trial { get; init; }

    public string Model { get; init; } = string.Empty;

    public string ReasoningEffort { get; init; } = string.Empty;

    public bool Valid { get; init; }

    public bool Correct { get; init; }

    public string[][] MissingEvidence { get; init; } = [];

    public string[] Issues { get; init; } = [];

    public int ToolCallCount { get; init; }

    public string RetrievalCommand { get; init; } = string.Empty;

    public long RetrievalBytes { get; init; }

    public int ExitCode { get; init; }

    public double DurationSeconds { get; init; }

    public TokenUsage? Usage { get; init; }

    public string AnswerPath { get; init; } = string.Empty;

    public string EvidencePath { get; init; } = string.Empty;

    public string EventPath { get; init; } = string.Empty;

    public string StderrPath { get; init; } = string.Empty;
}

/// <summary>
/// Identifies one scheduled case, condition, and trial tuple.
/// </summary>
internal sealed record BenchmarkSessionKey(string CaseId, string Condition, int Trial)
{
    public string RunId => $"{CaseId}-{Condition}-trial{Trial}";
}

/// <summary>
/// Represents one raw-text and RoslynKit result pair in generated reports.
/// </summary>
internal sealed record BenchmarkPairResult(
    string CaseId,
    int Trial,
    bool Comparable,
    long? RawInputTokens,
    long? RoslynKitInputTokens,
    double? InputTokenSavingsPercent);

/// <summary>
/// Summarizes strict benchmark acceptance across all scheduled pairs.
/// </summary>
internal sealed record BenchmarkReportData(
    IReadOnlyList<BenchmarkPairResult> Pairs,
    int ExpectedPairCount,
    int ComparablePairCount,
    double? MinimumSavingsPercent,
    double? MedianSavingsPercent,
    double? MaximumSavingsPercent,
    bool Accepted);
