namespace RoslynKit.Tests;

/// <summary>
/// Verifies parser binding, option handling, and usage validation for RoslynKit command lines.
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
    public void Parse_RejectsDocumentKeySelector()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse([
            "quick-info",
            "--target", "repo.slnx",
            "--document-key", "doc_123",
            "--line", "7",
            "--column", "15",
        ]));

        Assert.Equal("quick-info", exception.CommandName);
        Assert.Contains("Unknown option '--document-key'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AcceptsWholeFileDocumentTextFileSelector()
    {
        var command = CliParser.Parse(["document-text", "--target", "repo.slnx", "--file", "Program.cs"]);

        Assert.Equal("document-text", command.Name);
        Assert.Equal("Program.cs", command.Required("file"));
    }

    [Fact]
    public void Parse_AcceptsDocumentContextOptionsWithFileSelector()
    {
        var command = CliParser.Parse([
            "document-text",
            "--target", "repo.slnx",
            "--file", "Program.cs",
            "--project", "App.csproj",
            "--tfm", "net10.0",
            "--document-kind", "source",
        ]);

        Assert.Equal("document-text", command.Name);
        Assert.Equal("Program.cs", command.Required("file"));
        Assert.Equal("App.csproj", command.Required("project"));
        Assert.Equal("net10.0", command.Required("tfm"));
        Assert.Equal("source", command.Required("document-kind"));
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
        Assert.Contains("Missing required option '--file'.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsContextOptionsWithoutFile()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse([
            "document-text",
            "--target", "repo.slnx",
            "--project", "App.csproj",
        ]));

        Assert.Equal("document-text", exception.CommandName);
        Assert.Contains("Missing required option '--file'.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsUnknownDocumentKind()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse([
            "document-text",
            "--target", "repo.slnx",
            "--file", "Program.cs",
            "--document-kind", "banana",
        ]));

        Assert.Equal("document-text", exception.CommandName);
        Assert.Contains("Unknown document kind 'banana'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsLegacyDocumentTextRangeOptions()
    {
        var legacyOptions = new[]
        {
            ("start-line", "5"),
            ("start-column", "2"),
            ("end-line", "8"),
            ("end-column", "11"),
        };

        foreach (var (option, value) in legacyOptions)
        {
            var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse([
                "document-text",
                "--target", "repo.slnx",
                "--file", "Program.cs",
                $"--{option}", value,
            ]));

            Assert.Equal("document-text", exception.CommandName);
            Assert.Contains($"Option '--{option}' is no longer supported.", exception.Message, StringComparison.Ordinal);
            Assert.Contains("reads the entire resolved document only", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Parse_RejectsAmbiguousAbbreviation()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse(["diagnostics", "--target", "repo.slnx", "--include"]));

        Assert.Equal("diagnostics", exception.CommandName);
        Assert.Contains("ambiguous", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsFormatOption_AsUnknown()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse([
            "symbols",
            "--target", "repo.slnx",
            "--query", "CliApplication",
            "--format", "text",
        ]));

        Assert.Equal("symbols", exception.CommandName);
        Assert.Contains("Unknown option '--format' for command 'symbols'.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsDuplicateOption()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse(["workspace", "--target", "a.slnx", "--target", "b.slnx"]));

        Assert.Equal("workspace", exception.CommandName);
        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("definition")]
    [InlineData("references")]
    [InlineData("implementations")]
    public void Parse_AcceptsSymbolSelector_WithoutPositionOptions(string commandName)
    {
        var command = CliParser.Parse([commandName, "--target", "repo.slnx", "--symbol", "RoslynKit.CliApplication"]);

        Assert.Equal(commandName, command.Name);
        Assert.Equal("RoslynKit.CliApplication", command.Required("symbol"));
        Assert.Null(command.Optional("file"));
        Assert.Null(command.OptionalInt("line", 1));
    }

    [Fact]
    public void Parse_RejectsSymbolCombinedWithLine_ForDefinition()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse([
            "definition",
            "--target", "repo.slnx",
            "--symbol", "RoslynKit.CliApplication",
            "--line", "10",
        ]));

        Assert.Equal("definition", exception.CommandName);
        Assert.Contains("Option '--symbol' cannot be combined with", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsSymbolCombinedWithFile_ForImplementations()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse([
            "implementations",
            "--target", "repo.slnx",
            "--symbol", "RoslynKit.CliApplication",
            "--file", "Program.cs",
        ]));

        Assert.Equal("implementations", exception.CommandName);
        Assert.Contains("Option '--symbol' cannot be combined with", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsSymbolCombinedWithDocumentContext()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse([
            "references",
            "--target", "repo.slnx",
            "--symbol", "RoslynKit.CliApplication",
            "--project", "App.csproj",
        ]));

        Assert.Equal("references", exception.CommandName);
        Assert.Contains("Option '--symbol' cannot be combined with", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--project", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsMissingColumn_WhenPositionModeWithoutSymbol()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse([
            "references",
            "--target", "repo.slnx",
            "--file", "Program.cs",
            "--line", "10",
        ]));

        Assert.Equal("references", exception.CommandName);
        Assert.Contains("Missing required option '--column'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AcceptsSymbolSource_WithTargetAndSymbol()
    {
        var command = CliParser.Parse(["symbol-source", "--target", "repo.slnx", "--symbol", "M:RoslynKit.CliParser.Parse(System.Collections.Generic.IReadOnlyList{System.String})"]);

        Assert.Equal("symbol-source", command.Name);
        Assert.StartsWith("M:RoslynKit.CliParser.Parse(", command.Required("symbol"), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AcceptsDocumentLines_WithRequiredRange()
    {
        var command = CliParser.Parse([
            "document-lines",
            "--target", "repo.slnx",
            "--file", "Program.cs",
            "--start-line", "10",
            "--end-line", "14",
        ]);

        Assert.Equal("document-lines", command.Name);
        Assert.Equal("10", command.Required("start-line"));
        Assert.Equal("14", command.Required("end-line"));
    }

    [Fact]
    public void Parse_RejectsMissingEndLine_ForDocumentLines()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse([
            "document-lines",
            "--target", "repo.slnx",
            "--file", "Program.cs",
            "--start-line", "10",
        ]));

        Assert.Equal("document-lines", exception.CommandName);
        Assert.Contains("Missing required option '--end-line'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsMissingSymbol_ForSymbolSource()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse(["symbol-source", "--target", "repo.slnx"]));

        Assert.Equal("symbol-source", exception.CommandName);
        Assert.Contains("Missing required option '--symbol'", exception.Message, StringComparison.Ordinal);
    }
}
