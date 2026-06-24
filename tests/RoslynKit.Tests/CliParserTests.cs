namespace RoslynKit.Tests;

public sealed class CliParserTests
{
    [Fact]
    public void Parse_ReturnsHelp_WhenNoArguments()
    {
        var command = CliParser.Parse([]);

        Assert.True(command.IsHelp);
        Assert.Equal("help", command.Name);
    }

    [Fact]
    public void Parse_CollectsAliasesAndOptions()
    {
        var command = CliParser.Parse(["definition", "-t", "repo.slnx", "-f", "Program.cs", "--line", "10", "--column", "4"]);

        Assert.Equal("definition", command.Name);
        Assert.Equal("definition", command.Builtin?.Name);
        Assert.Equal("repo.slnx", command.Required("target"));
        Assert.Equal("Program.cs", command.Required("file"));
        Assert.Equal(10, command.OptionalInt("line", 1, 1));
        Assert.Equal(4, command.OptionalInt("column", 1, 1));
    }

    [Fact]
    public void Parse_CollectsLongOptionsWithInlineValues()
    {
        var command = CliParser.Parse(["symbols", "--target=repo.slnx", "--query=CliApplication", "--max=5"]);

        Assert.Equal("symbols", command.Name);
        Assert.Equal("repo.slnx", command.Required("target"));
        Assert.Equal("CliApplication", command.Required("query"));
        Assert.Equal(5, command.OptionalInt("max-results", 1, 1));
    }

    [Fact]
    public void Parse_ReturnsCommandHelp_WhenCommandAsksForHelp()
    {
        var command = CliParser.Parse(["symbols", "--help"]);

        Assert.True(command.IsHelp);
        Assert.Equal("symbols", command.HelpSubject?.Name);
    }

    [Fact]
    public void Parse_RejectsMissingRequiredOption()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse(["definition", "--target", "repo.slnx", "--file", "Program.cs"]));

        Assert.Equal("definition", exception.CommandName);
        Assert.Contains("Missing required option '--line'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAmbiguousAbbreviation()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse(["diagnostics", "--target", "repo.slnx", "--include"]));

        Assert.Equal("diagnostics", exception.CommandName);
        Assert.Contains("ambiguous", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsDuplicateOption()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse(["workspace", "--target", "a.slnx", "--target", "b.slnx"]));

        Assert.Equal("workspace", exception.CommandName);
        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }
}
