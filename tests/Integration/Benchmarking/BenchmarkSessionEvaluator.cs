namespace RoslynKit.Benchmarking;

/// <summary>
/// Applies operational, tool-free, and evidence-group validity rules to one judge session.
/// </summary>
internal static class BenchmarkSessionEvaluator
{
    public static BenchmarkSessionResult Evaluate(
        BenchmarkCase benchmarkCase,
        BenchmarkSessionKey key,
        BenchmarkRunConfiguration configuration,
        ProcessResult processResult,
        string answer,
        long retrievalBytes,
        double durationSeconds,
        string retrievalCommand,
        string answerPath,
        string evidencePath,
        string eventPath,
        string stderrPath)
    {
        var eventLog = CodexEventParser.Parse(processResult.StandardOutput);
        var issues = eventLog.Issues.ToList();
        if (processResult.ExitCode != 0)
        {
            issues.Add($"codex exited with {processResult.ExitCode}");
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            issues.Add("answer was empty");
        }

        if (eventLog.ToolCallCount > 0)
        {
            issues.Add("LLM judge used tools instead of judging only the supplied search text");
        }

        var missingEvidence = GetMissingEvidence(answer, benchmarkCase);
        return new BenchmarkSessionResult
        {
            RunId = key.RunId,
            CaseId = key.CaseId,
            Condition = key.Condition,
            Trial = key.Trial,
            Model = configuration.Model,
            ReasoningEffort = configuration.ReasoningEffort,
            Valid = issues.Count == 0,
            Correct = missingEvidence.Length == 0,
            MissingEvidence = missingEvidence,
            Issues = [.. issues],
            ToolCallCount = eventLog.ToolCallCount,
            RetrievalCommand = retrievalCommand,
            RetrievalBytes = retrievalBytes,
            ExitCode = processResult.ExitCode,
            DurationSeconds = Math.Round(durationSeconds, 4),
            Usage = eventLog.Usage,
            AnswerPath = answerPath,
            EvidencePath = evidencePath,
            EventPath = eventPath,
            StderrPath = stderrPath,
        };
    }

    public static string[][] GetMissingEvidence(string answer, BenchmarkCase benchmarkCase)
    {
        var normalizedAnswer = answer.Replace('\\', '/');
        return benchmarkCase.RequiredEvidenceGroups
            .Where(group => !group.Any(candidate => normalizedAnswer.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
            .Select(group => group.ToArray())
            .ToArray();
    }
}
