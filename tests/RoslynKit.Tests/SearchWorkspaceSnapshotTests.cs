namespace RoslynKit.Tests;

/// <summary>
/// Covers the stable repository snapshot boundary used before search commands read or publish index records.
/// </summary>
[Collection("Search command integration")]
public sealed class SearchWorkspaceSnapshotTests
{
    [Fact]
    public async Task LoadStableWorkspaceAsync_BindsTheLoadedWorkspaceToItsCapturedFingerprint()
    {
        await using var area = SearchWorkspaceSnapshotTestArea.Create();

        using var loaded = await SearchCommandService.LoadStableWorkspaceAsync(
            CreateIndexCommand(area),
            TestContext.Current.CancellationToken);

        var captured = loaded.LoadedWorktreeFingerprint;

        Assert.NotNull(captured);
        Assert.False(string.IsNullOrWhiteSpace(captured.HeadCommit));
    }

    [Fact]
    public async Task IndexAsync_RejectsAnUnboundWorkspaceBeforePublishingTargetMetadata()
    {
        await using var area = SearchWorkspaceSnapshotTestArea.Create();
        var command = CreateIndexCommand(area);
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(
            TestPaths.FixtureProjectPath(),
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SearchCommandService.IndexAsync(command, loaded, TestContext.Current.CancellationToken));

        Assert.Contains("not associated with a stable repository fingerprint", exception.Message, StringComparison.Ordinal);

        const string targetIdentity = "tests/FixtureWorkspace/App/App.csproj";
        var metadata = await new SqliteSearchIndex(area.DatabasePath).ReadMetadataAsync(
            RepositoryRelativePath.FromStoredValue(targetIdentity, "search target"),
            TestContext.Current.CancellationToken);

        Assert.Null(metadata);
    }

    private static ParsedCommand CreateIndexCommand(SearchWorkspaceSnapshotTestArea area)
    {
        return CliParser.Parse(
        [
            "index",
            "--target", TestPaths.FixtureProjectPath(),
            "--index-path", area.DatabasePath,
        ]);
    }

    private sealed class SearchWorkspaceSnapshotTestArea : IAsyncDisposable
    {
        private SearchWorkspaceSnapshotTestArea(string directoryPath)
        {
            DirectoryPath = directoryPath;
        }

        public string DirectoryPath { get; }

        public string DatabasePath => Path.Combine(DirectoryPath, "roslynkit.db");

        public static SearchWorkspaceSnapshotTestArea Create()
        {
            var directoryPath = TestPaths.RepoFile(
                "artifacts",
                "search-workspace-snapshot-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            return new SearchWorkspaceSnapshotTestArea(directoryPath);
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
