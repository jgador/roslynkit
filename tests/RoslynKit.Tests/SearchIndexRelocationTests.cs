using Microsoft.Data.Sqlite;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies that repository-local search indexes remain portable across repository and database relocations.
/// </summary>
[Collection("Search command integration")]
public sealed class SearchIndexRelocationTests
{
    private const string RelocationMethodDisplayName = "PortableSearch.RelocatedIndex.LocatePortableDatabase";

    [Fact]
    public async Task Index_RemainsUsableAfterMovingTheEntireRepositoryToADifferentAbsolutePath()
    {
        await using var repository = await SearchIndexRelocationTestArea.CreateAsync();
        var originalRoot = repository.RootPath;
        var originalDatabasePath = repository.GetPath("artifacts/original/roslynkit.db");

        var initial = await ExecuteIndexAsync(repository, originalDatabasePath);

        Assert.True(initial.SymbolCount > 0);
        Assert.True(File.Exists(originalDatabasePath));

        repository.MoveToRelocatedRoot();
        var relocatedDatabasePath = repository.GetPath("artifacts/original/roslynkit.db");

        Assert.False(Directory.Exists(originalRoot));
        Assert.True(File.Exists(relocatedDatabasePath));

        var metadataBeforeSearch = await ReadPersistedMetadataAsync(relocatedDatabasePath);
        var search = await ExecuteSearchAsync(repository, relocatedDatabasePath);

        Assert.Equal(metadataBeforeSearch, await ReadPersistedMetadataAsync(relocatedDatabasePath));
        AssertSearchUsesRepositoryPaths(search, repository, relocatedDatabasePath);
    }

    [Fact]
    public async Task Index_RemainsUsableWhenDatabaseIsCopiedAndMovedBetweenIgnoredDirectories()
    {
        await using var repository = await SearchIndexRelocationTestArea.CreateAsync();
        var originalDatabasePath = repository.GetPath("artifacts/original/roslynkit.db");
        var copiedDatabasePath = repository.GetPath("cache/copied/roslynkit.db");
        var movedDatabasePath = repository.GetPath("artifacts/moved/roslynkit.db");

        var initial = await ExecuteIndexAsync(repository, originalDatabasePath);
        CopySqliteDatabaseArtifacts(originalDatabasePath, copiedDatabasePath);

        var copiedMetadataBeforeSearch = await ReadPersistedMetadataAsync(copiedDatabasePath);
        var copiedSearch = await ExecuteSearchAsync(repository, copiedDatabasePath);

        Assert.True(initial.SymbolCount > 0);
        Assert.Equal(copiedMetadataBeforeSearch, await ReadPersistedMetadataAsync(copiedDatabasePath));
        AssertSearchUsesRepositoryPaths(copiedSearch, repository, copiedDatabasePath);

        MoveSqliteDatabaseArtifacts(copiedDatabasePath, movedDatabasePath);

        Assert.False(File.Exists(copiedDatabasePath));
        Assert.True(File.Exists(movedDatabasePath));

        var movedMetadataBeforeSearch = await ReadPersistedMetadataAsync(movedDatabasePath);
        var movedSearch = await ExecuteSearchAsync(repository, movedDatabasePath);

        Assert.Equal(movedMetadataBeforeSearch, await ReadPersistedMetadataAsync(movedDatabasePath));
        AssertSearchUsesRepositoryPaths(movedSearch, repository, movedDatabasePath);
    }

    private static Task<IndexResult> ExecuteIndexAsync(
        SearchIndexRelocationTestArea repository,
        string databasePath)
    {
        return TestPaths.ExecuteCommandAsync<IndexResult>(
            "index",
            "--target", repository.ProjectPath,
            "--index-path", databasePath);
    }

    private static Task<SearchResult> ExecuteSearchAsync(
        SearchIndexRelocationTestArea repository,
        string databasePath)
    {
        return TestPaths.ExecuteCommandAsync<SearchResult>(
            "search",
            "--target", repository.ProjectPath,
            "--index-path", databasePath,
            "--query", "portable index database repository relocation",
            "--max-results", "20");
    }

    private static void AssertSearchUsesRepositoryPaths(
        SearchResult search,
        SearchIndexRelocationTestArea repository,
        string databasePath)
    {
        Assert.Equal(SearchIndexState.Fresh, search.IndexState);
        Assert.Equal(repository.ProjectPath, search.TargetPath, PathComparer);
        Assert.Equal(databasePath, search.IndexPath, PathComparer);

        var hit = Assert.Single(search.Hits, hit => hit.DisplayName == RelocationMethodDisplayName);
        var locationPath = Assert.IsType<string>(hit.Location.Path);
        Assert.True(Path.IsPathFullyQualified(locationPath));
        Assert.Equal(repository.SourcePath, locationPath, PathComparer);
    }

    private static void CopySqliteDatabaseArtifacts(string sourceDatabasePath, string destinationDatabasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationDatabasePath)!);
        foreach (var suffix in SqliteDatabaseArtifactSuffixes)
        {
            var sourcePath = sourceDatabasePath + suffix;
            if (File.Exists(sourcePath))
            {
                File.Copy(sourcePath, destinationDatabasePath + suffix);
            }
        }
    }

    private static void MoveSqliteDatabaseArtifacts(string sourceDatabasePath, string destinationDatabasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationDatabasePath)!);
        foreach (var suffix in SqliteDatabaseArtifactSuffixes)
        {
            var sourcePath = sourceDatabasePath + suffix;
            if (File.Exists(sourcePath))
            {
                File.Move(sourcePath, destinationDatabasePath + suffix);
            }
        }
    }

    private static async Task<PersistedIndexMetadata> ReadPersistedMetadataAsync(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT fingerprint, indexed_at_utc, symbol_count FROM search_index_targets;";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        var metadata = new PersistedIndexMetadata(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2));
        Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return metadata;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static IReadOnlyList<string> SqliteDatabaseArtifactSuffixes { get; } = ["", "-wal", "-shm"];

    private sealed record PersistedIndexMetadata(
        string Fingerprint,
        string IndexedAtUtc,
        int SymbolCount);

    private sealed class SearchIndexRelocationTestArea : IAsyncDisposable
    {
        private SearchIndexRelocationTestArea(string testDirectory, string rootPath)
        {
            TestDirectory = testDirectory;
            RootPath = rootPath;
        }

        public string TestDirectory { get; }

        public string RootPath { get; private set; }

        public string ProjectPath => GetPath("src/PortableSearch.csproj");

        public string SourcePath => GetPath("src/RelocatedIndex.cs");

        public static async Task<SearchIndexRelocationTestArea> CreateAsync()
        {
            var testDirectory = Path.Combine(
                Path.GetTempPath(),
                "roslynkit-tests",
                "search-index-relocation",
                Guid.NewGuid().ToString("N"));
            var rootPath = Path.Combine(testDirectory, "original-repository");
            Directory.CreateDirectory(rootPath);

            var repository = new SearchIndexRelocationTestArea(testDirectory, rootPath);
            await repository.RunGitAsync("init");
            await repository.RunGitAsync("config", "user.name", "RoslynKit Tests");
            await repository.RunGitAsync("config", "user.email", "roslynkit@example.test");
            await repository.WriteAsync(".gitignore", "artifacts/\ncache/\nbin/\nobj/\n");
            await repository.WriteAsync(
                "src/PortableSearch.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await repository.WriteAsync(
                "src/RelocatedIndex.cs",
                """
                namespace PortableSearch;

                public static class RelocatedIndex
                {
                    /// <summary>
                    /// Locates the portable index database after repository relocation.
                    /// </summary>
                    public static string LocatePortableDatabase()
                    {
                        return "portable";
                    }
                }
                """);
            await repository.RunGitAsync("add", ".");
            await repository.RunGitAsync("commit", "-m", "Initial commit");
            return repository;
        }

        public string GetPath(string relativePath)
        {
            return Path.GetFullPath(relativePath, RootPath);
        }

        public void MoveToRelocatedRoot()
        {
            var relocatedRoot = Path.Combine(TestDirectory, "relocated-repository");
            Directory.Move(RootPath, relocatedRoot);
            RootPath = relocatedRoot;
        }

        public async Task WriteAsync(string relativePath, string content)
        {
            var path = GetPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        }

        public async Task RunGitAsync(params string[] arguments)
        {
            var result = await ProcessCommandRunner.RunAsync(
                "git",
                RootPath,
                arguments,
                TestContext.Current.CancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', arguments)} failed with exit code {result.ExitCode}: {result.StandardError}");
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(TestDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(TestDirectory, "*", SearchOption.AllDirectories))
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                }

                Directory.Delete(TestDirectory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
