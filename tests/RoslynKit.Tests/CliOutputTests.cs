using System.Reflection;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies the top-level CLI output contracts: markdown help, plain-text version output, and the
/// plain-text error shape with non-zero exit codes.
/// </summary>
public sealed class CliOutputTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsBufferedStreams_WithoutWritingThem()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var expected = new CliProcessResult(17, "buffered stdout", "buffered stderr");
        var application = new CliApplication(
            stdout,
            stderr,
            (_, _) => Task.FromResult(expected));

        var result = await application.ExecuteAsync(
            ["symbols", "--target", "missing.slnx", "--query", "Foo"],
            TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_WritesBufferedStreams_ToSeparateWriters()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var expected = new CliProcessResult(17, "buffered stdout", "buffered stderr");
        var application = new CliApplication(
            stdout,
            stderr,
            (_, _) => Task.FromResult(expected));

        var exitCode = await application.RunAsync(
            ["symbols", "--target", "missing.slnx", "--query", "Foo"],
            TestContext.Current.CancellationToken);

        Assert.Equal(17, exitCode);
        Assert.Equal(expected.Stdout, stdout.ToString());
        Assert.Equal(expected.Stderr, stderr.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_FormatsCancellation_FromWorkspaceRouter()
    {
        var application = new CliApplication(
            TextWriter.Null,
            TextWriter.Null,
            (_, _) => throw new OperationCanceledException());

        var result = await application.ExecuteAsync(
            ["symbols", "--target", "missing.slnx", "--query", "Foo"],
            TestContext.Current.CancellationToken);

        Assert.Equal(130, result.ExitCode);
        Assert.Equal($"error: canceled\nmessage: Operation was canceled.{Environment.NewLine}", result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Fact]
    public async Task ExecuteAsync_FormatsUnexpectedException_FromWorkspaceRouter()
    {
        var application = new CliApplication(
            TextWriter.Null,
            TextWriter.Null,
            (_, _) => throw new InvalidOperationException("test failure"));

        var result = await application.ExecuteAsync(
            ["symbols", "--target", "missing.slnx", "--query", "Foo"],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal($"error: InvalidOperationException\nmessage: test failure{Environment.NewLine}", result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Fact]
    public async Task RunAsync_WritesMarkdownHelp_ForHelpCommand()
    {
        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(["help"], TestContext.Current.CancellationToken);

        var output = writer.ToString();

        Assert.Equal(0, exitCode);
        Assert.StartsWith("tool: roslynkit", output, StringComparison.Ordinal);
        Assert.Contains("- command: `version` description: ", output, StringComparison.Ordinal);
        Assert.Contains("- command: `init` description: ", output, StringComparison.Ordinal);
        Assert.Contains("- command: `symbols` description: ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"data\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WritesCommandHelp_ForHelpWithCommandArgument()
    {
        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(["help", "symbols"], TestContext.Current.CancellationToken);

        var output = writer.ToString();

        Assert.Equal(0, exitCode);
        Assert.StartsWith("command: symbols\ndescription: ", output.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("usage: `roslynkit symbols ", output, StringComparison.Ordinal);
        Assert.Contains("- option: `--query` short: `-q` value: text required: true description: ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WritesCommandHelp_ForDocumentTextPathSelector()
    {
        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(["help", "document-text"], TestContext.Current.CancellationToken);

        var output = writer.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("--file <path>", output, StringComparison.Ordinal);
        Assert.Contains("- option: `--project`", output, StringComparison.Ordinal);
        Assert.Contains("- option: `--tfm`", output, StringComparison.Ordinal);
        Assert.Contains("- option: `--document-kind`", output, StringComparison.Ordinal);
        Assert.DoesNotContain("--document-key", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WritesCommandHelp_ForTopLevelVersionHelp()
    {
        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(["--version", "--help"], TestContext.Current.CancellationToken);

        var output = writer.ToString();

        Assert.Equal(0, exitCode);
        Assert.StartsWith("command: version", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WritesCommandHelp_ForInit()
    {
        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(["help", "init"], TestContext.Current.CancellationToken);

        var output = writer.ToString();

        Assert.Equal(0, exitCode);
        Assert.StartsWith("command: init", output, StringComparison.Ordinal);
        Assert.Contains("usage: `roslynkit init [--agent <codex|claude|copilot|all>] [--overwrite]`", output, StringComparison.Ordinal);
        Assert.Contains("- option: `--agent` value: agent description: ", output, StringComparison.Ordinal);
        Assert.Contains("- option: `--overwrite` description: ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WritesPlainText_ForVersionCommand()
    {
        await AssertVersionOutputAsync("version");
    }

    [Fact]
    public async Task RunAsync_WritesPlainText_ForTopLevelVersionFlag()
    {
        await AssertVersionOutputAsync("--version");
    }

    [Fact]
    public async Task RunAsync_WritesPlainTextUsageError_ForInvalidSymbolKind()
    {
        var output = await AssertUsageErrorAsync(["symbols", "--target", "missing.slnx", "--query", "Foo", "--kind", "banana"]);

        Assert.Contains("Unknown symbol kind 'banana'", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WritesPlainTextUsageError_ForVersionCommandWithUnexpectedPositional()
    {
        var output = await AssertUsageErrorAsync(["version", "extra"]);

        Assert.Contains("Unexpected positional argument 'extra'", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WritesPlainTextUsageError_ForMissingDocumentSelector()
    {
        var output = await AssertUsageErrorAsync(["document-text", "--target", "missing.slnx"]);

        Assert.Contains("Missing required option '--file'.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WritesPlainTextUsageError_ForDocumentTextLegacyRangeOption()
    {
        var output = await AssertUsageErrorAsync(["document-text", "--target", "missing.slnx", "--file", "Program.cs", "--start-line", "999"]);

        Assert.Contains("Option '--start-line' is no longer supported.", output, StringComparison.Ordinal);
        Assert.Contains("reads the entire resolved document only", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WritesUsageErrorHint_ForLineBeyondDocumentEnd()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");

        var output = await AssertUsageErrorAsync([
            "quick-info",
            "--target", TestPaths.SolutionPath(),
            "--file", programPath,
            "--line", "70",
            "--column", "1",
        ], expectedLineCount: 3);

        Assert.Contains("Line 70 is outside the document range 1..16.", output, StringComparison.Ordinal);
        Assert.Contains("hint: Retry with --line between 1 and 16", output, StringComparison.Ordinal);
        Assert.Contains("document-lines", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WritesUsageErrorHint_ForColumnBeyondLineEnd()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        var (line, _) = TestPaths.FindLineAndColumn(programPath, "return new CliApplication");

        var output = await AssertUsageErrorAsync([
            "quick-info",
            "--target", TestPaths.SolutionPath(),
            "--file", programPath,
            "--line", line.ToString(),
            "--column", "200",
        ], expectedLineCount: 3);

        Assert.Contains("Column 200 is outside the line range", output, StringComparison.Ordinal);
        Assert.Contains("hint: Retry with --column between 1 and", output, StringComparison.Ordinal);
        Assert.Contains($"for line {line}", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_OmitsUsageErrorHint_WhenNoRetryGuidanceExists()
    {
        var output = await AssertUsageErrorAsync(["symbols", "--query", "Foo"]);

        Assert.Equal("error: usage\nmessage: Missing required option '--target'.", NormalizeErrorOutput(output));
    }

    private static async Task<string> AssertUsageErrorAsync(string[] args, int expectedLineCount = 2)
    {
        using var writer = new StringWriter();
        using var errorWriter = new StringWriter();
        var exitCode = await new CliApplication(writer, errorWriter).RunAsync(args, TestContext.Current.CancellationToken);

        var output = writer.ToString();
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');

        Assert.Equal(2, exitCode);
        Assert.Equal(expectedLineCount, lines.Length);
        Assert.Equal("error: usage", lines[0]);
        Assert.StartsWith("message: ", lines[1], StringComparison.Ordinal);
        if (expectedLineCount == 3)
        {
            Assert.StartsWith("hint: ", lines[2], StringComparison.Ordinal);
        }

        Assert.DoesNotContain("{", output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, errorWriter.ToString());
        return output;
    }

    private static async Task AssertVersionOutputAsync(params string[] args)
    {
        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(args, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(GetExpectedVersionOutput() + Environment.NewLine, writer.ToString());
    }

    private static string GetExpectedVersionOutput()
    {
        var assembly = typeof(CliApplication).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = !string.IsNullOrWhiteSpace(informationalVersion)
            ? informationalVersion
            : assembly.GetName().Version?.ToString() ?? "unknown";
        return $"roslynkit version {version}";
    }

    private static string NormalizeErrorOutput(string output)
    {
        return output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
    }

}
