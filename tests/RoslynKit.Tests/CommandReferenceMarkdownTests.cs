namespace RoslynKit.Tests;

/// <summary>
/// Verifies the generated runtime command reference stays aligned with the built-in command registry.
/// </summary>
public sealed class CommandReferenceMarkdownTests
{
    [Fact]
    public void Render_ListsRuntimeCommands_InRegistryOrder()
    {
        var rendered = CommandReferenceMarkdown.Render();
        var previousIndex = -1;

        foreach (var command in BuiltinCommandRegistry.Commands)
        {
            var index = rendered.IndexOf($"## `{command.Name}`", StringComparison.Ordinal);

            Assert.True(index > previousIndex, $"Command '{command.Name}' was not rendered in registry order.");
            previousIndex = index;
        }
    }

    [Fact]
    public void Render_IncludesRepresentativeUsageAndOptions()
    {
        var rendered = CommandReferenceMarkdown.Render();

        Assert.Contains("roslynkit init [--agent <codex|claude|copilot|all>] [--overwrite]", rendered, StringComparison.Ordinal);
        Assert.Contains("- `--agent` `<agent>`: agent target: codex, claude, copilot, or all", rendered, StringComparison.Ordinal);
        Assert.Contains("## `daemon status`\n\nReport the compatible workspace daemon state without starting it.", rendered, StringComparison.Ordinal);
        Assert.Contains("roslynkit daemon status --target <target>", rendered, StringComparison.Ordinal);
        Assert.Contains("## `daemon stop`\n\nStop the compatible workspace daemon if it is running, without starting one.", rendered, StringComparison.Ordinal);
        Assert.Contains("roslynkit daemon stop --target <target>", rendered, StringComparison.Ordinal);
        Assert.Contains("roslynkit symbols --target <target> --query <text>", rendered, StringComparison.Ordinal);
        Assert.Contains("- `--query` / `-q` `<text>` (required): symbol name text to search for", rendered, StringComparison.Ordinal);
        Assert.Contains("roslynkit definition --target <target> --symbol <selector>", rendered, StringComparison.Ordinal);
        Assert.Contains("- `--symbol` `<selector>` (required): documentation-comment ID", rendered, StringComparison.Ordinal);
        Assert.Contains("roslynkit document-lines --target <target> --file <path>", rendered, StringComparison.Ordinal);
        Assert.Contains("## `version`\n\nPrint the installed RoslynKit version.", rendered, StringComparison.Ordinal);
        Assert.Contains("No options.", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_MatchesCheckedInCommandReference()
    {
        var expected = NormalizeNewlines(CommandReferenceMarkdown.Render());
        var path = TestPaths.RepoFile(".agents", "skills", "roslynkit", "references", "commands.md");

        Assert.True(File.Exists(path), "Run `dotnet run --file .\\tools\\RoslynKit.CommandDocs.cs -- --write` to create the generated command reference.");

        var actual = NormalizeNewlines(File.ReadAllText(path));
        Assert.Equal(expected, actual);
    }

    private static string NormalizeNewlines(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
