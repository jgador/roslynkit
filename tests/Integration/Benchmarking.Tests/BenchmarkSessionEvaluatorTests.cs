namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Verifies judge validity and required evidence-group matching.
/// </summary>
public sealed class BenchmarkSessionEvaluatorTests
{
    private const string UsageEvent =
        "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":100,\"cached_input_tokens\":0,\"output_tokens\":10,\"reasoning_output_tokens\":4}}\n";

    [Fact]
    public void Evaluate_AcceptsOneCandidateFromEveryEvidenceGroup()
    {
        var benchmarkCase = BenchmarkTestData.Case(evidenceGroups:
        [
            ["src/RoslynKit/One.cs", "src/RoslynKit/Alternative.cs"],
            ["tests/RoslynKit.Tests/OneTests.cs"],
        ]);

        var result = Evaluate(
            benchmarkCase,
            UsageEvent,
            "src\\RoslynKit\\Alternative.cs:10 and tests/RoslynKit.Tests/OneTests.cs:20");

        Assert.True(result.Valid);
        Assert.True(result.Correct);
        Assert.Empty(result.MissingEvidence);
    }

    [Fact]
    public void Evaluate_RejectsToolUseAndMissingEvidence()
    {
        var events = """
            {"type":"item.completed","item":{"id":"tool-1","type":"command_execution","command":"rg alpha","status":"completed","exit_code":0}}
            {"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":10,"reasoning_output_tokens":4}}
            """;

        var result = Evaluate(BenchmarkTestData.Case(), events, "src/RoslynKit/Alpha.cs:1");

        Assert.False(result.Valid);
        Assert.False(result.Correct);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Single(result.MissingEvidence);
    }

    private static BenchmarkSessionResult Evaluate(
        BenchmarkCase benchmarkCase,
        string events,
        string answer)
    {
        var key = new BenchmarkSessionKey(benchmarkCase.Id, BenchmarkConditions.RawText, 1);
        return BenchmarkSessionEvaluator.Evaluate(
            benchmarkCase,
            key,
            BenchmarkTestData.Document(cases: [benchmarkCase]).Configuration,
            new ProcessResult(0, events, string.Empty),
            answer,
            100,
            1,
            "retrieval",
            "answers/run.md",
            "evidence/run.txt",
            "events/run.jsonl",
            "stderr/run.txt");
    }
}
