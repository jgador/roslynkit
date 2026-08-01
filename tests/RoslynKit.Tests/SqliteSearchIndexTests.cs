using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies persistent SQLite full-text search index storage and query behavior.
/// </summary>
public sealed class SqliteSearchIndexTests
{
    [Fact]
    public async Task ReadMetadataAsync_ReturnsNull_WhenTargetHasNotBeenIndexed()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);

        var metadata = await index.ReadMetadataAsync("target-one", TestContext.Current.CancellationToken);

        Assert.Null(metadata);
    }

    [Fact]
    public async Task ReplaceTargetAsync_UsesWalAndKeepsTargetsPartitioned()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;

        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget("target-one", "C:/repo/one.slnx", "first"),
            [CreateSymbol("one-session", "WorkspaceDaemonSession", "workspace daemon session")],
            cancellationToken);
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget("target-two", "C:/repo/two.slnx", "second"),
            [CreateSymbol("two-fingerprint", "GitWorktreeFingerprintService", "git worktree fingerprint")],
            cancellationToken);

        var first = await index.ReadMetadataAsync("target-one", cancellationToken);
        var second = await index.ReadMetadataAsync("target-two", cancellationToken);
        var firstResults = await index.SearchAsync(
            new SqliteSearchIndexQuery("target-one", ["workspace", "daemon"], MaxResults: 20),
            cancellationToken);
        var secondResults = await index.SearchAsync(
            new SqliteSearchIndexQuery("target-two", ["workspace", "daemon"], MaxResults: 20),
            cancellationToken);

        Assert.Equal("wal", await index.ReadJournalModeAsync(cancellationToken));
        Assert.Equal("first", first!.Fingerprint);
        Assert.Equal("second", second!.Fingerprint);
        Assert.Equal("WorkspaceDaemonSession", Assert.Single(firstResults.Matches).Name);
        Assert.Empty(secondResults.Matches);
    }

    [Fact]
    public async Task ReplaceProjectsAsync_ReplacesSelectedProjectsWithoutChangingAnotherTarget()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget("target-one", "C:/repo/one.slnx", "first"),
            [CreateSymbol("one-old", "OldSymbol", "legacy workspace behavior")],
            cancellationToken);
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget("target-two", "C:/repo/two.slnx", "second"),
            [CreateSymbol("two-current", "CurrentSymbol", "current workspace behavior")],
            cancellationToken);

        await index.ReplaceProjectsAsync(
            new SqliteSearchIndexTarget("target-one", "C:/repo/one.slnx", "third"),
            ["C:/repo/App.csproj"],
            [CreateSymbol("one-new", "NewSymbol", "new workspace behavior")],
            cancellationToken);

        var oldResults = await index.SearchAsync(
            new SqliteSearchIndexQuery("target-one", ["legacy"], MaxResults: 20),
            cancellationToken);
        var newResults = await index.SearchAsync(
            new SqliteSearchIndexQuery("target-one", ["new"], MaxResults: 20),
            cancellationToken);
        var secondResults = await index.SearchAsync(
            new SqliteSearchIndexQuery("target-two", ["current"], MaxResults: 20),
            cancellationToken);

        Assert.Empty(oldResults.Matches);
        Assert.Equal("NewSymbol", Assert.Single(newResults.Matches).Name);
        Assert.Equal("CurrentSymbol", Assert.Single(secondResults.Matches).Name);
        Assert.Equal("third", (await index.ReadMetadataAsync("target-one", cancellationToken))!.Fingerprint);
    }

    [Fact]
    public async Task SearchAsync_UsesFtsPrefixTermsAndDeterministicTieBreakers()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget("target", "C:/repo/app.slnx", "fingerprint"),
            [
                CreateSymbol("a", "AlphaSession", "workspace daemon lifecycle", path: "C:/repo/Alpha.cs"),
                CreateSymbol("b", "BetaSession", "workspace daemon lifecycle", path: "C:/repo/Beta.cs"),
            ],
            cancellationToken);

        var results = await index.SearchAsync(
            new SqliteSearchIndexQuery("target", ["worksp", "daemon"], MaxResults: 20),
            cancellationToken);
        var limitedResults = await index.SearchAsync(
            new SqliteSearchIndexQuery("target", ["worksp", "daemon"], MaxResults: 1),
            cancellationToken);

        Assert.Equal(["AlphaSession", "BetaSession"], results.Matches.Select(static result => result.Name));
        Assert.Equal(2, results.TotalMatchCount);
        Assert.Equal(2, limitedResults.TotalMatchCount);
        Assert.Equal("AlphaSession", Assert.Single(limitedResults.Matches).Name);
    }

    [Fact]
    public async Task SearchAsync_SelectsDocumentationExcerptBeforeCommentsSignatureAndBody()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget("target", "C:/repo/app.slnx", "fingerprint"),
            [CreateSymbol(
                "match",
                "RefreshWorkspace",
                "workspace refresh",
                documentation: "Refreshes   the workspace before searching.",
                comments: "workspace comment",
                body: "RefreshWorkspace();")],
            cancellationToken);

        var result = await index.SearchAsync(
            new SqliteSearchIndexQuery("target", ["workspace"], MaxResults: 20),
            cancellationToken);

        var match = Assert.Single(result.Matches);
        Assert.Equal("Refreshes the workspace before searching.", match.Excerpt);
        Assert.Equal(10, match.Line);
        Assert.Equal(5, match.Column);
        Assert.Equal(10, match.EndLine);
        Assert.Equal(20, match.EndColumn);
    }

    [Fact]
    public async Task SearchAsync_AppliesProjectAndKindFilters()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget("target", "C:/repo/app.slnx", "fingerprint"),
            [
                CreateSymbol("method", "RefreshMethod", "workspace refresh", kind: "Method"),
                CreateSymbol(
                    "property",
                    "RefreshProperty",
                    "workspace refresh",
                    projectPath: "C:/repo/Other.csproj",
                    kind: "Property"),
            ],
            cancellationToken);

        var result = await index.SearchAsync(
            new SqliteSearchIndexQuery(
                "target",
                ["workspace"],
                ProjectPaths: ["C:/repo/Other.csproj"],
                Kinds: ["Property"],
                MaxResults: 20),
            cancellationToken);

        Assert.Equal(1, result.TotalMatchCount);
        Assert.Equal("RefreshProperty", Assert.Single(result.Matches).Name);
    }

    [Fact]
    public async Task SearchAsync_BoundsLateBodyExcerptAroundTheMatchingTerm()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget("target", "C:/repo/app.slnx", "fingerprint"),
            [CreateSymbol(
                "late-body",
                "RefreshWorkspace",
                "workspace refresh",
                body: $"{new string('x', 400)} workspace body match")],
            cancellationToken);

        var result = await index.SearchAsync(
            new SqliteSearchIndexQuery("target", ["workspace"], MaxResults: 20),
            cancellationToken);

        var excerpt = Assert.IsType<string>(Assert.Single(result.Matches).Excerpt);
        Assert.Contains("workspace", excerpt, StringComparison.OrdinalIgnoreCase);
        Assert.True(excerpt.Length <= 320);
    }

    [Fact]
    public async Task ReadApis_ReturnEmptyResults_WhenAValidDatabaseHasNoSchemaYet()
    {
        await using var area = SearchIndexTestArea.Create();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = area.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        }

        var index = new SqliteSearchIndex(area.DatabasePath);
        var metadata = await index.ReadMetadataAsync("target", TestContext.Current.CancellationToken);
        var search = await index.SearchAsync(
            new SqliteSearchIndexQuery("target", ["workspace"], MaxResults: 20),
            TestContext.Current.CancellationToken);

        Assert.Null(metadata);
        Assert.Equal(0, search.TotalMatchCount);
        Assert.Empty(search.Matches);
    }

    [Fact]
    public async Task ReadMetadataAsync_RejectsNonSqliteIndexFilesWithActionableError()
    {
        await using var area = SearchIndexTestArea.Create();
        await File.WriteAllTextAsync(area.DatabasePath, "not a sqlite database", TestContext.Current.CancellationToken);
        var index = new SqliteSearchIndex(area.DatabasePath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => index.ReadMetadataAsync(
            "target",
            TestContext.Current.CancellationToken));

        Assert.Contains("not a valid SQLite database", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadMetadataAsync_RejectsIncompleteSchemaBeforeSearch()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget("target", "C:/repo/app.slnx", "fingerprint"),
            [CreateSymbol("existing", "ExistingWorkspace", "existing workspace")],
            cancellationToken);
        await ExecuteSqlAsync(area.DatabasePath, "DROP TABLE search_index_fts;", cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => index.ReadMetadataAsync(
            "target",
            cancellationToken));

        Assert.Contains("search_index_fts", exception.Message, StringComparison.Ordinal);
        Assert.Contains("incomplete", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadMetadataAsync_RejectsSchemaVersionMismatchWithRebuildGuidance()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget("target", "C:/repo/app.slnx", "fingerprint"),
            [CreateSymbol("existing", "ExistingWorkspace", "existing workspace")],
            cancellationToken);
        await ExecuteSqlAsync(
            area.DatabasePath,
            "UPDATE search_index_schema SET schema_version = 999 WHERE schema_key = 1;",
            cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => index.ReadMetadataAsync(
            "target",
            cancellationToken));

        Assert.Contains("schema version 999", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Delete the index database", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriterLease_BlocksAnotherWriterWhileReadersSeeTheLastCommittedIndex()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget("target", "C:/repo/app.slnx", "first"),
            [CreateSymbol("old", "OldWorkspace", "legacy workspace")],
            cancellationToken);

        await using var lease = await index.AcquireWriterLeaseAsync(TimeSpan.FromSeconds(1), cancellationToken);
        Assert.Equal("first", (await lease.ReadMetadataAsync("target", cancellationToken))!.Fingerprint);
        await lease.ReplaceTargetAsync(
            new SqliteSearchIndexTarget("target", "C:/repo/app.slnx", "second"),
            [CreateSymbol("new", "NewWorkspace", "current workspace")],
            cancellationToken);

        var staleSnapshot = await index.ReadSearchSnapshotAsync(
            new SqliteSearchIndexQuery("target", ["legacy"], MaxResults: 20),
            cancellationToken);
        var probeElapsed = Stopwatch.StartNew();
        await Assert.ThrowsAsync<SqliteSearchIndexWriterLeaseUnavailableException>(
            () => index.AcquireWriterLeaseAsync(TimeSpan.Zero, cancellationToken));
        probeElapsed.Stop();

        Assert.Equal("first", staleSnapshot.Metadata!.Fingerprint);
        Assert.Equal("OldWorkspace", Assert.Single(staleSnapshot.SearchResult.Matches).Name);
        Assert.True(probeElapsed.Elapsed < TimeSpan.FromSeconds(2));
        await lease.CommitAsync(cancellationToken);

        var committedSnapshot = await index.ReadSearchSnapshotAsync(
            new SqliteSearchIndexQuery("target", ["current"], MaxResults: 20),
            cancellationToken);
        Assert.Equal("second", committedSnapshot.Metadata!.Fingerprint);
        Assert.Equal("NewWorkspace", Assert.Single(committedSnapshot.SearchResult.Matches).Name);
    }

    [Fact]
    public async Task WriterLease_DisposalRollsBackReplacementAfterCancellation()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget("target", "C:/repo/app.slnx", "first"),
            [CreateSymbol("old", "OldWorkspace", "legacy workspace")],
            cancellationToken);

        await using (var lease = await index.AcquireWriterLeaseAsync(TimeSpan.FromSeconds(1), cancellationToken))
        {
            await lease.ReplaceTargetAsync(
                new SqliteSearchIndexTarget("target", "C:/repo/app.slnx", "second"),
                [CreateSymbol("new", "NewWorkspace", "current workspace")],
                cancellationToken);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.ReplaceTargetAsync(
                new SqliteSearchIndexTarget("target", "C:/repo/app.slnx", "third"),
                [CreateSymbol("canceled", "CanceledWorkspace", "canceled workspace")],
                canceled.Token));
        }

        var result = await index.SearchAsync(
            new SqliteSearchIndexQuery("target", ["legacy"], MaxResults: 20),
            cancellationToken);
        Assert.Equal("OldWorkspace", Assert.Single(result.Matches).Name);
    }

    [Fact]
    public async Task WriterLease_UpdateTargetMetadataAsync_PreservesIndexedSymbols()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget("target", "C:/repo/app.slnx", "first"),
            [CreateSymbol("existing", "ExistingWorkspace", "existing workspace")],
            cancellationToken);

        await using (var lease = await index.AcquireWriterLeaseAsync(TimeSpan.FromSeconds(1), cancellationToken))
        {
            await lease.UpdateTargetMetadataAsync(
                new SqliteSearchIndexTarget("target", "C:/repo/app.slnx", "second"),
                cancellationToken);
            var pendingMetadata = await lease.ReadMetadataAsync("target", cancellationToken);
            Assert.Equal("second", pendingMetadata!.Fingerprint);
            Assert.Equal(1, pendingMetadata.SymbolCount);
            await lease.CommitAsync(cancellationToken);
        }

        var result = await index.SearchAsync(
            new SqliteSearchIndexQuery("target", ["existing"], MaxResults: 20),
            cancellationToken);
        var metadata = await index.ReadMetadataAsync("target", cancellationToken);
        Assert.Equal("ExistingWorkspace", Assert.Single(result.Matches).Name);
        Assert.Equal("second", metadata!.Fingerprint);
        Assert.Equal(1, metadata.SymbolCount);
    }

    [Fact]
    public async Task SearchAsync_OrdersBroaderQueryCoverageBeforeBm25()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget("target", "C:/repo/app.slnx", "fingerprint"),
            [
                CreateSymbol("one-term", "AlphaTitle", "alpha"),
                CreateSymbol("two-terms", "GenericTitle", "alpha beta"),
            ],
            cancellationToken);

        var results = await index.SearchAsync(
            new SqliteSearchIndexQuery("target", ["alpha", "beta"], MaxResults: 20),
            cancellationToken);

        Assert.Equal(["GenericTitle", "AlphaTitle"], results.Matches.Select(static match => match.Name));
        Assert.Equal([2, 1], results.Matches.Select(static match => match.QueryTermCoverage));
    }

    [Fact]
    public async Task SearchAsync_DoesNotUseSubstringOnlyDocumentationForExcerpt()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget("target", "C:/repo/app.slnx", "fingerprint"),
            [CreateSymbol(
                "get-settings",
                "GetSettings",
                "get settings",
                documentation: "Selects the target settings source.")],
            cancellationToken);

        var result = await index.SearchAsync(
            new SqliteSearchIndexQuery("target", ["get"], MaxResults: 20),
            cancellationToken);

        var excerpt = Assert.IsType<string>(Assert.Single(result.Matches).Excerpt);
        Assert.DoesNotContain("target settings", excerpt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetSettings", excerpt, StringComparison.Ordinal);
    }

    private static SqliteSearchIndexSymbol CreateSymbol(
        string key,
        string name,
        string details,
        string? path = null,
        string? documentation = null,
        string? comments = null,
        string? body = null,
        string projectPath = "C:/repo/App.csproj",
        string kind = "Method")
    {
        return new SqliteSearchIndexSymbol(
            key,
            projectPath,
            "App",
            kind,
            name,
            $"App.{name}",
            $"M:App.{name}",
            path ?? "C:/repo/App.cs",
            10,
            5,
            10,
            20,
            documentation,
            $"void {name}()",
            comments,
            body,
            name,
            "app",
            details,
            "app cs",
            details);
    }

    private static async Task ExecuteSqlAsync(string databasePath, string commandText, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class SearchIndexTestArea : IAsyncDisposable
    {
        private SearchIndexTestArea(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public string DatabasePath => System.IO.Path.Combine(Path, "roslynkit.db");

        public static SearchIndexTestArea Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "roslynkit-tests",
                "sqlite-search-index",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new SearchIndexTestArea(path);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
