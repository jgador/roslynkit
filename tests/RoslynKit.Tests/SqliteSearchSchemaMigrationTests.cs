using Microsoft.Data.Sqlite;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies that rebuildable search storage removes legacy semantic and completed-operation persistence atomically.
/// </summary>
public sealed class SqliteSearchSchemaMigrationTests
{
    [Fact]
    public async Task ReplaceTarget_MigratesLegacyTablesWithoutDeletingUnrelatedData()
    {
        await using var area = new MigrationArea();
        var cancellationToken = TestContext.Current.CancellationToken;
        await area.CreateLegacyAsync(cancellationToken);
        var index = new SqliteSearchIndex(area.DatabasePath);
        var target = Relative("__repository__");

        Assert.Null(await index.ReadMetadataAsync(target, cancellationToken));
        await index.ReplaceTargetAsync(new SqliteSearchIndexTarget(target, "current-inputs"), [CreateSymbol()], cancellationToken);

        Assert.Equal(SqliteSearchIndex.SchemaVersion, Convert.ToInt32(await area.ScalarAsync("PRAGMA user_version;", cancellationToken)));
        Assert.Equal(0L, await area.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'semantic_catalog_%';", cancellationToken));
        Assert.Equal("preserved", await area.ScalarAsync("SELECT value FROM unrelated_data;", cancellationToken));
        var result = await index.SearchAsync(new SqliteSearchIndexQuery(target, ["workspace"]), cancellationToken);
        Assert.Equal("Workspace", Assert.Single(result.Matches).Name);
        Assert.Equal("current-inputs", (await index.ReadMetadataAsync(target, cancellationToken))!.Fingerprint);
    }

    [Fact]
    public async Task WriterLease_RollsBackMigrationAndReplacementTogetherWhenNotCommitted()
    {
        await using var area = new MigrationArea();
        var cancellationToken = TestContext.Current.CancellationToken;
        await area.CreateLegacyAsync(cancellationToken);
        var index = new SqliteSearchIndex(area.DatabasePath);
        var target = Relative("__repository__");

        await using (var writer = await index.AcquireWriterLeaseAsync(TimeSpan.FromSeconds(1), cancellationToken))
        {
            await writer.ReplaceTargetAsync(new SqliteSearchIndexTarget(target, "unpublished"), [CreateSymbol()], cancellationToken);
        }

        Assert.Equal(0L, await area.ScalarAsync("PRAGMA user_version;", cancellationToken));
        Assert.Equal("old-answer", await area.ScalarAsync("SELECT payload_json FROM semantic_catalog_operation_cache;", cancellationToken));
        await index.ReplaceTargetAsync(new SqliteSearchIndexTarget(target, "recovered"), [CreateSymbol()], cancellationToken);
        Assert.Equal("recovered", (await index.ReadMetadataAsync(target, cancellationToken))!.Fingerprint);
        Assert.Equal(0L, await area.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'semantic_catalog_%';", cancellationToken));
    }

    [Fact]
    public async Task WriterLease_RejectsNewerSchemaWithoutChangingItsData()
    {
        await using var area = new MigrationArea();
        var cancellationToken = TestContext.Current.CancellationToken;
        await area.CreateLegacyAsync(cancellationToken);
        await area.ScalarAsync($"PRAGMA user_version = {SqliteSearchIndex.SchemaVersion + 1};", cancellationToken);
        var index = new SqliteSearchIndex(area.DatabasePath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => index.AcquireWriterLeaseAsync(TimeSpan.FromSeconds(1), cancellationToken));

        Assert.Contains("newer than supported", exception.Message, StringComparison.Ordinal);
        Assert.Equal("old-answer", await area.ScalarAsync("SELECT payload_json FROM semantic_catalog_operation_cache;", cancellationToken));
    }

    private static RepositoryRelativePath Relative(string value) => RepositoryRelativePath.FromStoredValue(value, "test path");

    private static SqliteSearchIndexSymbol CreateSymbol() => new(
        "App.csproj|Source.cs|T:Fixture.Workspace|0", Relative("App.csproj"), "App", "class", "Workspace",
        "Fixture.Workspace", "T:Fixture.Workspace", Relative("Source.cs"), 1, 1, 1, 20,
        "Workspace search fixture.", "class Workspace", null, "class Workspace { }",
        "workspace", "fixture", "workspace search fixture", "source cs", "workspace");

    /// <summary>
    /// Owns an isolated, disposable database with the previous unversioned storage shape.
    /// </summary>
    private sealed class MigrationArea : IAsyncDisposable
    {
        private readonly string _directory = TestPaths.RepoFile("artifacts", "search-schema-migrations", Guid.NewGuid().ToString("N"));

        public MigrationArea()
        {
            Directory.CreateDirectory(_directory);
        }

        public string DatabasePath => Path.Combine(_directory, "roslynkit.db");

        public Task<object?> CreateLegacyAsync(CancellationToken cancellationToken) => ScalarAsync("""
            CREATE TABLE semantic_catalog_projects (project_name TEXT);
            CREATE TABLE semantic_catalog_symbols (symbol_id TEXT);
            CREATE TABLE semantic_catalog_relations (target_symbol_id TEXT);
            CREATE TABLE semantic_catalog_operation_cache (payload_json TEXT);
            INSERT INTO semantic_catalog_operation_cache VALUES ('old-answer');
            CREATE TABLE unrelated_data (value TEXT);
            INSERT INTO unrelated_data VALUES ('preserved');
            """, cancellationToken);

        public async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
