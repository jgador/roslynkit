namespace RoslynKit.Tests;

/// <summary>
/// Verifies RoslynKit command execution against the repository solution and the fixture workspace.
/// </summary>
public sealed class CommandExecutionTests
{
    [Fact]
    public async Task Workspace_DefaultOutput_ListsRepoRelevantSourceDocumentsOnly()
    {
        var result = await TestPaths.ExecuteCommandAsync<WorkspaceResult>(
            "workspace",
            "--target", TestPaths.FixtureProjectPath());

        Assert.NotEmpty(result.Documents);
        Assert.All(result.Documents, document => Assert.Equal(DocumentKindNames.Source, document.DocumentKind));
        Assert.DoesNotContain(result.Documents, document => string.Equals(document.Name, "notes.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Documents, document => string.Equals(document.Name, ".editorconfig", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Workspace_IncludeFlags_AddGeneratedAdditionalAndAnalyzerConfigDocuments()
    {
        var result = await TestPaths.ExecuteCommandAsync<WorkspaceResult>(
            "workspace",
            "--target", TestPaths.FixtureProjectPath(),
            "--include-generated",
            "--include-additional",
            "--include-analyzer-config");

        Assert.Contains(result.Documents, document => document.DocumentKind == DocumentKindNames.SourceGenerated);
        Assert.Contains(result.Documents, document => document.DocumentKind == DocumentKindNames.Additional && document.Name == "notes.txt");
        Assert.Contains(result.Documents, document => document.DocumentKind == DocumentKindNames.AnalyzerConfig && document.Name == ".editorconfig");
    }

    [Fact]
    public async Task DocumentText_ReadsMethodBodySlice_FromFileSelector()
    {
        var executorPath = TestPaths.RepoFile("src", "RoslynKit", "RoslynCommandExecutor.cs");
        var (startLine, _) = TestPaths.FindLineAndColumn(executorPath, "public static async Task<object> ExecuteAsync(");
        var (nextMethodLine, _) = TestPaths.FindLineAndColumn(executorPath, "private static async Task<object> WorkspaceAsync(");

        var result = await TestPaths.ExecuteCommandAsync<DocumentTextResult>(
            "document-text",
            "--target", TestPaths.SolutionPath(),
            "--file", executorPath,
            "--start-line", startLine.ToString(),
            "--end-line", (nextMethodLine - 1).ToString());

        Assert.Equal("RoslynCommandExecutor.cs", result.Document.Name);
        Assert.Equal(startLine, result.ResolvedRange.Line);
        Assert.Contains("return command.Name switch", result.Text, StringComparison.Ordinal);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Definition_ReturnsCliApplicationConstructorDeclaration()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        var (line, column) = TestPaths.FindLineAndColumn(programPath, "CliApplication");

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
    public async Task QuickInfo_ReturnsConstructorSections_ForCliApplicationInstantiation()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        var (line, column) = TestPaths.FindLineAndColumn(programPath, "CliApplication");

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

    [Fact]
    public async Task DocumentText_DocumentKey_ReadsGeneratedDocument()
    {
        var workspace = await TestPaths.ExecuteCommandAsync<WorkspaceResult>(
            "workspace",
            "--target", TestPaths.FixtureProjectPath(),
            "--include-generated");
        var generatedDocument = workspace.Documents.First(document => document.DocumentKind == DocumentKindNames.SourceGenerated);

        var result = await TestPaths.ExecuteCommandAsync<DocumentTextResult>(
            "document-text",
            "--target", TestPaths.FixtureProjectPath(),
            "--document-key", generatedDocument.DocumentKey);

        Assert.Equal(DocumentKindNames.SourceGenerated, result.Document.DocumentKind);
        Assert.Contains("HelloRegex", result.Text, StringComparison.Ordinal);
    }
}
