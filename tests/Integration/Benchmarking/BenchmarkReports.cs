using System.Globalization;
using System.Text;

namespace RoslynKit.Benchmarking;

/// <summary>
/// Derives paired acceptance data, CSV rows, and Markdown from the canonical run document.
/// </summary>
internal static class BenchmarkReports
{
    public const double RequiredSavingsPercent = 20.0;

    public static BenchmarkReportData Create(BenchmarkRunDocument document)
    {
        var sessions = document.Sessions.ToDictionary(
            session => new BenchmarkSessionKey(session.CaseId, session.Condition, session.Trial));
        var pairs = new List<BenchmarkPairResult>();
        foreach (var benchmarkCase in document.Cases.OrderBy(candidate => candidate.Id, StringComparer.Ordinal))
        {
            for (var trial = 1; trial <= document.Configuration.Trials; trial++)
            {
                sessions.TryGetValue(
                    new BenchmarkSessionKey(benchmarkCase.Id, BenchmarkConditions.RawText, trial),
                    out var raw);
                sessions.TryGetValue(
                    new BenchmarkSessionKey(benchmarkCase.Id, BenchmarkConditions.RoslynKitSearch, trial),
                    out var roslynKit);
                var comparable = raw is { Valid: true, Correct: true, Usage.InputTokens: > 0 }
                    && roslynKit is { Valid: true, Correct: true, Usage.InputTokens: > 0 };
                double? savings = null;
                if (comparable)
                {
                    savings = Math.Round(
                        100.0 * (raw!.Usage!.InputTokens - roslynKit!.Usage!.InputTokens) / raw.Usage.InputTokens,
                        4);
                }

                pairs.Add(new BenchmarkPairResult(
                    benchmarkCase.Id,
                    trial,
                    comparable,
                    raw?.Usage?.InputTokens,
                    roslynKit?.Usage?.InputTokens,
                    savings));
            }
        }

        var comparableSavings = pairs
            .Where(pair => pair.Comparable)
            .Select(pair => pair.InputTokenSavingsPercent!.Value)
            .Order()
            .ToArray();
        var accepted = pairs.Count > 0
            && comparableSavings.Length == pairs.Count
            && comparableSavings.All(value => value >= RequiredSavingsPercent);
        return new BenchmarkReportData(
            pairs,
            pairs.Count,
            comparableSavings.Length,
            comparableSavings.FirstOrDefaultNullable(),
            Median(comparableSavings),
            comparableSavings.LastOrDefaultNullable(),
            accepted);
    }

    public static async Task WriteAsync(
        string runRoot,
        BenchmarkRunDocument document,
        CancellationToken cancellationToken)
    {
        var report = Create(document);
        await File.WriteAllTextAsync(
            Path.Combine(runRoot, "runs.csv"),
            RenderCsv(document),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(runRoot, "summary.md"),
            RenderMarkdown(document, report),
            cancellationToken).ConfigureAwait(false);
    }

    internal static string RenderCsv(BenchmarkRunDocument document)
    {
        var lines = new List<string>
        {
            "run_id,case_id,condition,trial,model,reasoning_effort,valid,correct,tool_call_count,exit_code,retrieval_bytes,duration_seconds,input_tokens,cached_input_tokens,cache_write_input_tokens,regular_uncached_input_tokens,output_tokens,reasoning_output_tokens,issues,missing_evidence,answer_path,evidence_path,event_path,stderr_path,retrieval_command",
        };
        foreach (var session in document.Sessions)
        {
            var values = new string?[]
            {
                session.RunId,
                session.CaseId,
                session.Condition,
                session.Trial.ToString(CultureInfo.InvariantCulture),
                session.Model,
                session.ReasoningEffort,
                session.Valid.ToString(CultureInfo.InvariantCulture),
                session.Correct.ToString(CultureInfo.InvariantCulture),
                session.ToolCallCount.ToString(CultureInfo.InvariantCulture),
                session.ExitCode.ToString(CultureInfo.InvariantCulture),
                session.RetrievalBytes.ToString(CultureInfo.InvariantCulture),
                session.DurationSeconds.ToString("0.0000", CultureInfo.InvariantCulture),
                session.Usage?.InputTokens.ToString(CultureInfo.InvariantCulture),
                session.Usage?.CachedInputTokens.ToString(CultureInfo.InvariantCulture),
                session.Usage?.CacheWriteInputTokens?.ToString(CultureInfo.InvariantCulture),
                session.Usage?.RegularUncachedInputTokens?.ToString(CultureInfo.InvariantCulture),
                session.Usage?.OutputTokens.ToString(CultureInfo.InvariantCulture),
                session.Usage?.ReasoningOutputTokens.ToString(CultureInfo.InvariantCulture),
                string.Join("; ", session.Issues),
                string.Join("; ", session.MissingEvidence.Select(group => string.Join('|', group))),
                session.AnswerPath,
                session.EvidencePath,
                session.EventPath,
                session.StderrPath,
                session.RetrievalCommand,
            };
            lines.Add(string.Join(',', values.Select(EscapeCsv)));
        }

        return string.Join('\n', lines) + "\n";
    }

    internal static string RenderMarkdown(BenchmarkRunDocument document, BenchmarkReportData report)
    {
        var lines = new List<string>
        {
            "# Search-text benchmark",
            string.Empty,
            $"Model: {document.Configuration.Model}",
            $"Reasoning effort: {document.Configuration.ReasoningEffort}",
            $"LLM judgments: {document.Sessions.Count}",
            $"Comparable pairs: {report.ComparablePairCount}/{report.ExpectedPairCount}",
            $"Minimum input-token savings: {FormatPercent(report.MinimumSavingsPercent)}",
            $"Median input-token savings: {FormatPercent(report.MedianSavingsPercent)}",
            $"Maximum input-token savings: {FormatPercent(report.MaximumSavingsPercent)}",
            $"Every scheduled pair was valid, correct, and saved at least 20%: {(report.Accepted ? "yes" : "no")}",
            string.Empty,
            "| Case | Trial | Raw input | RoslynKit input | Savings | Comparable |",
            "| --- | ---: | ---: | ---: | ---: | --- |",
        };
        foreach (var pair in report.Pairs)
        {
            lines.Add(
                $"| {pair.CaseId} | {pair.Trial} | {FormatValue(pair.RawInputTokens)} | "
                + $"{FormatValue(pair.RoslynKitInputTokens)} | {FormatPercent(pair.InputTokenSavingsPercent)} | "
                + $"{(pair.Comparable ? "yes" : "no")} |");
        }

        lines.Add(string.Empty);
        lines.Add("A pair is comparable only when both sessions are operationally valid and contain every required production/test evidence group.");
        return string.Join('\n', lines) + "\n";
    }

    private static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var middle = values.Count / 2;
        return values.Count % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2;
    }

    private static string EscapeCsv(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string FormatValue(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string FormatPercent(double? value) =>
        value is null ? "-" : $"{value.Value.ToString("0.00", CultureInfo.InvariantCulture)}%";

    private static double? FirstOrDefaultNullable(this IReadOnlyList<double> values) =>
        values.Count == 0 ? null : values[0];

    private static double? LastOrDefaultNullable(this IReadOnlyList<double> values) =>
        values.Count == 0 ? null : values[^1];
}
