namespace RoslynKit.Tests;

/// <summary>
/// Verifies semantic command execution against repo and fixture targets.
/// </summary>
public sealed partial class CommandExecutionTests
{
    [Fact]
    public async Task Definition_ReturnsCliApplicationConstructorDeclaration()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        var (line, markerColumn) = TestPaths.FindLineAndColumn(programPath, "new CliApplication");
        var column = markerColumn + "new ".Length;

        var result = await TestPaths.ExecuteCommandAsync<DefinitionResult>(
            "definition",
            "--target", TestPaths.SolutionPath(),
            "--file", programPath,
            "--line", line.ToString(),
            "--column", column.ToString());

        Assert.Equal(".ctor", result.Symbol.Name);
        Assert.Equal("RoslynKit.CliApplication", result.Symbol.ContainingType);
        Assert.EndsWith(Path.Combine("src", "RoslynKit", "CliApplication.cs"), result.Symbol.PrimaryLocation?.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Definition_ReturnsDocumentationForCliApplicationRunAsync()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        var (line, column) = TestPaths.FindLineAndColumn(programPath, "RunAsync(args, cancellationToken)");

        var result = await TestPaths.ExecuteCommandAsync<DefinitionResult>(
            "definition",
            "--target", TestPaths.SolutionPath(),
            "--file", programPath,
            "--line", line.ToString(),
            "--column", column.ToString());

        Assert.Equal("RunAsync", result.Symbol.Name);
        Assert.Contains("Processes one command and writes its buffered standard output", result.Symbol.Documentation!, StringComparison.Ordinal);
        Assert.Contains(
            "\n  documentation: Processes one command and writes its buffered standard output",
            MarkdownProjection.Render(result).Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuickInfo_ReturnsConstructorSections_ForCliApplicationInstantiation()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        var (line, markerColumn) = TestPaths.FindLineAndColumn(programPath, "new CliApplication");
        var column = markerColumn + "new ".Length;

        var result = await TestPaths.ExecuteCommandAsync<QuickInfoResult>(
            "quick-info",
            "--target", TestPaths.SolutionPath(),
            "--file", programPath,
            "--line", line.ToString(),
            "--column", column.ToString());

        Assert.NotEmpty(result.Sections);
        Assert.Contains(result.Sections, section => section.Text.Contains("CliApplication.CliApplication", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QuickInfo_LineBeyondDocumentEnd_HasRetryHint()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        var lineCount = File.ReadAllLines(programPath).Length + 1;
        var requestedLine = lineCount + 1;

        var exception = await Assert.ThrowsAsync<CliUsageException>(() => TestPaths.ExecuteCommandAsync<QuickInfoResult>(
            "quick-info",
            "--target", TestPaths.SolutionPath(),
            "--file", programPath,
            "--line", requestedLine.ToString(),
            "--column", "1"));

        Assert.Equal("quick-info", exception.CommandName);
        Assert.Equal($"Line {requestedLine} is outside the document range 1..{lineCount}.", exception.Message);
        var hint = exception.Hint;
        Assert.NotNull(hint);
        Assert.Contains($"--line between 1 and {lineCount}", hint!, StringComparison.Ordinal);
        Assert.Contains("document-lines", hint!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuickInfo_ColumnBeyondLineEnd_HasRetryHint()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        var (line, _) = TestPaths.FindLineAndColumn(programPath, "return new CliApplication");

        var exception = await Assert.ThrowsAsync<CliUsageException>(() => TestPaths.ExecuteCommandAsync<QuickInfoResult>(
            "quick-info",
            "--target", TestPaths.SolutionPath(),
            "--file", programPath,
            "--line", line.ToString(),
            "--column", "200"));

        Assert.Equal("quick-info", exception.CommandName);
        Assert.Contains("Column 200 is outside the line range", exception.Message, StringComparison.Ordinal);
        var hint = exception.Hint;
        Assert.NotNull(hint);
        Assert.Contains("--column between 1 and", hint!, StringComparison.Ordinal);
        Assert.Contains($"for line {line}", hint!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentSymbols_ReturnsDocumentationForProgram()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");

        var result = await TestPaths.ExecuteCommandAsync<DocumentSymbolsResult>(
            "document-symbols",
            "--target", TestPaths.SolutionPath(),
            "--file", programPath);

        var programSymbol = Assert.Single(result.Symbols, symbol => symbol.Name == "Program");
        Assert.Contains("Runs the ordinary RoslynKit command-line application", programSymbol.Documentation!, StringComparison.Ordinal);
        Assert.Contains(
            "\n  documentation: Runs the ordinary RoslynKit command-line application",
            MarkdownProjection.Render(result).Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypeDefinition_ReturnsFixtureInterfaceType()
    {
        var sourcePath = TestPaths.RepoFile("tests", "FixtureWorkspace", "App", "Source.cs");
        var (line, column) = TestPaths.FindLineAndColumn(sourcePath, "source = _source");

        var result = await TestPaths.ExecuteCommandAsync<TypeDefinitionResult>(
            "type-definition",
            "--target", TestPaths.FixtureProjectPath(),
            "--file", sourcePath,
            "--line", line.ToString(),
            "--column", column.ToString());

        Assert.Equal("IMessageSource", result.Symbol.Name);
        Assert.EndsWith(Path.Combine("tests", "FixtureWorkspace", "App", "Source.cs"), result.Symbol.PrimaryLocation?.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TypeDefinition_ReturnsDocumentationForParsedCommand()
    {
        var applicationPath = TestPaths.RepoFile("src", "RoslynKit", "CliApplication.cs");
        var (line, column) = TestPaths.FindLineAndColumn(applicationPath, "command = CliParser.Parse");

        var result = await TestPaths.ExecuteCommandAsync<TypeDefinitionResult>(
            "type-definition",
            "--target", TestPaths.SolutionPath(),
            "--file", applicationPath,
            "--line", line.ToString(),
            "--column", column.ToString());

        Assert.Equal("ParsedCommand", result.Symbol.Name);
        Assert.Contains("Represents a parsed RoslynKit invocation", result.Symbol.Documentation!, StringComparison.Ordinal);
        Assert.Contains(
            "\n  documentation: Represents a parsed RoslynKit invocation",
            MarkdownProjection.Render(result).Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Implementations_ReturnsFixtureImplementation()
    {
        var sourcePath = TestPaths.RepoFile("tests", "FixtureWorkspace", "App", "Source.cs");
        var (line, column) = TestPaths.FindLineAndColumn(sourcePath, "IMessageSource _source");

        var result = await TestPaths.ExecuteCommandAsync<ImplementationsResult>(
            "implementations",
            "--target", TestPaths.FixtureProjectPath(),
            "--file", sourcePath,
            "--line", line.ToString(),
            "--column", column.ToString());

        Assert.Contains(result.Symbols, symbol => symbol.Name == "GeneratedMessageSource");
    }

    [Fact]
    public async Task Implementations_ReturnsDocumentationForDisposableImplementation()
    {
        var result = await TestPaths.ExecuteCommandAsync<ImplementationsResult>(
            "implementations",
            "--target", TestPaths.SolutionPath(),
            "--symbol", "T:System.IDisposable");

        var implementation = Assert.Single(result.Symbols, symbol => symbol.Name == "RoslynWorkspaceLoader");
        Assert.Contains("Loads an MSBuild workspace", implementation.Documentation!, StringComparison.Ordinal);
        Assert.Contains(
            "\n  documentation: Loads an MSBuild workspace",
            MarkdownProjection.Render(result).Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignatureHelp_ReturnsConstructorSignature()
    {
        var parserPath = TestPaths.RepoFile("src", "RoslynKit", "CliParser.cs");
        var marker = "new CliUsageException(";
        var (line, column) = TestPaths.FindLineAndColumn(parserPath, marker, columnOffset: marker.Length);

        var result = await TestPaths.ExecuteCommandAsync<SignatureHelpResult>(
            "signature-help",
            "--target", TestPaths.SolutionPath(),
            "--file", parserPath,
            "--line", line.ToString(),
            "--column", column.ToString());

        Assert.NotEmpty(result.Signatures);
        Assert.Contains(result.Signatures, signature => signature.Label.Contains("CliUsageException", StringComparison.Ordinal));
        Assert.Contains(result.Signatures.SelectMany(signature => signature.Parameters), parameter => parameter.Name == "commandName");
        Assert.Contains(result.Signatures.SelectMany(signature => signature.Parameters), parameter => parameter.Name == "message");
    }
}
