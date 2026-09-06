using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Data.Sqlite;

namespace RoslynKit.Tests;

/// <summary>
/// Exercises independent snapshot publishers and search shutdown without loading MSBuild.
/// </summary>
[Collection("Search command integration")]
public sealed class LiveSearchIndexTests
{
    [Fact]
    public async Task Index_RebuildReportsSuccessorFailureEvenWhenItFinishesBeforeWaitersObserveIt()
    {
        await using var area = await LiveSearchArea.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var session = area.CreateSession();
        var finishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePump = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseObservers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause = false;
        await using var liveIndex = new LiveSearchIndex(
            session, TestPaths.RepositoryRoot(), area.ProjectPath,
            beforeRefreshCompletion: async token =>
            {
                if (pause)
                {
                    finishing.TrySetResult();
                    await releasePump.Task.WaitAsync(token);
                }
            },
            beforeObservingRefreshSuccessor: async token =>
            {
                if (pause)
                {
                    await releaseObservers.Task.WaitAsync(token);
                }
            });
        await liveIndex.SearchAsync(area.SearchCommand(), cancellationToken);
        var index = new SqliteSearchIndex(area.DatabasePath);
        var target = RepositoryRelativePath.FromPhysicalPath(TestPaths.RepositoryRoot(), area.ProjectPath, "test target");
        var initial = await index.ReadMetadataAsync(target, cancellationToken);
        pause = true;
        var warmSearch = liveIndex.SearchAsync(area.SearchCommand(), cancellationToken);
        await finishing.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = area.DatabasePath,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER fail_forced_rebuild BEFORE INSERT ON search_index_symbols
                BEGIN SELECT RAISE(ABORT, 'forced rebuild failure'); END;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var forced = liveIndex.IndexAsync(CliParser.Parse([
            "index", "--target", area.ProjectPath, "--index-path", area.DatabasePath, "--rebuild",
        ]), cancellationToken);
        releasePump.TrySetResult();
        using var completionDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        completionDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (liveIndex.IsBusy)
        {
            await Task.Delay(10, completionDeadline.Token);
        }

        Assert.False(forced.IsCompleted);
        releaseObservers.TrySetResult();
        var exception = await Assert.ThrowsAsync<SqliteException>(() => forced);
        Assert.Contains("forced rebuild failure", exception.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<SqliteException>(() => warmSearch);
        Assert.Equal(initial, await index.ReadMetadataAsync(target, cancellationToken));
    }

    [Fact]
    public async Task Search_RebuildsAnIndexWithoutTheCurrentCorpusBuildIdentity()
    {
        await using var area = await LiveSearchArea.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var session = area.CreateSession();
        await using var liveIndex = area.CreateIndex(session);
        await liveIndex.SearchAsync(area.SearchCommand(), cancellationToken);
        var index = new SqliteSearchIndex(area.DatabasePath);
        var target = RepositoryRelativePath.FromPhysicalPath(TestPaths.RepositoryRoot(), area.ProjectPath, "test target");
        var initial = await index.ReadMetadataAsync(target, cancellationToken);
        Assert.Contains(typeof(RoslynSearchCorpusBuilder).Module.ModuleVersionId.ToString("N"), initial!.Fingerprint!, StringComparison.Ordinal);
        using var snapshot = await session.AcquireAsync(cancellationToken);
        var oldFingerprint = $"search-v{SqliteSearchIndex.SchemaVersion}|{target.Value}|{snapshot.Fingerprint}";
        await index.ReplaceTargetAsync(new SqliteSearchIndexTarget(target, oldFingerprint), [], cancellationToken);

        var refreshed = await liveIndex.SearchAsync(area.SearchCommand(), cancellationToken);

        Assert.Contains(refreshed.Hits, hit => hit.DisplayName == "Fixture.OriginalService");
        Assert.Equal(initial.Fingerprint, (await index.ReadMetadataAsync(target, cancellationToken))!.Fingerprint);
    }

    [Fact]
    public async Task Index_RebuildWaitsForSuccessorRefreshWhenRequestedDuringPumpCompletion()
    {
        await using var area = await LiveSearchArea.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var session = area.CreateSession();
        var finishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseCompletion = false;
        await using var liveIndex = new LiveSearchIndex(session, TestPaths.RepositoryRoot(), area.ProjectPath, async token =>
        {
            if (pauseCompletion)
            {
                finishing.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        });
        await liveIndex.SearchAsync(area.SearchCommand(), cancellationToken);
        var index = new SqliteSearchIndex(area.DatabasePath);
        var target = RepositoryRelativePath.FromPhysicalPath(TestPaths.RepositoryRoot(), area.ProjectPath, "test target");
        var initial = await index.ReadMetadataAsync(target, cancellationToken);
        await using var writer = await index.AcquireWriterLeaseAsync(TimeSpan.FromSeconds(1), cancellationToken);
        pauseCompletion = true;

        var warmSearch = liveIndex.SearchAsync(area.SearchCommand(), cancellationToken);
        await finishing.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var forced = liveIndex.IndexAsync(CliParser.Parse([
            "index", "--target", area.ProjectPath, "--index-path", area.DatabasePath, "--rebuild",
        ]), cancellationToken);
        release.TrySetResult();
        await Task.Delay(100, cancellationToken);
        Assert.False(forced.IsCompleted);
        await writer.DisposeAsync();

        var result = await forced.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await warmSearch;
        var rebuilt = await index.ReadMetadataAsync(target, cancellationToken);
        Assert.True(result.Rebuilt);
        Assert.True(rebuilt!.IndexedAtUtc > initial!.IndexedAtUtc);
    }

    [Fact]
    public async Task Search_RejectsAnOldSessionSnapshotAfterAnotherSessionPublishesNewInputs()
    {
        await using var area = await LiveSearchArea.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var olderSession = area.CreateSession();
        await using var olderIndex = area.CreateIndex(olderSession);
        var original = await olderIndex.SearchAsync(area.SearchCommand(), cancellationToken);
        Assert.Contains(original.Hits, hit => hit.DisplayName == "Fixture.OriginalService");
        using var retainedOriginal = await olderSession.AcquireAsync(cancellationToken);

        await area.WriteSourceAsync("UpdatedService", cancellationToken);
        await using var newerSession = area.CreateSession();
        await using var newerIndex = area.CreateIndex(newerSession);
        var published = await newerIndex.SearchAsync(area.SearchCommand(), cancellationToken);
        Assert.Contains(published.Hits, hit => hit.DisplayName == "Fixture.UpdatedService");

        var recovered = await olderIndex.SearchAsync(area.SearchCommand(), cancellationToken);

        Assert.Contains(recovered.Hits, hit => hit.DisplayName == "Fixture.UpdatedService");
        Assert.DoesNotContain(recovered.Hits, hit => hit.DisplayName == "Fixture.OriginalService");
        using var currentOlder = await olderSession.AcquireAsync(cancellationToken);
        using var currentNewer = await newerSession.AcquireAsync(cancellationToken);
        Assert.Equal(currentNewer.Fingerprint, currentOlder.Fingerprint);
        Assert.NotEqual(retainedOriginal.Fingerprint, currentOlder.Fingerprint);
        Assert.Contains("OriginalService", (await retainedOriginal.Snapshot.Solution.Projects.Single()
            .Documents.Single().GetTextAsync(cancellationToken)).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeAsync_CancelsBlockedIndexWorkWithoutBlockingSemanticSnapshotsOrFutureSearch()
    {
        await using var area = await LiveSearchArea.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var session = area.CreateSession();
        await using var liveIndex = area.CreateIndex(session);
        await liveIndex.SearchAsync(area.SearchCommand(), cancellationToken);
        await using var writer = await new SqliteSearchIndex(area.DatabasePath)
            .AcquireWriterLeaseAsync(TimeSpan.FromSeconds(1), cancellationToken);
        await area.WriteSourceAsync("UpdatedService", cancellationToken);
        session.NotifyFileChanged(area.SourcePath);

        using var semanticSnapshot = await session.AcquireAsync(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        Assert.Contains("UpdatedService", (await semanticSnapshot.Snapshot.Solution.Projects.Single()
            .Documents.Single().GetTextAsync(cancellationToken)).ToString(), StringComparison.Ordinal);
        var pendingSearch = liveIndex.SearchAsync(area.SearchCommand(), cancellationToken);
        await Task.Delay(100, cancellationToken);
        Assert.False(pendingSearch.IsCompleted);

        await liveIndex.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingSearch);
        await writer.DisposeAsync();
        await using var replacementIndex = area.CreateIndex(session);
        var recovered = await replacementIndex.SearchAsync(area.SearchCommand(), cancellationToken);

        Assert.Equal(SearchIndexState.Fresh, recovered.IndexState);
        Assert.Contains(recovered.Hits, hit => hit.DisplayName == "Fixture.UpdatedService");
    }

    /// <summary>
    /// Supplies one source file and an injected immutable workspace under an ignored test directory.
    /// </summary>
    private sealed class LiveSearchArea : IAsyncDisposable
    {
        private readonly string _directory = TestPaths.RepoFile("artifacts", "live-search-tests", Guid.NewGuid().ToString("N"));

        public string ProjectPath => Path.Combine(_directory, "Fixture.csproj");
        public string SourcePath => Path.Combine(_directory, "Source.cs");
        public string DatabasePath => Path.Combine(_directory, "roslynkit.db");

        public static async Task<LiveSearchArea> CreateAsync()
        {
            var area = new LiveSearchArea();
            Directory.CreateDirectory(area._directory);
            await File.WriteAllTextAsync(area.ProjectPath, "<Project />", TestContext.Current.CancellationToken);
            await area.WriteSourceAsync("OriginalService", TestContext.Current.CancellationToken);
            return area;
        }

        public Task WriteSourceAsync(string name, CancellationToken cancellationToken) => File.WriteAllTextAsync(
            SourcePath, $"namespace Fixture; public class {name} {{ }}", cancellationToken);

        public RepositoryWorkspaceSession CreateSession() => new(
            TestPaths.RepositoryRoot(), ProjectPath, LoadAsync,
            reconciliationInterval: Timeout.InfiniteTimeSpan, changeDebounce: TimeSpan.Zero, watchFiles: false);

        public LiveSearchIndex CreateIndex(RepositoryWorkspaceSession session) => new(
            session, TestPaths.RepositoryRoot(), ProjectPath);

        public ParsedCommand SearchCommand() => CliParser.Parse([
            "search", "--target", ProjectPath, "--index-path", DatabasePath, "--query", "service",
        ]);

        private async Task<RoslynWorkspaceLoader> LoadAsync(CancellationToken cancellationToken)
        {
            var workspace = new AdhocWorkspace();
            try
            {
                var projectId = ProjectId.CreateNewId();
                var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
                    projectId, VersionStamp.Create(), "Fixture", "Fixture", LanguageNames.CSharp,
                    filePath: ProjectPath, compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                    metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
                solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "Source.cs",
                    SourceText.From(await File.ReadAllTextAsync(SourcePath, cancellationToken)), filePath: SourcePath);
                return new RoslynWorkspaceLoader(workspace, solution, ProjectPath, "csproj", TestPaths.RepositoryRoot(),
                    new Dictionary<ProjectId, string?> { [projectId] = "net10.0" }, []);
            }
            catch
            {
                workspace.Dispose();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
