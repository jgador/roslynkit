namespace RoslynKit.Tests;

/// <summary>
/// Verifies symbol-selector command execution against repo and fixture targets.
/// </summary>
public sealed partial class CommandExecutionTests
{
    [Fact]
    public async Task References_BySymbolSelector_FindsPositionResolverCallers()
    {
        var result = await TestPaths.ExecuteCommandAsync<ReferencesResult>(
            "references",
            "--target", TestPaths.SolutionPath(),
            "--symbol", "RoslynKit.PositionResolver.GetPositionAsync");

        Assert.Null(result.Document);
        Assert.Null(result.Line);
        Assert.Null(result.Column);
        Assert.Equal("RoslynKit.PositionResolver.GetPositionAsync", result.Selector);
        Assert.StartsWith("M:RoslynKit.PositionResolver.GetPositionAsync(", result.Symbol.SymbolId, StringComparison.Ordinal);
        Assert.Contains("Validates one-based CLI coordinates", result.Symbol.Documentation!, StringComparison.Ordinal);
        Assert.True(result.Locations.Count >= 3);
        Assert.Contains(result.Locations, location => location.Path?.EndsWith("RoslynCommandExecutor.cs", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task Definition_ByDocIdSelector_ReturnsType()
    {
        var result = await TestPaths.ExecuteCommandAsync<DefinitionResult>(
            "definition",
            "--target", TestPaths.SolutionPath(),
            "--symbol", "T:RoslynKit.PositionResolver");

        Assert.Null(result.Document);
        Assert.Null(result.Line);
        Assert.Equal("T:RoslynKit.PositionResolver", result.Selector);
        Assert.Equal("PositionResolver", result.Symbol.Name);
        Assert.Equal("T:RoslynKit.PositionResolver", result.Symbol.SymbolId);
        Assert.EndsWith(Path.Combine("src", "RoslynKit", "PositionResolver.cs"), result.Symbol.PrimaryLocation?.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Implementations_BySymbolSelector_ReturnsFixtureImplementation()
    {
        var result = await TestPaths.ExecuteCommandAsync<ImplementationsResult>(
            "implementations",
            "--target", TestPaths.FixtureProjectPath(),
            "--symbol", "FixtureApp.IMessageSource");

        Assert.Null(result.Document);
        Assert.Equal("FixtureApp.IMessageSource", result.Selector);
        Assert.Contains(result.Symbols, symbol => symbol.Name == "GeneratedMessageSource");
    }

    [Fact]
    public async Task References_AmbiguousQualifiedName_ListsCandidateDocIds()
    {
        var exception = await Assert.ThrowsAsync<CliUsageException>(() => TestPaths.ExecuteCommandAsync<ReferencesResult>(
            "references",
            "--target", TestPaths.SolutionPath(),
            "--symbol", "RoslynKit.SymbolItem.FromSymbol"));

        Assert.Equal("references", exception.CommandName);
        Assert.Contains("ambiguous", exception.Message, StringComparison.Ordinal);
        Assert.Contains("M:RoslynKit.SymbolItem.FromSymbol(Microsoft.CodeAnalysis.ISymbol,System.String)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SymbolSource_ReturnsFullMethodBlock_ForFixtureMember()
    {
        var result = await TestPaths.ExecuteCommandAsync<SymbolSourceResult>(
            "symbol-source",
            "--target", TestPaths.FixtureProjectPath(),
            "--symbol", "M:FixtureApp.Consumer.Run");

        var declaration = Assert.Single(result.Declarations);
        Assert.Equal("M:FixtureApp.Consumer.Run", result.Selector);
        Assert.StartsWith("public string Run()", declaration.Text, StringComparison.Ordinal);
        Assert.Contains("return source.GetMessage(\"world\");", declaration.Text, StringComparison.Ordinal);
        Assert.EndsWith("}", declaration.Text, StringComparison.Ordinal);
        Assert.True(declaration.Range.EndLine > declaration.Range.Line);
    }

    [Fact]
    public async Task SymbolSource_ReturnsSourceAndGeneratedDeclarations_ForPartialType()
    {
        var result = await TestPaths.ExecuteCommandAsync<SymbolSourceResult>(
            "symbol-source",
            "--target", TestPaths.FixtureProjectPath(),
            "--symbol", "T:FixtureApp.GeneratedMessageSource");

        Assert.Contains(result.Declarations, declaration =>
            declaration.Document.DocumentKind == DocumentKindNames.Source
            && declaration.Text.StartsWith("public sealed partial class GeneratedMessageSource", StringComparison.Ordinal));
        Assert.Contains(result.Declarations, declaration => declaration.Document.DocumentKind == DocumentKindNames.SourceGenerated);
    }

    [Fact]
    public async Task SymbolSource_UnknownSelector_ThrowsUsageError()
    {
        var exception = await Assert.ThrowsAsync<CliUsageException>(() => TestPaths.ExecuteCommandAsync<SymbolSourceResult>(
            "symbol-source",
            "--target", TestPaths.FixtureProjectPath(),
            "--symbol", "FixtureApp.DoesNotExist"));

        Assert.Equal("symbol-source", exception.CommandName);
        Assert.Contains("No symbol found for 'FixtureApp.DoesNotExist'", exception.Message, StringComparison.Ordinal);
    }
}
