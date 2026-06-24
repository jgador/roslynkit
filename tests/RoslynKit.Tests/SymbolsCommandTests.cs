namespace RoslynKit.Tests;

/// <summary>
/// Verifies symbol search behavior for the symbols command.
/// </summary>
public sealed class SymbolsCommandTests
{
    [Fact]
    public async Task Symbols_Exact_ReturnsClassDeclaration()
    {
        var result = await RunSymbolsAsync("--query", "RoslynCommandExecutor", "--exact", "--kind", "class");

        var symbol = Assert.Single(result.Symbols);
        Assert.Equal("RoslynCommandExecutor", symbol.Name);
        Assert.Equal("NamedType", symbol.Kind);
        Assert.Equal("RoslynKit.RoslynCommandExecutor", symbol.DisplayName);
        Assert.NotNull(symbol.PrimaryLocation);
    }

    [Fact]
    public async Task Symbols_Pattern_ReturnsCamelCaseDeclaration()
    {
        var result = await RunSymbolsAsync("--query", "RCE", "--kind", "class");

        Assert.Contains(result.Symbols, symbol => symbol.Name == "RoslynCommandExecutor");
    }

    [Fact]
    public async Task Symbols_Kind_FiltersMethods()
    {
        var result = await RunSymbolsAsync("--query", "ExecuteAsync", "--exact", "--kind", "method");

        Assert.NotEmpty(result.Symbols);
        Assert.All(result.Symbols, symbol => Assert.Equal("Method", symbol.Kind));
        Assert.Contains(result.Symbols, symbol => symbol.Name == "ExecuteAsync" && symbol.ContainingType == "RoslynKit.RoslynCommandExecutor");
    }

    [Fact]
    public async Task Symbols_MaxResults_Truncates()
    {
        var result = await RunSymbolsAsync("--query", "Async", "--kind", "method", "--max-results", "1");

        Assert.Equal(1, result.ReturnedCount);
        Assert.True(result.TotalCount > result.ReturnedCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Symbols_CaseSensitive_ExcludesDifferentCase()
    {
        var result = await RunSymbolsAsync("--query", "roslyncommandexecutor", "--exact", "--case-sensitive", "--kind", "class");

        Assert.Empty(result.Symbols);
        Assert.Equal(0, result.TotalCount);
    }

    private static async Task<SymbolsResult> RunSymbolsAsync(params string[] args)
    {
        var commandArgs = new[] { "symbols", "--target", SolutionPath() }.Concat(args).ToArray();
        var command = CliParser.Parse(commandArgs);
        var result = await RoslynCommandExecutor.ExecuteAsync(command, TestContext.Current.CancellationToken);

        return Assert.IsType<SymbolsResult>(result);
    }

    private static string SolutionPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var solutionPath = Path.Combine(directory.FullName, "RoslynKit.slnx");
            if (File.Exists(solutionPath))
            {
                return solutionPath;
            }
        }

        throw new InvalidOperationException("Could not locate RoslynKit.slnx from the test output directory.");
    }
}
