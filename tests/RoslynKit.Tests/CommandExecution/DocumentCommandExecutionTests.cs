namespace RoslynKit.Tests;

/// <summary>
/// Verifies document text and line command execution against repo and fixture targets.
/// </summary>
public sealed partial class CommandExecutionTests
{
    [Fact]
    public async Task DocumentText_ReadsFullSourceDocument_FromFileSelector()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        var expectedText = File.ReadAllText(programPath);

        var result = await TestPaths.ExecuteCommandAsync<DocumentTextResult>(
            "document-text",
            "--target", TestPaths.SolutionPath(),
            "--file", programPath);

        Assert.Equal("Program.cs", result.Document.Name);
        Assert.Equal(expectedText, result.Text);
        AssertWholeDocumentRange(result.ResolvedRange, expectedText);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task DocumentText_ReadsFullSourceDocument_FromRelativeFileSelector()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, programPath);
        var expectedText = File.ReadAllText(programPath);

        var result = await TestPaths.ExecuteCommandAsync<DocumentTextResult>(
            "document-text",
            "--target", TestPaths.SolutionPath(),
            "--file", relativePath);

        Assert.Equal("Program.cs", result.Document.Name);
        Assert.Equal(expectedText, result.Text);
    }

    [Fact]
    public async Task DocumentLines_ReadsBoundedRange_FromFileSelector()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        var lines = File.ReadAllLines(programPath);
        var lineNumber = Array.FindIndex(lines, line => line.Contains("new CliApplication", StringComparison.Ordinal)) + 1;
        Assert.True(lineNumber > 0);

        var result = await TestPaths.ExecuteCommandAsync<DocumentLinesResult>(
            "document-lines",
            "--target", TestPaths.SolutionPath(),
            "--file", programPath,
            "--start-line", lineNumber.ToString(),
            "--end-line", lineNumber.ToString());

        Assert.Equal("Program.cs", result.Document.Name);
        Assert.Equal(lines[lineNumber - 1], result.Text);
        Assert.Equal(lineNumber, result.Range.Line);
        Assert.Equal(lineNumber, result.Range.EndLine);
        Assert.Equal(1, result.Range.Column);
        Assert.Equal(lines[lineNumber - 1].Length + 1, result.Range.EndColumn);
    }

    [Fact]
    public async Task DocumentLines_CapsOversizedEndLine_ToDocumentEnd()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(TestPaths.SolutionPath(), TestContext.Current.CancellationToken);
        var context = await loaded.FindTextDocumentAsync(programPath, null, null, null, "document-lines", TestContext.Current.CancellationToken);
        var text = await context.TextDocument.GetTextAsync(TestContext.Current.CancellationToken);
        var lastLineNumber = text.Lines.Count;
        var lastTextLine = text.Lines[lastLineNumber - 1];

        var result = await TestPaths.ExecuteCommandAsync<DocumentLinesResult>(
            "document-lines",
            "--target", TestPaths.SolutionPath(),
            "--file", programPath,
            "--start-line", "1",
            "--end-line", "70");

        Assert.Equal("Program.cs", result.Document.Name);
        Assert.Equal(1, result.Range.Line);
        Assert.Equal(lastLineNumber, result.Range.EndLine);
        Assert.Equal(1, result.Range.Column);
        Assert.Equal(lastTextLine.Span.Length + 1, result.Range.EndColumn);
        Assert.Equal(text.ToString(), result.Text);
    }

    [Fact]
    public async Task DocumentLines_RejectsReversedRange()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");

        var exception = await Assert.ThrowsAsync<CliUsageException>(() => TestPaths.ExecuteCommandAsync<DocumentLinesResult>(
            "document-lines",
            "--target", TestPaths.SolutionPath(),
            "--file", programPath,
            "--start-line", "4",
            "--end-line", "3"));

        Assert.Equal("document-lines", exception.CommandName);
        Assert.Contains("greater than or equal to", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.Hint);
    }

    [Fact]
    public async Task DocumentLines_RejectsStartLineBeyondDocumentEnd()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(TestPaths.SolutionPath(), TestContext.Current.CancellationToken);
        var context = await loaded.FindTextDocumentAsync(programPath, null, null, null, "document-lines", TestContext.Current.CancellationToken);
        var text = await context.TextDocument.GetTextAsync(TestContext.Current.CancellationToken);
        var lineBeyondEnd = text.Lines.Count + 1;

        var exception = await Assert.ThrowsAsync<CliUsageException>(() => TestPaths.ExecuteCommandAsync<DocumentLinesResult>(
            "document-lines",
            "--target", TestPaths.SolutionPath(),
            "--file", programPath,
            "--start-line", lineBeyondEnd.ToString(),
            "--end-line", lineBeyondEnd.ToString()));

        Assert.Equal("document-lines", exception.CommandName);
        Assert.Contains($"Line {lineBeyondEnd} is outside the document range", exception.Message, StringComparison.Ordinal);
        var hint = exception.Hint;
        Assert.NotNull(hint);
        Assert.Contains($"--start-line between 1 and {text.Lines.Count}", hint!, StringComparison.Ordinal);
    }
}
