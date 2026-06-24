using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynKit.Tests;

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
    public void JsonEnvelope_UsesExplicitPropertyNames_WithoutNamingPolicy()
    {
        var envelope = JsonEnvelope.ForSuccess(
            "workspace",
            new WorkspaceResult(
                @"C:\repo\GitHub\roslynkit\RoslynKit.slnx",
                "slnx",
                [new WorkspaceProject("RoslynKit", null, "C#", 2, ["RoslynKit.Tests"])],
                [new WorkspaceDocument("RoslynKit", "Program.cs", @"C:\repo\GitHub\roslynkit\src\RoslynKit\Program.cs")],
                [new WorkspaceLoadDiagnostic("Warning", "Workspace issue")]));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope, CreateContractOptions()));
        var root = document.RootElement;
        var data = root.GetProperty("data");
        var project = data.GetProperty("projects")[0];
        var sourceDocument = data.GetProperty("documents")[0];
        var workspaceDiagnostic = data.GetProperty("workspaceDiagnostics")[0];

        Assert.True(root.TryGetProperty("schemaVersion", out _));
        Assert.False(root.TryGetProperty("SchemaVersion", out _));
        Assert.Equal("workspace", root.GetProperty("command").GetString());
        Assert.Equal(2, project.GetProperty("documentCount").GetInt32());
        Assert.False(project.TryGetProperty("DocumentCount", out _));
        Assert.Equal("RoslynKit", sourceDocument.GetProperty("projectName").GetString());
        Assert.Equal("Warning", workspaceDiagnostic.GetProperty("kind").GetString());
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

    private static JsonSerializerOptions CreateContractOptions()
    {
        return new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }
}
