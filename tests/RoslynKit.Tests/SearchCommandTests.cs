namespace RoslynKit.Tests;

/// <summary>
/// Exercises the in-process index and search commands against a repository-local test database.
/// </summary>
[Collection("Search command integration")]
public sealed class SearchCommandTests
{
    private const string ConfigurationMethodDisplayName = "FixtureApp.ConfigurationValidator.ValidateConfiguration";

    [Fact]
    public async Task Search_AutomaticallyCreatesAnInitialIndexForAResponsibilityQuery()
    {
        await using var area = SearchCommandTestArea.Create();
        Assert.False(File.Exists(area.DatabasePath));

        var result = await ExecuteSearchAsync(
            area,
            "where is configuration validation performed",
            "--max-results", "50");

        Assert.Equal(SearchIndexState.Fresh, result.IndexState);
        Assert.True(File.Exists(area.DatabasePath));
        Assert.True(result.TotalCount > 0);
        Assert.Contains(result.Hits, hit => hit.DisplayName == ConfigurationMethodDisplayName);
    }

    [Fact]
    public async Task Search_ReturnsThePrimaryDeclarationWithinThreeResultsForAResponsibilityQuery()
    {
        await using var area = SearchCommandTestArea.Create();

        var result = await ExecuteSearchAsync(
            area,
            "where does application validate configuration snapshot",
            "--max-results", "3");

        Assert.Equal(3, result.ReturnedCount);
        Assert.Contains(result.Hits, hit => hit.DisplayName == ConfigurationMethodDisplayName);
    }

    [Fact]
    public async Task Index_ReusesAnUnchangedTargetWithoutRebuilding()
    {
        await using var area = SearchCommandTestArea.Create();

        var initial = await ExecuteIndexAsync(area);
        var reused = await ExecuteIndexAsync(area);

        Assert.Equal(SearchIndexState.Fresh, initial.IndexState);
        Assert.True(initial.SymbolCount > 0);
        Assert.False(reused.Rebuilt);
        Assert.Equal(initial.SymbolCount, reused.SymbolCount);
    }

    [Fact]
    public async Task Index_RebuildForcesAFullRefreshForAnOtherwiseFreshTarget()
    {
        await using var area = SearchCommandTestArea.Create();

        var initial = await ExecuteIndexAsync(area);
        var rebuilt = await ExecuteIndexAsync(area, "--rebuild");

        Assert.Equal(SearchIndexState.Fresh, rebuilt.IndexState);
        Assert.True(rebuilt.Rebuilt);
        Assert.Equal(initial.SymbolCount, rebuilt.SymbolCount);
    }

    [Fact]
    public async Task Search_DefaultResultLimitReturnsTwentyOfMoreThanTwentyMatches()
    {
        await using var area = SearchCommandTestArea.Create();

        var result = await ExecuteSearchAsync(area, "configuration validation rule");

        Assert.Equal(20, result.ReturnedCount);
        Assert.True(result.TotalCount > result.ReturnedCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Search_ProjectAndKindFiltersReturnOnlyMatchingFixtureMethods()
    {
        await using var area = SearchCommandTestArea.Create();

        var result = await ExecuteSearchAsync(
            area,
            "configuration validation",
            "--project", TestPaths.FixtureProjectPath(),
            "--kind", "method",
            "--max-results", "50");

        Assert.NotEmpty(result.Hits);
        Assert.All(result.Hits, hit => Assert.Equal("method", hit.Kind));
        Assert.Contains(result.Hits, hit => hit.DisplayName == ConfigurationMethodDisplayName);
    }

    [Fact]
    public async Task Search_ReturnsNavigationIdentityFullRangeAndNormalizedSourceDerivedExcerpt()
    {
        await using var area = SearchCommandTestArea.Create();

        var result = await ExecuteSearchAsync(
            area,
            "configuration validation performed",
            "--max-results", "50");
        var hit = Assert.Single(result.Hits, hit => hit.DisplayName == ConfigurationMethodDisplayName);

        Assert.StartsWith(
            "M:FixtureApp.ConfigurationValidator.ValidateConfiguration",
            Assert.IsType<string>(hit.SymbolId),
            StringComparison.Ordinal);
        var hitPath = Assert.IsType<string>(hit.Location.Path);
        Assert.Equal(
            TestPaths.RepoFile("tests", "FixtureWorkspace", "App", "SearchExamples.cs"),
            hitPath,
            ignoreCase: OperatingSystem.IsWindows());
        Assert.True(Path.IsPathFullyQualified(hitPath));
        Assert.True(hit.Location.Line > 0);
        Assert.True(hit.Location.Column > 0);
        Assert.True(hit.Location.EndLine >= hit.Location.Line);
        Assert.True(hit.Location.EndColumn > 0);
        Assert.Equal(
            "Performs configuration validation before the application continues with a configuration snapshot.",
            hit.Excerpt);
    }

    [Fact]
    public async Task Search_RefreshesAChangedSourceFileBeforeReturningNewMatches()
    {
        await using var area = SearchCommandTestArea.Create();
        var sourcePath = TestPaths.RepoFile("tests", "FixtureWorkspace", "App", "SearchExamples.cs");
        var original = await File.ReadAllTextAsync(sourcePath, TestContext.Current.CancellationToken);
        const string originalDocumentation = "Performs configuration validation before the application continues with a configuration snapshot.";
        const string refreshedDocumentation = "Applies the compatibility safeguard before the application accepts a configuration snapshot.";
        Assert.Contains(originalDocumentation, original, StringComparison.Ordinal);

        try
        {
            var beforeChange = await ExecuteSearchAsync(area, "compatibility safeguard");
            Assert.DoesNotContain(beforeChange.Hits, hit => hit.DisplayName == ConfigurationMethodDisplayName);

            var changed = original.Replace(originalDocumentation, refreshedDocumentation, StringComparison.Ordinal);
            Assert.NotEqual(original, changed);
            await File.WriteAllTextAsync(sourcePath, changed, TestContext.Current.CancellationToken);

            var afterChange = await ExecuteSearchAsync(area, "compatibility safeguard");
            var hit = Assert.Single(afterChange.Hits, hit => hit.DisplayName == ConfigurationMethodDisplayName);

            Assert.Equal(SearchIndexState.Fresh, afterChange.IndexState);
            Assert.Equal(refreshedDocumentation, hit.Excerpt);
        }
        finally
        {
            await File.WriteAllTextAsync(sourcePath, original, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Search_ReturnsTheExistingPartitionAsStaleWhileAnotherWriterOwnsTheDatabase()
    {
        await using var area = SearchCommandTestArea.Create();
        await ExecuteIndexAsync(area);

        var markerPath = TestPaths.RepoFile(
            "tests",
            "FixtureWorkspace",
            "App",
            $"search-stale-{Guid.NewGuid():N}.marker");

        try
        {
            await File.WriteAllTextAsync(markerPath, "stale index marker", TestContext.Current.CancellationToken);

            var index = new SqliteSearchIndex(area.DatabasePath);
            await using var writerLease = await index.AcquireWriterLeaseAsync(
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken);

            var result = await ExecuteSearchAsync(area, "configuration validation", "--max-results", "50");

            Assert.Equal(SearchIndexState.Stale, result.IndexState);
            Assert.Contains(result.Hits, hit => hit.DisplayName == ConfigurationMethodDisplayName);
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    private static async Task<IndexResult> ExecuteIndexAsync(
        SearchCommandTestArea area,
        params string[] additionalArguments)
    {
        var arguments = new List<string>
        {
            "index",
            "--target", TestPaths.FixtureProjectPath(),
            "--index-path", area.DatabasePath,
        };
        arguments.AddRange(additionalArguments);

        var command = CliParser.Parse(arguments);
        var result = await RoslynCommandExecutor.ExecuteAsync(command, TestContext.Current.CancellationToken);
        return Assert.IsType<IndexResult>(result);
    }

    private static async Task<SearchResult> ExecuteSearchAsync(
        SearchCommandTestArea area,
        string query,
        params string[] additionalArguments)
    {
        var arguments = new List<string>
        {
            "search",
            "--target", TestPaths.FixtureProjectPath(),
            "--index-path", area.DatabasePath,
            "--query", query,
        };
        arguments.AddRange(additionalArguments);

        var command = CliParser.Parse(arguments);
        var result = await RoslynCommandExecutor.ExecuteAsync(command, TestContext.Current.CancellationToken);
        return Assert.IsType<SearchResult>(result);
    }

    private sealed class SearchCommandTestArea : IAsyncDisposable
    {
        private SearchCommandTestArea(string directoryPath)
        {
            DirectoryPath = directoryPath;
        }

        public string DirectoryPath { get; }

        public string DatabasePath => Path.Combine(DirectoryPath, "roslynkit.db");

        public static SearchCommandTestArea Create()
        {
            var directoryPath = TestPaths.RepoFile(
                "artifacts",
                "search-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            return new SearchCommandTestArea(directoryPath);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Prevents test cases that edit the search fixture from running alongside other search command integration tests.
/// </summary>
[CollectionDefinition("Search command integration", DisableParallelization = true)]
public sealed class SearchCommandIntegrationCollection;
