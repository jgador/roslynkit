namespace RoslynKit.Tests;

/// <summary>
/// Verifies the public command contract shared by standalone and retained execution.
/// </summary>
public sealed class RetainedCommandContractTests
{
    [Fact]
    public void Parse_ServeAcceptsRetentionAndRestoreOptions()
    {
        var command = CliParser.Parse(["serve", "--max-workspaces", "2", "--no-restore"]);

        Assert.Equal("serve", command.Name);
        Assert.Equal(2, command.OptionalInt("max-workspaces", 4, 1));
        Assert.Equal("false", command.Optional("restore"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("invalid")]
    public void Parse_ServeRejectsInvalidRetentionLimit(string value)
    {
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["serve", "--max-workspaces", value]));
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("diagnostics")]
    [InlineData("index")]
    public void Parse_WorkspaceCommandsCanDisableAutomaticRestore(string name)
    {
        var command = CliParser.Parse([name, "--no-restore"]);

        Assert.Equal("false", command.Optional("restore"));
    }

    [Fact]
    public void Help_AdvertisesServerWithoutSemanticAnswerCaching()
    {
        var reference = CommandReferenceMarkdown.Render();

        Assert.Contains("roslynkit serve", reference, StringComparison.Ordinal);
        Assert.Contains("--max-workspaces", reference, StringComparison.Ordinal);
        Assert.DoesNotContain("semantic catalog", reference, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RefreshIsNotAStandaloneCommand()
    {
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["refresh"]));
    }
}
