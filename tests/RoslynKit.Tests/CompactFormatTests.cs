using System.Text.Json;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies the <c>--format compact</c> output mode emits a minified, trimmed envelope with collapsed source locations.
/// </summary>
public sealed class CompactFormatTests
{
    [Fact]
    public async Task Compact_EmitsMinifiedTrimmedEnvelope_ForDefinition()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        var (line, column) = TestPaths.FindLineAndColumn(programPath, "CliApplication");

        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(
            [
                "definition",
                "--target", TestPaths.SolutionPath(),
                "--file", programPath,
                "--line", line.ToString(),
                "--column", column.ToString(),
                "--format", "compact",
            ],
            TestContext.Current.CancellationToken);

        var output = writer.ToString();

        Assert.Equal(0, exitCode);

        // Minified: a single output line with no indentation whitespace.
        Assert.Single(output.TrimEnd('\r', '\n').Split('\n'));
        Assert.DoesNotContain("  ", output, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        // Trimmed envelope: keeps command/success/data, drops the constant schemaVersion/tool fields.
        Assert.Equal("definition", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.False(root.TryGetProperty("schemaVersion", out _));
        Assert.False(root.TryGetProperty("tool", out _));

        var symbol = root.GetProperty("data").GetProperty("symbol");

        // Location collapsed into a single path:line:column string.
        var loc = symbol.GetProperty("loc").GetString();
        Assert.NotNull(loc);
        Assert.EndsWith("CliApplication.cs:" + LineColumnSuffix(loc!), loc, StringComparison.OrdinalIgnoreCase);

        // Verbose per-symbol metadata is dropped in compact mode.
        Assert.False(symbol.TryGetProperty("metadataName", out _));
        Assert.False(symbol.TryGetProperty("displayName", out _));
        Assert.False(symbol.TryGetProperty("primaryLocation", out _));
    }

    private static string LineColumnSuffix(string loc)
    {
        var parts = loc.Split(':');
        return $"{parts[^2]}:{parts[^1]}";
    }

    [Fact]
    public async Task Compact_DataDeserializesIntoTypedDto_ViaJsonPropertyName()
    {
        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(
            [
                "symbols",
                "--target", TestPaths.SolutionPath(),
                "--query", "CliApplication",
                "--max-results", "2",
                "--format", "compact",
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(writer.ToString());
        var data = document.RootElement.GetProperty("data").GetRawText();

        // The compact data payload deserializes into the typed DTO purely via its [JsonPropertyName] members.
        var typed = JsonSerializer.Deserialize<CompactSymbolsData>(data);

        Assert.NotNull(typed);
        Assert.Equal("CliApplication", typed!.Query);
        Assert.NotEmpty(typed.Symbols);
        Assert.All(typed.Symbols, symbol =>
        {
            Assert.False(string.IsNullOrEmpty(symbol.Kind));
            Assert.False(string.IsNullOrEmpty(symbol.Name));
            Assert.False(string.IsNullOrEmpty(symbol.Loc));
        });
    }

    [Fact]
    public void CompactSymbolsData_RoundTrips_SerializeThenDeserialize()
    {
        var original = new CompactSymbolsData(
            "app.slnx",
            "Foo",
            3,
            1,
            Truncated: true,
            [new CompactSymbol("Method", "Foo", "App.Bar", "src/Bar.cs:10:5", null)],
            WorkspaceDiagnosticCount: null);

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<CompactSymbolsData>(json);

        // Compare via re-serialization: record equality compares collection members by reference,
        // so assert structural fidelity through the JSON instead.
        Assert.NotNull(roundTripped);
        Assert.Equal(json, JsonSerializer.Serialize(roundTripped));
        Assert.Equal("app.slnx", roundTripped!.Target);
        Assert.Equal("src/Bar.cs:10:5", roundTripped.Symbols[0].Loc);
        Assert.True(roundTripped.Truncated);
    }
}
