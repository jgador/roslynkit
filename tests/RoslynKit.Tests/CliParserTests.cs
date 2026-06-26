namespace RoslynKit.Tests;

/// <summary>
/// Verifies RoslynKit command-line parsing and usage validation behavior.
/// </summary>
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
    public void Parse_RewritesTopLevelVersionFlagToVersionCommand()
    {
        var command = CliParser.Parse(["--version"]);

        Assert.Equal("version", command.Name);
        Assert.Equal("version", command.Builtin?.Name);
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
        var command = CliParser.Parse(["symbols", "--target=repo.slnx", "--query=CliApplication", "--max=5", "--exact", "--kind=method"]);

        Assert.Equal("symbols", command.Name);
        Assert.Equal("repo.slnx", command.Required("target"));
        Assert.Equal("CliApplication", command.Required("query"));
        Assert.Equal(5, command.OptionalInt("max-results", 1, 1));
        Assert.True(command.Flag("exact"));
        Assert.Equal("method", command.Required("kind"));
    }

    [Fact]
    public void Parse_AcceptsDocumentKeySelector()
    {
        var command = CliParser.Parse(["quick-info", "--target", "repo.slnx", "--document-key", "doc_123", "--line", "7", "--column", "15"]);

        Assert.Equal("quick-info", command.Name);
        Assert.Equal("doc_123", command.Required("document-key"));
        Assert.Null(command.Optional("file"));
    }

    [Fact]
    public void Parse_CollectsDocumentTextRangeOptions()
    {
        var command = CliParser.Parse([
            "document-text",
            "--target", "repo.slnx",
            "--file", "Program.cs",
            "--start-line", "5",
            "--start-column", "2",
            "--end-line", "8",
            "--end-column", "11",
        ]);

        Assert.Equal("document-text", command.Name);
        Assert.Equal(5, command.OptionalInt("start-line", 1));
        Assert.Equal(2, command.OptionalInt("start-column", 1));
        Assert.Equal(8, command.OptionalInt("end-line", 1));
        Assert.Equal(11, command.OptionalInt("end-column", 1));
    }

    [Fact]
    public void Parse_ParsesImplementationsMaxResults()
    {
        var command = CliParser.Parse(["implementations", "--target", "repo.slnx", "--file", "Program.cs", "--line", "8", "--column", "6", "--max-results", "3"]);

        Assert.Equal("implementations", command.Name);
        Assert.Equal(3, command.OptionalInt("max-results", 1, 1));
    }

    [Fact]
    public void Parse_ReturnsCommandHelp_WhenCommandAsksForHelp()
    {
        var command = CliParser.Parse(["symbols", "--help"]);

        Assert.True(command.IsHelp);
        Assert.Equal("symbols", command.HelpSubject?.Name);
    }

    [Fact]
    public void Parse_ReturnsVersionHelp_WhenTopLevelVersionFlagAsksForHelp()
    {
        var command = CliParser.Parse(["--version", "--help"]);

        Assert.True(command.IsHelp);
        Assert.Equal("version", command.HelpSubject?.Name);
    }

    [Fact]
    public void Parse_RejectsMissingRequiredOption()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse(["definition", "--target", "repo.slnx", "--file", "Program.cs"]));

        Assert.Equal("definition", exception.CommandName);
        Assert.Contains("Missing required option '--line'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsUnexpectedPositional_ForVersionCommand()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse(["version", "extra"]));

        Assert.Equal("version", exception.CommandName);
        Assert.Contains("Unexpected positional argument 'extra'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsMissingDocumentSelector()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse(["document-text", "--target", "repo.slnx"]));

        Assert.Equal("document-text", exception.CommandName);
        Assert.Contains("Exactly one of '--file' or '--document-key' is required.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsMultipleDocumentSelectors()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse([
            "definition",
            "--target", "repo.slnx",
            "--file", "Program.cs",
            "--document-key", "doc_123",
            "--line", "10",
            "--column", "4",
        ]));

        Assert.Equal("definition", exception.CommandName);
        Assert.Contains("Exactly one of '--file' or '--document-key' is required.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsRangeColumnWithoutLine()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse([
            "document-text",
            "--target", "repo.slnx",
            "--file", "Program.cs",
            "--start-column", "3",
        ]));

        Assert.Equal("document-text", exception.CommandName);
        Assert.Contains("requires '--start-line'", exception.Message, StringComparison.Ordinal);
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
