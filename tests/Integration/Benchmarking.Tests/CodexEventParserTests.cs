namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Verifies strict JSON Lines accounting and tool-free judge enforcement.
/// </summary>
public sealed class CodexEventParserTests
{
    [Fact]
    public void Parse_UsesOneTerminalUsageEventAndDerivesUncachedTokens()
    {
        var events = """
            {"type":"thread.started","thread_id":"thread-1"}
            {"type":"turn.started"}
            {"type":"item.completed","item":{"id":"item-1","type":"agent_message","text":"answer"}}
            {"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":40,"cache_write_input_tokens":50,"output_tokens":10,"reasoning_output_tokens":4}}
            """;

        var result = CodexEventParser.Parse(events);

        Assert.Empty(result.Issues);
        Assert.Equal(0, result.ToolCallCount);
        Assert.Equal(60, result.Usage!.UncachedInputTokens);
        Assert.Equal(10, result.Usage.RegularUncachedInputTokens);
    }

    [Fact]
    public void Parse_RejectsMalformedAndMultipleTerminalEvents()
    {
        var events = """
            {"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":10,"reasoning_output_tokens":4}}
            {
            {"type":"turn.completed","usage":{"input_tokens":90,"cached_input_tokens":0,"output_tokens":10,"reasoning_output_tokens":4}}
            """;

        var result = CodexEventParser.Parse(events);

        Assert.Contains(result.Issues, issue => issue.Contains("line 2", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => issue.Contains("2 terminal usage events", StringComparison.Ordinal));
        Assert.Null(result.Usage);
    }

    [Fact]
    public void Parse_RejectsSecondTerminalEventWithoutUsage()
    {
        var events = """
            {"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":10,"reasoning_output_tokens":4}}
            {"type":"turn.completed"}
            """;

        var result = CodexEventParser.Parse(events);

        Assert.Contains(result.Issues, issue => issue.Contains("2 terminal usage events", StringComparison.Ordinal));
        Assert.Null(result.Usage);
    }

    [Fact]
    public void Parse_CountsOneToolAcrossStartedAndCompletedEvents()
    {
        var events = """
            {"type":"item.started","item":{"id":"tool-1","type":"command_execution","command":"pwd","status":"in_progress"}}
            {"type":"item.completed","item":{"id":"tool-1","type":"command_execution","command":"pwd","status":"completed","exit_code":0}}
            {"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":10,"reasoning_output_tokens":4}}
            """;

        var result = CodexEventParser.Parse(events);

        Assert.Equal(1, result.ToolCallCount);
    }

    [Fact]
    public void Parse_ClassifiesFailedErrorEventsWithoutToolUse()
    {
        var events = """
            {"type":"error","message":"authentication failed"}
            {"type":"item.completed","item":{"id":"error-1","type":"error","message":"authentication failed"}}
            {"type":"turn.failed","error":{"message":"authentication failed"}}
            """;

        var result = CodexEventParser.Parse(events);

        Assert.Equal(0, result.ToolCallCount);
        Assert.Null(result.Usage);
        Assert.Equal(["event log did not contain terminal token accounting"], result.Issues);
    }
}
