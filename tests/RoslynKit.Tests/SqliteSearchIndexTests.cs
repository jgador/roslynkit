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

        var metadata = await index.ReadMetadataAsync(RelativePath("target-one"), TestContext.Current.CancellationToken);

        Assert.Null(metadata);
    }

    [Fact]
    public async Task ReplaceTargetAsync_UsesWalAndKeepsTargetsPartitioned()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;

        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target-one"), "first"),
            [CreateSymbol("one-session", "WorkspaceDaemonSession", "workspace daemon session")],
            cancellationToken);
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target-two"), "second"),
            [CreateSymbol("two-fingerprint", "GitWorktreeFingerprintService", "git worktree fingerprint")],
            cancellationToken);

        var first = await index.ReadMetadataAsync(RelativePath("target-one"), cancellationToken);
        var second = await index.ReadMetadataAsync(RelativePath("target-two"), cancellationToken);
        var firstResults = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target-one"), ["workspace", "daemon"], MaxResults: 20),
            cancellationToken);
        var secondResults = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target-two"), ["workspace", "daemon"], MaxResults: 20),
            cancellationToken);

        Assert.Equal("wal", await index.ReadJournalModeAsync(cancellationToken));
        Assert.Equal("first", first!.Fingerprint);
        Assert.Equal("second", second!.Fingerprint);
        Assert.Equal("WorkspaceDaemonSession", Assert.Single(firstResults.Matches).Name);
        Assert.Empty(secondResults.Matches);
    }

    [Fact]
    public async Task ReplaceTargetAsync_OmitsTargetPathAndPersistsCanonicalRelativeValues()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        const string targetIdentity = "__repository__";
        const string projectPath = "tests/FixtureWorkspace/App/App.csproj";
        const string sourcePath = "tests/FixtureWorkspace/App/Source.cs";
        const string symbolKey = "tests/FixtureWorkspace/App/App.csproj|tests/FixtureWorkspace/App/Source.cs|M:FixtureApp.IMessageSource.GetMessage(System.String)|0";
        const string pathTokens = "tests fixtureworkspace app source cs";

        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath(targetIdentity), "fingerprint"),
            [CreateSymbol(
                symbolKey,
                "GetMessage",
                "fixture message",
                path: sourcePath,
                projectPath: projectPath,
                pathTokens: pathTokens)],
            cancellationToken);

        var metadata = await index.ReadMetadataAsync(RelativePath(targetIdentity), cancellationToken);
        var targetColumns = await ReadColumnNamesAsync(area.DatabasePath, "search_index_targets", cancellationToken);
        var tableNames = await ReadTableNamesAsync(area.DatabasePath, cancellationToken);
        var persisted = await ReadPersistedPathValuesAsync(area.DatabasePath, targetIdentity, cancellationToken);

        Assert.NotNull(metadata);
        Assert.DoesNotContain("search_index_schema", tableNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("target_path", targetColumns, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(targetIdentity, persisted.TargetIdentity);
        Assert.Equal(projectPath, persisted.ProjectPath);
        Assert.Equal(sourcePath, persisted.Path);
        Assert.Equal(symbolKey, persisted.SymbolKey);
        Assert.Equal(pathTokens, persisted.PathTokens);
        Assert.All(
            [persisted.TargetIdentity, persisted.ProjectPath, persisted.Path, persisted.SymbolKey, persisted.PathTokens],
            value => Assert.DoesNotContain(TestPaths.RepositoryRoot(), value, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain('\\', persisted.TargetIdentity);
        Assert.DoesNotContain('\\', persisted.ProjectPath);
        Assert.DoesNotContain('\\', persisted.Path);
        Assert.DoesNotContain('\\', persisted.SymbolKey);
        Assert.DoesNotContain(persisted.TargetIdentity, persisted.SymbolKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadMetadataAsync_RejectsLegacyTargetPathColumn()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target"), "fingerprint"),
            [CreateSymbol("existing", "ExistingWorkspace", "existing workspace")],
            cancellationToken);
        await ExecuteSqlAsync(
            area.DatabasePath,
            "ALTER TABLE search_index_targets ADD COLUMN target_path TEXT NULL;",
            cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => index.ReadMetadataAsync(
            RelativePath("target"),
            cancellationToken));

        Assert.Contains("search_index_targets", exception.Message, StringComparison.Ordinal);
        Assert.Contains("target_path", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Delete the index database", exception.Message, StringComparison.Ordinal);
        Assert.Contains("run index again", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceProjectsAsync_ReplacesSelectedProjectsWithoutChangingAnotherTarget()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target-one"), "first"),
            [CreateSymbol("one-old", "OldSymbol", "legacy workspace behavior")],
            cancellationToken);
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target-two"), "second"),
            [CreateSymbol("two-current", "CurrentSymbol", "current workspace behavior")],
            cancellationToken);

        await index.ReplaceProjectsAsync(
            new SqliteSearchIndexTarget(RelativePath("target-one"), "third"),
            [RelativePath("App.csproj")],
            [CreateSymbol("one-new", "NewSymbol", "new workspace behavior")],
            cancellationToken);

        var oldResults = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target-one"), ["legacy"], MaxResults: 20),
            cancellationToken);
        var newResults = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target-one"), ["new"], MaxResults: 20),
            cancellationToken);
        var secondResults = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target-two"), ["current"], MaxResults: 20),
            cancellationToken);

        Assert.Empty(oldResults.Matches);
        Assert.Equal("NewSymbol", Assert.Single(newResults.Matches).Name);
        Assert.Equal("CurrentSymbol", Assert.Single(secondResults.Matches).Name);
        Assert.Equal("third", (await index.ReadMetadataAsync(RelativePath("target-one"), cancellationToken))!.Fingerprint);
    }

    [Fact]
    public async Task SearchAsync_UsesFtsPrefixTermsAndDeterministicTieBreakers()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target"), "fingerprint"),
            [
                CreateSymbol("a", "AlphaSession", "workspace daemon lifecycle", path: "Alpha.cs"),
                CreateSymbol("b", "BetaSession", "workspace daemon lifecycle", path: "Beta.cs"),
            ],
            cancellationToken);

        var results = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target"), ["worksp", "daemon"], MaxResults: 20),
            cancellationToken);
        var limitedResults = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target"), ["worksp", "daemon"], MaxResults: 1),
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
            new SqliteSearchIndexTarget(RelativePath("target"), "fingerprint"),
            [CreateSymbol(
                "match",
                "RefreshWorkspace",
                "workspace refresh",
                documentation: "Refreshes   the workspace before searching.",
                comments: "workspace comment",
                body: "RefreshWorkspace();")],
            cancellationToken);

        var result = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target"), ["workspace"], MaxResults: 20),
            cancellationToken);

        var match = Assert.Single(result.Matches);
        Assert.Equal("Refreshes the workspace before searching.", match.Excerpt);
        Assert.Equal(SearchExcerptSource.Documentation, match.ExcerptSource);
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
            new SqliteSearchIndexTarget(RelativePath("target"), "fingerprint"),
            [
                CreateSymbol("method", "RefreshMethod", "workspace refresh", kind: "Method"),
                CreateSymbol(
                    "property",
                    "RefreshProperty",
                    "workspace refresh",
                    projectPath: "Other.csproj",
                    kind: "Property"),
            ],
            cancellationToken);

        var result = await index.SearchAsync(
            new SqliteSearchIndexQuery(
                RelativePath("target"),
                ["workspace"],
                ProjectPaths: [RelativePath("Other.csproj")],
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
            new SqliteSearchIndexTarget(RelativePath("target"), "fingerprint"),
            [CreateSymbol(
                "late-body",
                "RefreshWorkspace",
                "workspace refresh",
                body: $"{new string('x', 400)} workspace body match")],
            cancellationToken);

        var result = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target"), ["workspace"], MaxResults: 20),
            cancellationToken);

        var excerpt = Assert.IsType<string>(Assert.Single(result.Matches).Excerpt);
        Assert.Contains("workspace", excerpt, StringComparison.OrdinalIgnoreCase);
        Assert.True(excerpt.Length <= 320);
    }

    [Theory]
    [InlineData(SearchExcerptSource.Documentation, "documentation needle")]
    [InlineData(SearchExcerptSource.Comment, "comment needle")]
    [InlineData(SearchExcerptSource.Signature, "void SearchTarget(needle value)")]
    [InlineData(SearchExcerptSource.Body, "needle body")]
    public async Task SearchAsync_ReportsExcerptSourceForEachIndexedField(
        SearchExcerptSource expectedSource,
        string expectedExcerpt)
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        const string matchToken = "needle";
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target"), "fingerprint"),
            [CreateSymbol(
                "match",
                "SearchTarget",
                matchToken,
                documentation: expectedSource == SearchExcerptSource.Documentation ? expectedExcerpt : null,
                comments: expectedSource == SearchExcerptSource.Comment ? expectedExcerpt : null,
                signature: expectedSource == SearchExcerptSource.Signature ? expectedExcerpt : null,
                body: expectedSource == SearchExcerptSource.Body ? expectedExcerpt : null)],
            cancellationToken);

        var result = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target"), [matchToken], MaxResults: 20),
            cancellationToken);

        var match = Assert.Single(result.Matches);
        Assert.Equal(expectedSource, match.ExcerptSource);
        Assert.Equal(expectedExcerpt, match.Excerpt);
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
        var metadata = await index.ReadMetadataAsync(RelativePath("target"), TestContext.Current.CancellationToken);
        var search = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target"), ["workspace"], MaxResults: 20),
            TestContext.Current.CancellationToken);

        Assert.Null(metadata);
        Assert.Equal(0, search.TotalMatchCount);
        Assert.Empty(search.Matches);
    }

    [Fact]
    public async Task SearchAsync_RejectsDefaultProjectPathFiltersInsteadOfDroppingThem()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => index.SearchAsync(
            new SqliteSearchIndexQuery(
                RelativePath("target"),
                ["workspace"],
                ProjectPaths: [RelativePath("App.csproj"), default],
                MaxResults: 20),
            TestContext.Current.CancellationToken));

        Assert.Contains("Search query project path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceProjectsAsync_RejectsDefaultProjectPathInsteadOfDroppingIt()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => index.ReplaceProjectsAsync(
            new SqliteSearchIndexTarget(RelativePath("target"), "fingerprint"),
            [RelativePath("App.csproj"), default],
            [CreateSymbol("existing", "ExistingWorkspace", "existing workspace")],
            TestContext.Current.CancellationToken));

        Assert.Contains("Search index project path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadMetadataAsync_RejectsNonSqliteIndexFilesWithActionableError()
    {
        await using var area = SearchIndexTestArea.Create();
        await File.WriteAllTextAsync(area.DatabasePath, "not a sqlite database", TestContext.Current.CancellationToken);
        var index = new SqliteSearchIndex(area.DatabasePath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => index.ReadMetadataAsync(
            RelativePath("target"),
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
            new SqliteSearchIndexTarget(RelativePath("target"), "fingerprint"),
            [CreateSymbol("existing", "ExistingWorkspace", "existing workspace")],
            cancellationToken);
        await ExecuteSqlAsync(area.DatabasePath, "DROP TABLE search_index_fts;", cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => index.ReadMetadataAsync(
            RelativePath("target"),
            cancellationToken));

        Assert.Contains("search_index_fts", exception.Message, StringComparison.Ordinal);
        Assert.Contains("incomplete", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("target|App.csproj|App.cs|M:App.Workspace|0")]
    [InlineData("App.csproj|C:/repo/App.cs|M:App.Workspace|0")]
    [InlineData("App.csproj|./App.cs|M:App.Workspace|0")]
    [InlineData("App.csproj|App.cs|M:App.Workspace")]
    public async Task ReplaceTargetAsync_RejectsMalformedOrNoncanonicalSymbolKeyPathComponents(string symbolKey)
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target"), "fingerprint"),
            [CreateSymbol(symbolKey, "Workspace", "workspace")],
            TestContext.Current.CancellationToken));

        Assert.Contains("Search symbol key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceTargetAsync_RejectsSymbolKeyPathComponentsThatDoNotMatchTheSymbol()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target"), "fingerprint"),
            [CreateSymbol("Other.csproj|App.cs|M:App.Workspace|0", "Workspace", "workspace")],
            TestContext.Current.CancellationToken));

        Assert.Contains("must match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceTargetAsync_AcceptsDelimiterCharactersInsideCanonicalPathComponents()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        const string targetIdentity = "targets/target|branch.csproj";
        const string projectPath = "src/App|Variant.csproj";
        const string sourcePath = "src/App|Variant.cs";
        var symbolKey = $"{projectPath}|{sourcePath}|M:App.Workspace|0";

        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath(targetIdentity), "fingerprint"),
            [CreateSymbol(
                symbolKey,
                "Workspace",
                "workspace",
                path: sourcePath,
                projectPath: projectPath)],
            cancellationToken);

        var result = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath(targetIdentity), ["workspace"], MaxResults: 20),
            cancellationToken);

        Assert.Equal(symbolKey, Assert.Single(result.Matches).SymbolKey);
    }

    [Fact]
    public async Task ReplaceTargetAsync_AllowsIdenticalTargetLocalSymbolKeysInDistinctTargetPartitions()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        const string symbolKey = "App.csproj|App.cs|M:App.Workspace|0";

        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target-one"), "first"),
            [CreateSymbol(symbolKey, "FirstWorkspace", "first workspace")],
            cancellationToken);
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target-two"), "second"),
            [CreateSymbol(symbolKey, "SecondWorkspace", "second workspace")],
            cancellationToken);

        var first = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target-one"), ["workspace"], MaxResults: 20),
            cancellationToken);
        var second = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target-two"), ["workspace"], MaxResults: 20),
            cancellationToken);

        Assert.Equal("FirstWorkspace", Assert.Single(first.Matches).Name);
        Assert.Equal("SecondWorkspace", Assert.Single(second.Matches).Name);
        Assert.Equal(symbolKey, Assert.Single(first.Matches).SymbolKey);
        Assert.Equal(symbolKey, Assert.Single(second.Matches).SymbolKey);
    }

    [Fact]
    public async Task WriterLease_BlocksAnotherWriterWhileReadersSeeTheLastCommittedIndex()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target"), "first"),
            [CreateSymbol("old", "OldWorkspace", "legacy workspace")],
            cancellationToken);

        await using var lease = await index.AcquireWriterLeaseAsync(TimeSpan.FromSeconds(1), cancellationToken);
        Assert.Equal("first", (await lease.ReadMetadataAsync(RelativePath("target"), cancellationToken))!.Fingerprint);
        await lease.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target"), "second"),
            [CreateSymbol("new", "NewWorkspace", "current workspace")],
            cancellationToken);

        var staleSnapshot = await index.ReadSearchSnapshotAsync(
            new SqliteSearchIndexQuery(RelativePath("target"), ["legacy"], MaxResults: 20),
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
            new SqliteSearchIndexQuery(RelativePath("target"), ["current"], MaxResults: 20),
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
            new SqliteSearchIndexTarget(RelativePath("target"), "first"),
            [CreateSymbol("old", "OldWorkspace", "legacy workspace")],
            cancellationToken);

        await using (var lease = await index.AcquireWriterLeaseAsync(TimeSpan.FromSeconds(1), cancellationToken))
        {
            await lease.ReplaceTargetAsync(
                new SqliteSearchIndexTarget(RelativePath("target"), "second"),
                [CreateSymbol("new", "NewWorkspace", "current workspace")],
                cancellationToken);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.ReplaceTargetAsync(
                new SqliteSearchIndexTarget(RelativePath("target"), "third"),
                [CreateSymbol("canceled", "CanceledWorkspace", "canceled workspace")],
                canceled.Token));
        }

        var result = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target"), ["legacy"], MaxResults: 20),
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
            new SqliteSearchIndexTarget(RelativePath("target"), "first"),
            [CreateSymbol("existing", "ExistingWorkspace", "existing workspace")],
            cancellationToken);

        await using (var lease = await index.AcquireWriterLeaseAsync(TimeSpan.FromSeconds(1), cancellationToken))
        {
            await lease.UpdateTargetMetadataAsync(
                new SqliteSearchIndexTarget(RelativePath("target"), "second"),
                cancellationToken);
            var pendingMetadata = await lease.ReadMetadataAsync(RelativePath("target"), cancellationToken);
            Assert.Equal("second", pendingMetadata!.Fingerprint);
            Assert.Equal(1, pendingMetadata.SymbolCount);
            await lease.CommitAsync(cancellationToken);
        }

        var result = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target"), ["existing"], MaxResults: 20),
            cancellationToken);
        var metadata = await index.ReadMetadataAsync(RelativePath("target"), cancellationToken);
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
            new SqliteSearchIndexTarget(RelativePath("target"), "fingerprint"),
            [
                CreateSymbol("one-term", "AlphaTitle", "alpha"),
                CreateSymbol("two-terms", "GenericTitle", "alpha beta"),
            ],
            cancellationToken);

        var results = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target"), ["alpha", "beta"], MaxResults: 20),
            cancellationToken);

        Assert.Equal(["GenericTitle", "AlphaTitle"], results.Matches.Select(static match => match.Name));
        Assert.Equal([2, 1], results.Matches.Select(static match => match.QueryTermCoverage));
    }

    [Fact]
    public async Task SearchAsync_PrioritizesNavigableMembersBeforeNamespaces()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target"), "fingerprint"),
            [
                CreateSymbol("namespace", "RoslynKit", "workspace daemon reload", kind: "Namespace"),
                CreateSymbol("method", "ReloadAsync", "reload", kind: "Method"),
            ],
            cancellationToken);

        var results = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target"), ["workspace", "daemon", "reload"], MaxResults: 2),
            cancellationToken);

        Assert.Equal(["ReloadAsync", "RoslynKit"], results.Matches.Select(static match => match.Name));
    }

    [Fact]
    public async Task SearchAsync_PrioritizesProductionMembersBeforeTestMembers()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target"), "fingerprint"),
            [
                CreateSymbol("test", "ReloadAsync", "workspace daemon reload", path: "tests/RoslynKit.Tests/WorkspaceDaemonSessionTests.cs"),
                CreateSymbol("production", "ReloadAsync", "workspace daemon reload", path: "src/RoslynKit/WorkspaceDaemonSession.cs"),
            ],
            cancellationToken);

        var results = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target"), ["workspace", "daemon", "reload"], MaxResults: 2),
            cancellationToken);

        Assert.Equal(
            ["src/RoslynKit/WorkspaceDaemonSession.cs", "tests/RoslynKit.Tests/WorkspaceDaemonSessionTests.cs"],
            results.Matches.Select(static match => match.Path.Value));
    }

    [Fact]
    public async Task SearchAsync_PrioritizesMoreRelevantTestEvidenceBeforeLessRelevantProductionMembers()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target"), "fingerprint"),
            [
                CreateSymbol(
                    "production",
                    "RefreshConfiguration",
                    "configuration refresh",
                    path: "src/Product/RefreshConfiguration.cs"),
                CreateSymbol(
                    "test",
                    "RefreshConfiguration_WhenSettingsChange",
                    "configuration snapshot refresh settings change",
                    path: "tests/Product.Tests/RefreshConfigurationTests.cs"),
            ],
            cancellationToken);

        var results = await index.SearchAsync(
            new SqliteSearchIndexQuery(
                RelativePath("target"),
                ["configuration", "snapshot", "refresh", "settings", "change"],
                MaxResults: 2),
            cancellationToken);

        Assert.Equal(
            [
                "tests/Product.Tests/RefreshConfigurationTests.cs",
                "src/Product/RefreshConfiguration.cs",
            ],
            results.Matches.Select(static match => match.Path.Value));
        Assert.Equal([5, 2], results.Matches.Select(static match => match.QueryTermCoverage));
    }

    [Fact]
    public async Task SearchAsync_DoesNotUseSubstringOnlyDocumentationForExcerpt()
    {
        await using var area = SearchIndexTestArea.Create();
        var index = new SqliteSearchIndex(area.DatabasePath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await index.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(RelativePath("target"), "fingerprint"),
            [CreateSymbol(
                "get-settings",
                "GetSettings",
                "get settings",
                documentation: "Selects the target settings source.")],
            cancellationToken);

        var result = await index.SearchAsync(
            new SqliteSearchIndexQuery(RelativePath("target"), ["get"], MaxResults: 20),
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
        string? signature = null,
        string? body = null,
        string projectPath = "App.csproj",
        string kind = "Method",
        string? pathTokens = null)
    {
        var relativeProjectPath = RelativePath(projectPath);
        var relativeSourcePath = RelativePath(path ?? "App.cs");
        var symbolKey = key.Contains('|')
            ? key
            : $"{relativeProjectPath.Value}|{relativeSourcePath.Value}|{key}|0";
        return new SqliteSearchIndexSymbol(
            symbolKey,
            relativeProjectPath,
            "App",
            kind,
            name,
            $"App.{name}",
            $"M:App.{name}",
            relativeSourcePath,
            10,
            5,
            10,
            20,
            documentation,
            signature ?? $"void {name}()",
            comments,
            body,
            name,
            "app",
            details,
            pathTokens ?? "app cs",
            details);
    }

    private static RepositoryRelativePath RelativePath(string value)
    {
        return RepositoryRelativePath.FromStoredValue(value, "test path");
    }

    private static async Task<IReadOnlySet<string>> ReadColumnNamesAsync(
        string databasePath,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static async Task<IReadOnlySet<string>> ReadTableNamesAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<PersistedSearchPathValues> ReadPersistedPathValuesAsync(
        string databasePath,
        string targetIdentity,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT targets.target_identity,
                   symbols.project_path,
                   symbols.path,
                   symbols.symbol_key,
                   fts.path_tokens
            FROM search_index_targets AS targets
            INNER JOIN search_index_symbols AS symbols
                ON symbols.target_identity = targets.target_identity
            INNER JOIN search_index_fts AS fts
                ON fts.rowid = symbols.id
            WHERE targets.target_identity = $targetIdentity;
            """;
        command.Parameters.AddWithValue("$targetIdentity", targetIdentity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        var result = new PersistedSearchPathValues(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4));
        Assert.False(await reader.ReadAsync(cancellationToken));
        return result;
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

    private sealed record PersistedSearchPathValues(
        string TargetIdentity,
        string ProjectPath,
        string Path,
        string SymbolKey,
        string PathTokens);

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
