using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies JSON envelope and help payload contracts emitted by the CLI.
/// </summary>
public sealed class EnvelopeTests
{
    [Fact]
    public async Task RunAsync_WritesJsonEnvelope_ForHelp()
    {
        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(["help"], TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;

        Assert.Equal(0, exitCode);
        Assert.Equal("roslynkit", root.GetProperty("tool").GetString());
        Assert.Equal("help", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task RunAsync_ReturnsUsageError_ForInvalidSymbolKind()
    {
        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(["symbols", "--target", "missing.slnx", "--query", "Foo", "--kind", "banana"], TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;

        Assert.Equal(2, exitCode);
        Assert.Equal("symbols", root.GetProperty("command").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Contains("Unknown symbol kind 'banana'", root.GetProperty("errors")[0].GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReturnsUsageError_ForMissingDocumentSelector()
    {
        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(["document-text", "--target", "missing.slnx"], TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;

        Assert.Equal(2, exitCode);
        Assert.Equal("document-text", root.GetProperty("command").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Contains("Exactly one of '--file' or '--document-key' is required.", root.GetProperty("errors")[0].GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonEnvelope_UsesExplicitPropertyNames_ForWorkspaceContracts()
    {
        var envelope = JsonEnvelope.ForSuccess(
            "workspace",
            new WorkspaceResult(
                @"C:\repo\GitHub\roslynkit\RoslynKit.slnx",
                "slnx",
                [new WorkspaceProject("RoslynKit", null, "net10.0", "C#", 2, ["RoslynKit.Tests"])],
                [CreateDescriptor()],
                [new WorkspaceLoadDiagnostic("Warning", "Workspace issue")]));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope, CreateContractOptions()));
        var root = document.RootElement;
        var data = root.GetProperty("data");
        var project = data.GetProperty("projects")[0];
        var workspaceDocument = data.GetProperty("documents")[0];

        Assert.True(root.TryGetProperty("schemaVersion", out _));
        Assert.False(root.TryGetProperty("SchemaVersion", out _));
        Assert.Equal("workspace", root.GetProperty("command").GetString());
        Assert.Equal("net10.0", project.GetProperty("targetFramework").GetString());
        Assert.False(project.TryGetProperty("TargetFramework", out _));
        Assert.Equal("doc_123", workspaceDocument.GetProperty("documentKey").GetString());
        Assert.Equal("source", workspaceDocument.GetProperty("documentKind").GetString());
        Assert.False(workspaceDocument.TryGetProperty("DocumentKind", out _));
    }

    [Fact]
    public void JsonEnvelope_UsesExplicitPropertyNames_ForDocumentTextContracts()
    {
        var envelope = JsonEnvelope.ForSuccess(
            "document-text",
            new DocumentTextResult(
                CreateDescriptor(),
                new DocumentRange(5, 2, 8, 11),
                "sample text",
                truncated: false,
                [new WorkspaceLoadDiagnostic("Warning", "Workspace issue")]));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope, CreateContractOptions()));
        var data = document.RootElement.GetProperty("data");

        Assert.Equal("Program.cs", data.GetProperty("document").GetProperty("name").GetString());
        Assert.Equal(5, data.GetProperty("resolvedRange").GetProperty("line").GetInt32());
        Assert.False(data.TryGetProperty("ResolvedRange", out _));
        Assert.False(data.TryGetProperty("Truncated", out _));
    }

    [Fact]
    public void JsonEnvelope_UsesExplicitPropertyNames_ForTypeDefinitionContracts()
    {
        var envelope = JsonEnvelope.ForSuccess(
            "type-definition",
            new TypeDefinitionResult(
                CreateDescriptor(),
                7,
                15,
                CreateSymbol(),
                [new WorkspaceLoadDiagnostic("Warning", "Workspace issue")]));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope, CreateContractOptions()));
        var data = document.RootElement.GetProperty("data");

        Assert.Equal(7, data.GetProperty("line").GetInt32());
        Assert.Equal("CliApplication", data.GetProperty("symbol").GetProperty("name").GetString());
        Assert.False(data.GetProperty("symbol").TryGetProperty("PrimaryLocation", out _));
    }

    [Fact]
    public void JsonEnvelope_UsesExplicitPropertyNames_ForQuickInfoContracts()
    {
        var envelope = JsonEnvelope.ForSuccess(
            "quick-info",
            new QuickInfoResult(
                CreateDescriptor(),
                7,
                15,
                new DocumentRange(7, 11, 7, 25),
                ["Class"],
                [new QuickInfoSectionItem("Description", "RoslynKit.CliApplication")],
                [new WorkspaceLoadDiagnostic("Warning", "Workspace issue")]));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope, CreateContractOptions()));
        var data = document.RootElement.GetProperty("data");
        var section = data.GetProperty("sections")[0];

        Assert.Equal("Class", data.GetProperty("tags")[0].GetString());
        Assert.Equal("Description", section.GetProperty("kind").GetString());
        Assert.Equal("RoslynKit.CliApplication", section.GetProperty("text").GetString());
    }

    [Fact]
    public void JsonEnvelope_UsesExplicitPropertyNames_ForImplementationsContracts()
    {
        var envelope = JsonEnvelope.ForSuccess(
            "implementations",
            new ImplementationsResult(
                CreateDescriptor(),
                12,
                4,
                CreateSymbol(),
                totalCount: 2,
                returnedCount: 1,
                truncated: true,
                [CreateSymbol()],
                [new WorkspaceLoadDiagnostic("Warning", "Workspace issue")]));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope, CreateContractOptions()));
        var data = document.RootElement.GetProperty("data");

        Assert.Equal(2, data.GetProperty("totalCount").GetInt32());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal("CliApplication", data.GetProperty("symbols")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void JsonEnvelope_UsesExplicitPropertyNames_ForSignatureHelpContracts()
    {
        var envelope = JsonEnvelope.ForSuccess(
            "signature-help",
            new SignatureHelpResult(
                CreateDescriptor(),
                9,
                18,
                new DocumentRange(9, 18, 9, 36),
                activeSignature: 0,
                activeParameter: 1,
                [new SignatureHelpSignatureItem(
                    "CliUsageException(string commandName, string message)",
                    "constructor docs",
                    isVariadic: false,
                    [new SignatureHelpParameterItem("commandName", "string commandName", "name docs", isOptional: false)])],
                [new WorkspaceLoadDiagnostic("Warning", "Workspace issue")]));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope, CreateContractOptions()));
        var data = document.RootElement.GetProperty("data");
        var signature = data.GetProperty("signatures")[0];
        var parameter = signature.GetProperty("parameters")[0];

        Assert.Equal(1, data.GetProperty("activeParameter").GetInt32());
        Assert.Equal("CliUsageException(string commandName, string message)", signature.GetProperty("label").GetString());
        Assert.Equal("commandName", parameter.GetProperty("name").GetString());
        Assert.False(parameter.TryGetProperty("IsOptional", out _));
    }

    [Fact]
    public void HelpModels_UseExplicitPropertyNames_WithoutNamingPolicy()
    {
        var envelope = JsonEnvelope.ForSuccess(
            "help",
            new HelpResult(
                "roslynkit",
                "desc",
                [new CommandHelp(
                    "workspace",
                    "desc",
                    ["roslynkit workspace --target <PATH>"],
                    [new OptionHelp("target", "-t", "String", "PATH", "Target path", true)])]));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope, CreateContractOptions()));
        var root = document.RootElement;
        var command = root.GetProperty("data").GetProperty("commands")[0];
        var option = command.GetProperty("options")[0];

        Assert.Equal("roslynkit", root.GetProperty("data").GetProperty("name").GetString());
        Assert.Equal("workspace", command.GetProperty("name").GetString());
        Assert.Equal("-t", option.GetProperty("shortName").GetString());
        Assert.Equal("PATH", option.GetProperty("valueName").GetString());
        Assert.True(option.GetProperty("required").GetBoolean());
    }

    private static DocumentDescriptor CreateDescriptor()
    {
        return new DocumentDescriptor(
            "doc_123",
            "RoslynKit",
            @"C:\repo\GitHub\roslynkit\src\RoslynKit\RoslynKit.csproj",
            "net10.0",
            DocumentKindNames.Source,
            "Program.cs",
            @"C:\repo\GitHub\roslynkit\src\RoslynKit\Program.cs");
    }

    private static SymbolItem CreateSymbol()
    {
        return new SymbolItem(
            "RoslynKit",
            "CliApplication",
            "CliApplication",
            "RoslynKit.CliApplication",
            "NamedType",
            "Public",
            isStatic: false,
            containingType: null,
            containingNamespace: "RoslynKit",
            new SourceRange(@"C:\repo\GitHub\roslynkit\src\RoslynKit\CliApplication.cs", 8, 20, 8, 34),
            [new SourceRange(@"C:\repo\GitHub\roslynkit\src\RoslynKit\CliApplication.cs", 8, 20, 8, 34)]);
    }

    private static JsonSerializerOptions CreateContractOptions()
    {
        return new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }
}
