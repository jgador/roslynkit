namespace RoslynKit.Tests;

/// <summary>
/// Verifies the markdown output renderer emits deterministic key-value headers, labeled bullets,
/// code-span escaping, and verbatim fenced source text.
/// </summary>
public sealed class MarkdownFormatTests
{
    [Fact]
    public async Task RunAsync_WritesMarkdownPayload_ForSymbolsCommand()
    {
        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(
            [
                "symbols",
                "--target", TestPaths.SolutionPath(),
                "--query", "CliApplication",
                "--exact",
                "--kind", "class",
            ],
            TestContext.Current.CancellationToken);

        var output = writer.ToString();

        Assert.True(exitCode == 0, $"Expected exit code 0 but got {exitCode}. Output: {output}");
        Assert.StartsWith("command: symbols\nquery: `CliApplication`\nreturned: ", output.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("- kind: NamedType name: `RoslynKit.CliApplication`", output, StringComparison.Ordinal);
        Assert.Contains("id: `T:RoslynKit.CliApplication`", output, StringComparison.Ordinal);
        Assert.Contains("documentation: Owns the top-level CLI flow", output, StringComparison.Ordinal);
        Assert.Contains("CliApplication.cs:", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_WritesDocumentation_ForReferencesCommand()
    {
        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(
            [
                "references",
                "--target", TestPaths.SolutionPath(),
                "--symbol", "RoslynKit.PositionResolver.GetPositionAsync",
                "--max-results", "1",
            ],
            TestContext.Current.CancellationToken);

        var output = writer.ToString();

        Assert.True(exitCode == 0, $"Expected exit code 0 but got {exitCode}. Output: {output}");
        Assert.Contains("command: references", output, StringComparison.Ordinal);
        Assert.Contains("symbol: `M:RoslynKit.PositionResolver.GetPositionAsync(", output, StringComparison.Ordinal);
        Assert.Contains(
            "documentation: Validates one-based CLI coordinates and converts them into a Roslyn document position.",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EmitsQueryAndCountHeader_ForSymbols()
    {
        var result = new SymbolsResult(
            "app.slnx",
            "Widget",
            totalCount: 2,
            returnedCount: 2,
            truncated: false,
            [CreateSymbol("Represents a widget.")],
            []);

        var rendered = MarkdownProjection.Render(result);

        var expected = "command: symbols\n"
            + "query: `Widget`\n"
            + "returned: 2/2\n"
            + "truncated: false\n"
            + "\n"
            + "- kind: NamedType name: `App.Widget` loc: `" + SourcePath + ":3:14-3:20` id: `T:App.Widget`\n"
            + "  documentation: Represents a widget.";
        Assert.Equal(expected, rendered);
    }

    [Fact]
    public void Render_OmitsEmptyDocumentation_ForSymbolBullets()
    {
        var result = new SymbolsResult(
            "app.slnx",
            "Widget",
            totalCount: 1,
            returnedCount: 1,
            truncated: false,
            [CreateSymbol("   ")],
            []);

        var rendered = MarkdownProjection.Render(result);

        Assert.DoesNotContain("documentation:", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EscapesBackticks_InInlineCodeSpans()
    {
        var result = new SymbolsResult("app.slnx", "a`b", 0, 0, truncated: false, [], []);

        var rendered = MarkdownProjection.Render(result);

        Assert.Contains("query: ``a`b``", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EmitsFencedDeclaration_Unescaped_ForSymbolSource()
    {
        var declarationText = "public sealed class Widget\n{\n    private const string Label = \"quoted\";\n}";
        var result = new SymbolSourceResult(
            "app.slnx",
            "T:App.Widget",
            CreateSymbol("Represents a widget."),
            [new SymbolSourceDeclaration(CreateDescriptor(), new DocumentRange(3, 1, 6, 2), declarationText)],
            []);

        var rendered = MarkdownProjection.Render(result);

        var expected = "command: symbol-source\n"
            + "symbol: `T:App.Widget`\n"
            + "\n"
            + "- kind: NamedType name: `App.Widget` loc: `" + SourcePath + ":3:14-3:20` id: `T:App.Widget`\n"
            + "\n"
            + "loc: `" + SourcePath + ":3:1-6:2`\n"
            + "```csharp\n"
            + declarationText + "\n"
            + "```";
        Assert.Equal(expected, rendered);
        Assert.DoesNotContain("documentation:", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\\n", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\"", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ExtendsFence_WhenSourceContainsBacktickRuns()
    {
        var declarationText = "var markdown = \"```csharp\";";
        var result = new SymbolSourceResult(
            "app.slnx",
            "T:App.Widget",
            CreateSymbol(),
            [new SymbolSourceDeclaration(CreateDescriptor(), new DocumentRange(3, 1, 3, 28), declarationText)],
            []);

        var rendered = MarkdownProjection.Render(result);

        Assert.Contains("````csharp\n" + declarationText + "\n````", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_SplitsDescriptionAndDocumentationFences_ForQuickInfo()
    {
        var result = new QuickInfoResult(
            CreateDescriptor(),
            7,
            15,
            new DocumentRange(7, 11, 7, 25),
            ["Class", "Public"],
            [
                new QuickInfoSectionItem("Description", "record App.Widget"),
                new QuickInfoSectionItem("DocumentationComments", "Represents a widget."),
            ],
            [new WorkspaceLoadDiagnostic("Warning", "Workspace issue")]);

        var rendered = MarkdownProjection.Render(result);

        var expected = "command: quick-info\n"
            + "selector: `" + SourcePath + ":7:15-7:15`\n"
            + "range: `" + SourcePath + ":7:11-7:25`\n"
            + "tags: `Class`, `Public`\n"
            + "\n"
            + "description:\n"
            + "```csharp\nrecord App.Widget\n```\n"
            + "\n"
            + "documentation:\n"
            + "```text\nRepresents a widget.\n```\n"
            + "\n"
            + "workspace-diagnostics: 1\n"
            + "- severity: Warning message: `Workspace issue`";
        Assert.Equal(expected, rendered);
    }

    [Fact]
    public void Render_MarksImplicitReferences()
    {
        var result = new ReferencesResult(
            document: null,
            line: null,
            column: null,
            selector: "M:App.Widget.Run",
            CreateSymbol("Runs application work for the current request."),
            totalCount: 2,
            returnedCount: 2,
            truncated: false,
            [
                new ReferenceItem(ProgramPath, 10, 5, 10, 8, isImplicit: false, "App.Widget"),
                new ReferenceItem(ProgramPath, 20, 5, 20, 8, isImplicit: true, "App.Widget"),
            ],
            []);

        var rendered = MarkdownProjection.Render(result);

        var expected = "command: references\n"
            + "selector: `M:App.Widget.Run`\n"
            + "symbol: `T:App.Widget`\n"
            + "documentation: Runs application work for the current request.\n"
            + "returned: 2/2\n"
            + "truncated: false\n"
            + "\n"
            + "- loc: `" + ProgramPath + ":10:5-10:8`\n"
            + "- loc: `" + ProgramPath + ":20:5-20:8` implicit: true";
        Assert.Equal(expected, rendered);
    }

    [Fact]
    public void Render_OmitsEmptyDocumentation_ForReferences()
    {
        var result = new ReferencesResult(
            document: null,
            line: null,
            column: null,
            selector: "M:App.Widget.Run",
            CreateSymbol("   "),
            totalCount: 0,
            returnedCount: 0,
            truncated: false,
            [],
            []);

        var rendered = MarkdownProjection.Render(result);

        Assert.DoesNotContain("documentation:", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EmitsBoundedSymbolContextWithNodeIdentityAndCommentMetadata()
    {
        var selectedLocation = new SourceRange(SourcePath, 7, 5, 12, 6);
        var alternateLocation = new SourceRange(SourcePath, 20, 1, 24, 2);
        var invocationLocation = new SourceRange(SourcePath, 10, 16, 10, 32);
        var commentLocation = new SourceRange(SourcePath, 6, 5, 6, 32);
        var result = new SymbolContextResult(
            document: null,
            line: null,
            column: null,
            selector: "M:App.Widget.Run",
            selectedNode: new SyntaxContextNode("MethodDeclaration", selectedLocation, "Run", "M:App.Widget.Run"),
            symbol: CreateSymbol("Runs application work for the current request."),
            documentation: "Runs application work for the current request.",
            alternateDeclarations: [alternateLocation],
            ancestors: [new SyntaxContextNode("ClassDeclaration", new SourceRange(SourcePath, 3, 1, 15, 2), "Widget", "T:App.Widget")],
            totalDescendantCount: 2,
            returnedDescendantCount: 1,
            descendantsTruncated: true,
            descendants: [new SymbolContextDescendant("invocation", 2, "InvocationExpression", invocationLocation, "App.Worker.Run", "M:App.Worker.Run")],
            totalCommentCount: 2,
            returnedCommentCount: 1,
            commentsTruncated: true,
            comments: [new SymbolContextComment("leading", "line", commentLocation, "Routes to the worker.")],
            workspaceDiagnostics: []);

        var rendered = MarkdownProjection.Render(result);

        var expected = "command: symbol-context\n"
            + "selector: `M:App.Widget.Run`\n"
            + "selected-node: kind: MethodDeclaration loc: `" + SourcePath + ":7:5-12:6` name: `Run` id: `M:App.Widget.Run`\n"
            + "symbol: `T:App.Widget` name: `App.Widget`\n"
            + "documentation: Runs application work for the current request.\n"
            + "alternate-declarations: 1\n"
            + "- loc: `" + SourcePath + ":20:1-24:2`\n"
            + "ancestors: 1\n"
            + "- kind: ClassDeclaration loc: `" + SourcePath + ":3:1-15:2` name: `Widget` id: `T:App.Widget`\n"
            + "descendants: 1/2\n"
            + "descendants-truncated: true\n"
            + "- relation: invocation depth: 2 kind: InvocationExpression loc: `" + SourcePath + ":10:16-10:32` target: `App.Worker.Run` target-id: `M:App.Worker.Run`\n"
            + "comments: 1/2\n"
            + "comments-truncated: true\n"
            + "- placement: leading style: line loc: `" + SourcePath + ":6:5-6:32` text: `Routes to the worker.`";
        Assert.Equal(expected, rendered);
    }

    [Fact]
    public void Render_EmitsTextFence_ForNonSourceDocumentText()
    {
        var descriptor = new DocumentDescriptor(
            "App",
            null,
            "net10.0",
            DocumentKindNames.Additional,
            "notes.txt",
            @"C:\repo\App\notes.txt");
        var result = new DocumentTextResult(descriptor, new DocumentRange(1, 1, 3, 1), "line1\nline2\n", truncated: false, []);

        var rendered = MarkdownProjection.Render(result);

        var expected = "command: document-text\n"
            + "path: `" + @"C:\repo\App\notes.txt" + "`\n"
            + "\n"
            + "```text\nline1\nline2\n```";
        Assert.Equal(expected, rendered);
    }

    [Fact]
    public void Render_EmitsRangeFence_ForDocumentLines()
    {
        const string sourcePath = @"src\App\Source.cs";
        var descriptor = new DocumentDescriptor(
            "App",
            null,
            "net10.0",
            DocumentKindNames.Source,
            "Source.cs",
            sourcePath);
        var result = new DocumentLinesResult(
            descriptor,
            new DocumentRange(10, 1, 12, 2),
            "if (ready)\n{\n}",
            []);

        var rendered = MarkdownProjection.Render(result);

        var expected = "command: document-lines\n"
            + "path: `" + sourcePath + "`\n"
            + "range: `" + sourcePath + ":10:1-12:2`\n"
            + "\n"
            + "```csharp\n"
            + "if (ready)\n{\n}\n"
            + "```";
        Assert.Equal(expected, rendered);
    }

    [Fact]
    public void Render_UsesDisplayPath_ForDocumentText()
    {
        var descriptor = new DocumentDescriptor(
            "App",
            @"C:\repo\App\App.csproj",
            "net10.0",
            DocumentKindNames.Source,
            "Source.cs",
            @"C:\repo\App\Source.cs",
            @"App\App.csproj",
            @"App\Source.cs");
        var result = new DocumentTextResult(descriptor, new DocumentRange(1, 1, 1, 14), "public class C", truncated: false, []);

        var rendered = MarkdownProjection.Render(result);

        Assert.Contains("path: `" + @"App\Source.cs" + "`", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\repo\App\Source.cs", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EmitsProjectAndDocumentBullets_ForWorkspace()
    {
        var result = new WorkspaceResult(
            "app.slnx",
            "slnx",
            [new WorkspaceProject("App", null, "net10.0", "C#", 2, [])],
            [CreateDescriptor()],
            []);

        var rendered = MarkdownProjection.Render(result);

        var expected = "command: workspace\n"
            + "documents: 1\n"
            + "\n"
            + "- project: `App` tfm: `net10.0` documents: 2\n"
            + "- project: `App` tfm: `net10.0` kind: source path: `" + SourcePath + "`";
        Assert.Equal(expected, rendered);
    }

    [Fact]
    public void Render_EmitsWorkspaceDiagnosticMessages_AsBullets()
    {
        var result = new SymbolsResult(
            "app.slnx",
            "Widget",
            0,
            0,
            truncated: false,
            [],
            [new WorkspaceLoadDiagnostic("Failure", "Project load failed for App.csproj")]);

        var rendered = MarkdownProjection.Render(result);

        var expected = "command: symbols\n"
            + "query: `Widget`\n"
            + "returned: 0/0\n"
            + "truncated: false\n"
            + "\n"
            + "workspace-diagnostics: 1\n"
            + "- severity: Failure message: `Project load failed for App.csproj`";
        Assert.Equal(expected, rendered);
    }

    private const string SourcePath = @"C:\repo\App\Source.cs";
    private const string ProgramPath = @"C:\repo\App\Program.cs";

    private static DocumentDescriptor CreateDescriptor()
    {
        return new DocumentDescriptor(
            "App",
            null,
            "net10.0",
            DocumentKindNames.Source,
            "Source.cs",
            SourcePath);
    }

    private static SymbolItem CreateSymbol(string? documentation = null)
    {
        return new SymbolItem(
            "App",
            "Widget",
            "Widget",
            "App.Widget",
            "NamedType",
            "Public",
            isStatic: false,
            containingType: null,
            containingNamespace: "App",
            new SourceRange(SourcePath, 3, 14, 3, 20),
            [new SourceRange(SourcePath, 3, 14, 3, 20)],
            "T:App.Widget",
            documentation);
    }
}
