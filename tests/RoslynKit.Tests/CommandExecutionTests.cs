namespace RoslynKit.Tests;

/// <summary>
/// Verifies workspace-backed command handlers such as definition, quick info, and document reads against the repo and fixture targets.
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
    public async Task Workspace_RendersRootContainedDocumentPathsRelative()
    {
        var result = await TestPaths.ExecuteCommandAsync<WorkspaceResult>(
            "workspace",
            "--target", TestPaths.FixtureProjectPath());

        var source = Assert.Single(result.Documents, document => document.Name == "Source.cs");

        Assert.Equal(Path.Combine("tests", "FixtureWorkspace", "App", "Source.cs"), source.DisplayPath);
        Assert.Equal(Path.Combine("tests", "FixtureWorkspace", "App", "App.csproj"), source.DisplayProjectPath);
    }

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

    [Fact]
    public async Task Definition_ReturnsCliApplicationConstructorDeclaration()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        // Occurrence 2 is the `new CliApplication(...)` constructor call; occurrence 1 is the type
        // reference inside the XML doc <see cref="..."/>, which resolves to the type, not the constructor.
        var (line, column) = TestPaths.FindLineAndColumn(programPath, "CliApplication", occurrence: 2);

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
        var (line, column) = TestPaths.FindLineAndColumn(programPath, "RunAsync(args)");

        var result = await TestPaths.ExecuteCommandAsync<DefinitionResult>(
            "definition",
            "--target", TestPaths.SolutionPath(),
            "--file", programPath,
            "--line", line.ToString(),
            "--column", column.ToString());

        Assert.Equal("RunAsync", result.Symbol.Name);
        Assert.Contains("Parses arguments, dispatches help or command execution", result.Symbol.Documentation!, StringComparison.Ordinal);
        Assert.Contains(
            "\n  documentation: Parses arguments, dispatches help or command execution",
            MarkdownProjection.Render(result).Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuickInfo_ReturnsConstructorSections_ForCliApplicationInstantiation()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        // Occurrence 2 is the `new CliApplication(...)` constructor call; occurrence 1 is the type
        // reference inside the XML doc <see cref="..."/>, which resolves to the type, not the constructor.
        var (line, column) = TestPaths.FindLineAndColumn(programPath, "CliApplication", occurrence: 2);

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

        var exception = await Assert.ThrowsAsync<CliUsageException>(() => TestPaths.ExecuteCommandAsync<QuickInfoResult>(
            "quick-info",
            "--target", TestPaths.SolutionPath(),
            "--file", programPath,
            "--line", "70",
            "--column", "1"));

        Assert.Equal("quick-info", exception.CommandName);
        Assert.Equal("Line 70 is outside the document range 1..16.", exception.Message);
        var hint = exception.Hint;
        Assert.NotNull(hint);
        Assert.Contains("--line between 1 and 16", hint!, StringComparison.Ordinal);
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
        Assert.Contains("Forwards the RoslynKit console entry point", programSymbol.Documentation!, StringComparison.Ordinal);
        Assert.Contains(
            "\n  documentation: Forwards the RoslynKit console entry point",
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

    [Fact]
    public async Task DocumentText_FileSelector_ReadsFullGeneratedDocument()
    {
        var workspace = await TestPaths.ExecuteCommandAsync<WorkspaceResult>(
            "workspace",
            "--target", TestPaths.FixtureProjectPath(),
            "--include-generated");
        var generatedDocument = workspace.Documents.First(document => document.DocumentKind == DocumentKindNames.SourceGenerated);
        Assert.NotNull(generatedDocument.Path);
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(TestPaths.FixtureProjectPath(), TestContext.Current.CancellationToken);
        var context = await loaded.FindTextDocumentAsync(generatedDocument.Path, null, null, DocumentKindNames.SourceGenerated, "document-text", TestContext.Current.CancellationToken);
        var expectedText = (await context.TextDocument.GetTextAsync(TestContext.Current.CancellationToken)).ToString();

        var result = await TestPaths.ExecuteCommandAsync<DocumentTextResult>(
            "document-text",
            "--target", TestPaths.FixtureProjectPath(),
            "--file", generatedDocument.Path!,
            "--document-kind", DocumentKindNames.SourceGenerated);

        Assert.Equal(DocumentKindNames.SourceGenerated, result.Document.DocumentKind);
        Assert.Equal(expectedText, result.Text);
        AssertWholeDocumentRange(result.ResolvedRange, expectedText);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task DocumentText_AmbiguousFilePath_ListsProjectTfmKindAndPath()
    {
        var fixture = CreateAmbiguousPathFixture();

        var exception = await Assert.ThrowsAsync<CliUsageException>(() => TestPaths.ExecuteCommandAsync<DocumentTextResult>(
            "document-text",
            "--target", fixture.SolutionPath,
            "--file", fixture.SharedSourcePath));

        Assert.Equal("document-text", exception.CommandName);
        Assert.Contains("multiple document contexts", exception.Message, StringComparison.Ordinal);
        var hint = exception.Hint;
        Assert.NotNull(hint);
        Assert.Contains("--project", hint!, StringComparison.Ordinal);
        Assert.Contains("--tfm", hint!, StringComparison.Ordinal);
        Assert.Contains("--document-kind", hint!, StringComparison.Ordinal);
        Assert.Contains("ProjectA.csproj", hint!, StringComparison.Ordinal);
        Assert.Contains("ProjectB.csproj", hint!, StringComparison.Ordinal);
        Assert.Contains("net10.0", hint!, StringComparison.Ordinal);
        Assert.Contains("netstandard2.1", hint!, StringComparison.Ordinal);
        Assert.Contains("Shared.cs", hint!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentText_ContextOptions_DisambiguateFilePath()
    {
        var fixture = CreateAmbiguousPathFixture();

        var projectResult = await TestPaths.ExecuteCommandAsync<DocumentTextResult>(
            "document-text",
            "--target", fixture.SolutionPath,
            "--file", fixture.SharedSourcePath,
            "--project", fixture.ProjectAPath);
        var tfmResult = await TestPaths.ExecuteCommandAsync<DocumentTextResult>(
            "document-text",
            "--target", fixture.SolutionPath,
            "--file", fixture.SharedSourcePath,
            "--tfm", "netstandard2.1");

        Assert.Equal("ProjectA", projectResult.Document.ProjectName);
        Assert.Equal("net10.0", projectResult.Document.TargetFramework);
        Assert.Equal("ProjectB", tfmResult.Document.ProjectName);
        Assert.Equal("netstandard2.1", tfmResult.Document.TargetFramework);
    }

    [Fact]
    public async Task References_BySymbolSelector_FindsPositionResolverCallers()
    {
        var result = await TestPaths.ExecuteCommandAsync<ReferencesResult>(
            "references",
            "--target", TestPaths.SolutionPath(),
            "--symbol", "RoslynKit.PositionResolver.GetPositionAsync");

        Assert.Null(result.Document);
        Assert.Null(result.Line);
        Assert.Null(result.Column);
        Assert.Equal("RoslynKit.PositionResolver.GetPositionAsync", result.Selector);
        Assert.StartsWith("M:RoslynKit.PositionResolver.GetPositionAsync(", result.Symbol.SymbolId, StringComparison.Ordinal);
        Assert.Contains("Validates one-based CLI coordinates", result.Symbol.Documentation!, StringComparison.Ordinal);
        Assert.True(result.Locations.Count >= 3);
        Assert.Contains(result.Locations, location => location.Path?.EndsWith("RoslynCommandExecutor.cs", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task Definition_ByDocIdSelector_ReturnsType()
    {
        var result = await TestPaths.ExecuteCommandAsync<DefinitionResult>(
            "definition",
            "--target", TestPaths.SolutionPath(),
            "--symbol", "T:RoslynKit.PositionResolver");

        Assert.Null(result.Document);
        Assert.Null(result.Line);
        Assert.Equal("T:RoslynKit.PositionResolver", result.Selector);
        Assert.Equal("PositionResolver", result.Symbol.Name);
        Assert.Equal("T:RoslynKit.PositionResolver", result.Symbol.SymbolId);
        Assert.EndsWith(Path.Combine("src", "RoslynKit", "PositionResolver.cs"), result.Symbol.PrimaryLocation?.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Implementations_BySymbolSelector_ReturnsFixtureImplementation()
    {
        var result = await TestPaths.ExecuteCommandAsync<ImplementationsResult>(
            "implementations",
            "--target", TestPaths.FixtureProjectPath(),
            "--symbol", "FixtureApp.IMessageSource");

        Assert.Null(result.Document);
        Assert.Equal("FixtureApp.IMessageSource", result.Selector);
        Assert.Contains(result.Symbols, symbol => symbol.Name == "GeneratedMessageSource");
    }

    [Fact]
    public async Task References_AmbiguousQualifiedName_ListsCandidateDocIds()
    {
        var exception = await Assert.ThrowsAsync<CliUsageException>(() => TestPaths.ExecuteCommandAsync<ReferencesResult>(
            "references",
            "--target", TestPaths.SolutionPath(),
            "--symbol", "RoslynKit.SymbolItem.FromSymbol"));

        Assert.Equal("references", exception.CommandName);
        Assert.Contains("ambiguous", exception.Message, StringComparison.Ordinal);
        Assert.Contains("M:RoslynKit.SymbolItem.FromSymbol(Microsoft.CodeAnalysis.ISymbol,System.String)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SymbolSource_ReturnsFullMethodBlock_ForFixtureMember()
    {
        var result = await TestPaths.ExecuteCommandAsync<SymbolSourceResult>(
            "symbol-source",
            "--target", TestPaths.FixtureProjectPath(),
            "--symbol", "M:FixtureApp.Consumer.Run");

        var declaration = Assert.Single(result.Declarations);
        Assert.Equal("M:FixtureApp.Consumer.Run", result.Selector);
        Assert.StartsWith("public string Run()", declaration.Text, StringComparison.Ordinal);
        Assert.Contains("return source.GetMessage(\"world\");", declaration.Text, StringComparison.Ordinal);
        Assert.EndsWith("}", declaration.Text, StringComparison.Ordinal);
        Assert.True(declaration.Range.EndLine > declaration.Range.Line);
    }

    [Fact]
    public async Task SymbolSource_ReturnsSourceAndGeneratedDeclarations_ForPartialType()
    {
        var result = await TestPaths.ExecuteCommandAsync<SymbolSourceResult>(
            "symbol-source",
            "--target", TestPaths.FixtureProjectPath(),
            "--symbol", "T:FixtureApp.GeneratedMessageSource");

        Assert.Contains(result.Declarations, declaration =>
            declaration.Document.DocumentKind == DocumentKindNames.Source
            && declaration.Text.StartsWith("public sealed partial class GeneratedMessageSource", StringComparison.Ordinal));
        Assert.Contains(result.Declarations, declaration => declaration.Document.DocumentKind == DocumentKindNames.SourceGenerated);
    }

    [Fact]
    public async Task SymbolSource_UnknownSelector_ThrowsUsageError()
    {
        var exception = await Assert.ThrowsAsync<CliUsageException>(() => TestPaths.ExecuteCommandAsync<SymbolSourceResult>(
            "symbol-source",
            "--target", TestPaths.FixtureProjectPath(),
            "--symbol", "FixtureApp.DoesNotExist"));

        Assert.Equal("symbol-source", exception.CommandName);
        Assert.Contains("No symbol found for 'FixtureApp.DoesNotExist'", exception.Message, StringComparison.Ordinal);
    }

    private static void AssertWholeDocumentRange(DocumentRange range, string text)
    {
        var lines = text.Split('\n');

        Assert.Equal(1, range.Line);
        Assert.Equal(1, range.Column);
        Assert.Equal(lines.Length, range.EndLine);
        Assert.Equal(lines[^1].TrimEnd('\r').Length + 1, range.EndColumn);
    }

    private static AmbiguousPathFixture CreateAmbiguousPathFixture()
    {
        var root = Path.Combine(TestPaths.RepositoryRoot(), "artifacts", "path-first-document-selection", Guid.NewGuid().ToString("N"));
        var projectADirectory = Path.Combine(root, "ProjectA");
        var projectBDirectory = Path.Combine(root, "ProjectB");
        Directory.CreateDirectory(projectADirectory);
        Directory.CreateDirectory(projectBDirectory);

        var sharedSourcePath = Path.Combine(root, "Shared.cs");
        File.WriteAllText(sharedSourcePath, "namespace AmbiguousFixture;\n\npublic sealed class Shared\n{\n    public string Value => \"shared\";\n}\n");

        var projectAPath = Path.Combine(projectADirectory, "ProjectA.csproj");
        var projectBPath = Path.Combine(projectBDirectory, "ProjectB.csproj");
        File.WriteAllText(projectAPath, CreateSharedCompileProject("net10.0"));
        File.WriteAllText(projectBPath, CreateSharedCompileProject("netstandard2.1"));

        var solutionPath = Path.Combine(root, "Ambiguous.slnx");
        File.WriteAllText(solutionPath, """
            <Solution>
              <Project Path="ProjectA/ProjectA.csproj" />
              <Project Path="ProjectB/ProjectB.csproj" />
            </Solution>
            """);

        return new AmbiguousPathFixture(solutionPath, projectAPath, projectBPath, sharedSourcePath);
    }

    private static string CreateSharedCompileProject(string targetFramework)
    {
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{{targetFramework}}</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="..\Shared.cs" Link="Shared.cs" />
              </ItemGroup>
            </Project>
            """;
    }

    private sealed record AmbiguousPathFixture(
        string SolutionPath,
        string ProjectAPath,
        string ProjectBPath,
        string SharedSourcePath);
}
