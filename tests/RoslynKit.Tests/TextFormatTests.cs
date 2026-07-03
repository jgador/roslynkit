using System.Text.Json;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies the <c>--format text</c> output mode emits deterministic plain text on success and the
/// minified JSON errors envelope on failure.
/// </summary>
public sealed class TextFormatTests
{
    [Fact]
    public async Task Text_EmitsPlainTextPayload_ForSymbols()
    {
        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(
            [
                "symbols",
                "--target", TestPaths.SolutionPath(),
                "--query", "CliApplication",
                "--exact",
                "--kind", "class",
                "--format", "text",
            ],
            TestContext.Current.CancellationToken);

        var output = writer.ToString();

        Assert.True(exitCode == 0, $"Expected exit code 0 but got {exitCode}. Output: {output}");
        Assert.StartsWith("symbols CliApplication total ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"data\"", output, StringComparison.Ordinal);
        Assert.Contains("T:RoslynKit.CliApplication", output, StringComparison.Ordinal);
        Assert.Contains("CliApplication.cs:", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Text_WritesMinifiedJsonEnvelope_ForUsageFailure()
    {
        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(
            ["symbols", "--target", "missing.slnx", "--query", "Foo", "--kind", "banana", "--format", "text"],
            TestContext.Current.CancellationToken);

        var output = writer.ToString();

        Assert.Equal(2, exitCode);
        Assert.Single(output.TrimEnd('\r', '\n').Split('\n'));

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("data", out _));
        Assert.Contains("Unknown symbol kind 'banana'", root.GetProperty("errors")[0].GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_RejectsUnknownFormatValue()
    {
        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(
            ["symbols", "--target", "missing.slnx", "--query", "Foo", "--format", "banana"],
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(writer.ToString());
        var message = document.RootElement.GetProperty("errors")[0].GetProperty("message").GetString();

        Assert.Equal(2, exitCode);
        Assert.Contains("Option '--format' must be 'json', 'compact', or 'text'.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_RendersSymbolSourceDeclarations_Unescaped()
    {
        var declarationText = "public sealed class Widget\n{\n    private const string Label = \"quoted\";\n}";
        var descriptor = new DocumentDescriptor(
            "doc_123",
            "App",
            null,
            "net10.0",
            DocumentKindNames.Source,
            "Source.cs",
            @"C:\repo\App\Source.cs");
        var result = new SymbolSourceResult(
            "app.slnx",
            "T:App.Widget",
            new SymbolItem(
                "App",
                "Widget",
                "Widget",
                "App.Widget",
                "NamedType",
                "Public",
                isStatic: false,
                containingType: null,
                containingNamespace: "App",
                new SourceRange(@"C:\repo\App\Source.cs", 3, 14, 3, 20),
                [new SourceRange(@"C:\repo\App\Source.cs", 3, 14, 3, 20)],
                "T:App.Widget"),
            [new SymbolSourceDeclaration(descriptor, new DocumentRange(3, 1, 6, 2), declarationText)],
            []);

        var rendered = TextProjection.Render(result);

        Assert.StartsWith("symbol-source T:App.Widget", rendered, StringComparison.Ordinal);
        Assert.Contains("symbol App.Widget " + @"C:\repo\App\Source.cs" + ":3:14 T:App.Widget", rendered, StringComparison.Ordinal);
        Assert.Contains("== " + @"C:\repo\App\Source.cs" + ":3:1-6:2", rendered, StringComparison.Ordinal);

        // The declaration body stays verbatim: real newlines and quotes, no JSON string escaping.
        Assert.Contains(declarationText, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\\n", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\"", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_RendersQuickInfoSections()
    {
        var descriptor = new DocumentDescriptor(
            "doc_123",
            "App",
            null,
            "net10.0",
            DocumentKindNames.Source,
            "Source.cs",
            @"C:\repo\App\Source.cs");
        var result = new QuickInfoResult(
            descriptor,
            7,
            15,
            new DocumentRange(7, 11, 7, 25),
            ["Class", "Public"],
            [
                new QuickInfoSectionItem("Description", "record App.Widget"),
                new QuickInfoSectionItem("DocumentationComments", "Represents a widget."),
            ],
            [new WorkspaceLoadDiagnostic("Warning", "Workspace issue")]);

        var rendered = TextProjection.Render(result);

        var expected = "quick-info " + @"C:\repo\App\Source.cs" + ":7:15 [Class,Public]\n"
            + "record App.Widget\n"
            + "Represents a widget.\n"
            + "workspace-diagnostics 1";
        Assert.Equal(expected, rendered);
    }
}
