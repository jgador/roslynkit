namespace RoslynKit.Tests;

/// <summary>
/// Verifies the public parser contract for indexed symbol search.
/// </summary>
public sealed class SearchCliContractTests
{
    [Fact]
    public void Parse_IndexAcceptsRequiredOptionsAndRebuildFlag()
    {
        var command = CliParser.Parse(
        [
            "index",
            "--target", "repo.slnx",
            "--index-path", "artifacts\\roslynkit.db",
            "--rebuild",
        ]);

        Assert.Equal("index", command.Name);
        Assert.Equal("repo.slnx", command.Required("target"));
        Assert.Equal("artifacts\\roslynkit.db", command.Required("index-path"));
        Assert.True(command.Flag("rebuild"));
    }

    [Fact]
    public void Parse_IndexAcceptsImplicitRepositoryAndDatabase()
    {
        var command = CliParser.Parse(["index"]);

        Assert.Null(command.Optional("target"));
        Assert.Null(command.Optional("index-path"));
    }

    [Fact]
    public void Parse_SearchAcceptsAllSupportedOptions()
    {
        var command = CliParser.Parse(
        [
            "search",
            "--target", "repo.slnx",
            "--index-path", "artifacts\\roslynkit.db",
            "--query", "workspace daemon session",
            "--project", "src\\RoslynKit\\RoslynKit.csproj",
            "--kind", "class",
            "--max-results", "7",
        ]);

        Assert.Equal("search", command.Name);
        Assert.Equal("workspace daemon session", command.Required("query"));
        Assert.Equal("src\\RoslynKit\\RoslynKit.csproj", command.Required("project"));
        Assert.Equal("class", command.Required("kind"));
        Assert.Equal(7, command.OptionalInt("max-results", 20, 1));
    }

    [Fact]
    public void Parse_SearchUsesTheDefaultResultLimitWhenNoOptionalFiltersAreProvided()
    {
        var command = CliParser.Parse(
        [
            "search",
            "--target", "repo.slnx",
            "--index-path", "artifacts\\roslynkit.db",
            "--query", "workspace daemon session",
        ]);

        Assert.Equal(20, command.OptionalInt("max-results", 20, 1));
        Assert.Null(command.Optional("project"));
        Assert.Null(command.Optional("kind"));
    }

    [Fact]
    public void Parse_SearchAcceptsImplicitRepositoryAndDatabase()
    {
        var command = CliParser.Parse(["search", "--query", "workspace catalog"]);

        Assert.Equal("workspace catalog", command.Required("query"));
        Assert.Null(command.Optional("target"));
        Assert.Null(command.Optional("index-path"));
    }

    [Fact]
    public void Parse_IndexRebuildFlagDefaultsToFalse()
    {
        var command = CliParser.Parse(
        [
            "index",
            "--target", "repo.slnx",
            "--index-path", "artifacts\\roslynkit.db",
        ]);

        Assert.False(command.Flag("rebuild"));
    }

    [Theory]
    [InlineData("search")]
    [InlineData("search", "--target", "repo.slnx")]
    [InlineData("search", "--target", "repo.slnx", "--index-path", "artifacts\\roslynkit.db")]
    public void Parse_RejectsMissingRequiredSearchQuery(params string[] arguments)
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse(arguments));

        Assert.Contains("Missing required option", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_IndexRejectsSearchOnlyOption()
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse(
        [
            "index",
            "--target", "repo.slnx",
            "--index-path", "artifacts\\roslynkit.db",
            "--query", "workspace daemon",
        ]));

        Assert.Equal("index", exception.CommandName);
        Assert.Contains("Unknown option '--query'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("index", "--target", "repo.slnx", "--index-path", "artifacts\\roslynkit.db", "--include-generated")]
    [InlineData("search", "--target", "repo.slnx", "--index-path", "artifacts\\roslynkit.db", "--query", "workspace daemon", "--include-generated")]
    public void Parse_IndexAndSearchRejectIncludeGenerated(params string[] arguments)
    {
        var exception = Assert.Throws<CliUsageException>(() => CliParser.Parse(arguments));

        Assert.Contains("Unknown option '--include-generated'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DiagnosticsRetainsIncludeGenerated()
    {
        var command = CliParser.Parse(
        [
            "diagnostics",
            "--target", "repo.slnx",
            "--include-generated",
        ]);

        Assert.True(command.Flag("include-generated"));
    }

    [Fact]
    public async Task ExecuteSearch_RejectsUppercaseSymbolKindsBeforeLoadingTheWorkspace()
    {
        var command = CliParser.Parse(
        [
            "search",
            "--target", "not-loaded.slnx",
            "--index-path", "artifacts\\roslynkit.db",
            "--query", "workspace daemon session",
            "--kind", "Class",
        ]);

        var exception = await Assert.ThrowsAsync<CliUsageException>(
            () => RoslynCommandExecutor.ExecuteAsync(command, TestContext.Current.CancellationToken));

        Assert.Equal("search", exception.CommandName);
        Assert.Contains("Unknown symbol kind 'Class'", exception.Message, StringComparison.Ordinal);
    }

}
