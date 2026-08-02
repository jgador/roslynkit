using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RoslynKit;

/// <summary>
/// Persists and queries target-partitioned Roslyn symbol search records through SQLite full-text search.
/// </summary>
internal sealed class SqliteSearchIndex
{
    private const int CurrentSchemaVersion = 1;
    private const int BusyTimeoutMilliseconds = 5_000;
    private const int NameWeight = 12;
    private const int ContainingWeight = 6;
    private const int DetailsWeight = 3;
    private const int PathWeight = 1;
    private const int BodyWeight = 1;
    private static readonly string[] TargetColumns =
    [
        "target_identity",
        "fingerprint",
        "indexed_at_utc",
        "symbol_count",
        "schema_version",
    ];
    private static readonly string[] SymbolColumns =
    [
        "id",
        "target_identity",
        "symbol_key",
        "project_path",
        "project_name",
        "kind",
        "name",
        "display_name",
        "symbol_id",
        "path",
        "line",
        "column_number",
        "end_line",
        "end_column_number",
        "documentation",
        "signature",
        "comments",
        "body",
    ];
    private static readonly string[] FtsColumns =
    [
        "name_tokens",
        "containing_tokens",
        "details_tokens",
        "path_tokens",
        "body_tokens",
    ];
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly string _readConnectionString;

    public SqliteSearchIndex(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        _readConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
    }

    /// <summary>
    /// Replaces all indexed symbols for a target atomically while leaving other target partitions unchanged.
    /// </summary>
    public async Task ReplaceTargetAsync(
        SqliteSearchIndexTarget target,
        IReadOnlyCollection<SqliteSearchIndexSymbol> symbols,
        CancellationToken cancellationToken)
    {
        await using var lease = await AcquireWriterLeaseAsync(TimeSpan.FromMilliseconds(BusyTimeoutMilliseconds), cancellationToken);
        await lease.ReplaceTargetAsync(target, symbols, cancellationToken);
        await lease.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces selected project partitions for a target atomically and refreshes target metadata.
    /// </summary>
    public async Task ReplaceProjectsAsync(
        SqliteSearchIndexTarget target,
        IReadOnlyCollection<RepositoryRelativePath> projectPaths,
        IReadOnlyCollection<SqliteSearchIndexSymbol> symbols,
        CancellationToken cancellationToken)
    {
        await using var lease = await AcquireWriterLeaseAsync(TimeSpan.FromMilliseconds(BusyTimeoutMilliseconds), cancellationToken);
        await lease.ReplaceProjectsAsync(target, projectPaths, symbols, cancellationToken);
        await lease.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Acquires the database-wide immediate write transaction used to keep one index refresh coherent.
    /// </summary>
    public async Task<SqliteSearchIndexWriterLease> AcquireWriterLeaseAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ValidateLeaseTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        SqliteConnection? connection = null;
        SqliteTransaction? transaction = null;
        try
        {
            connection = await OpenWriteConnectionAsync(timeout, cancellationToken);
            var schemaExists = await HasSchemaAsync(connection, cancellationToken);
            transaction = connection.BeginTransaction(deferred: false);
            if (schemaExists)
            {
                await ValidateSchemaAsync(connection, transaction, cancellationToken);
            }
            else
            {
                await EnsureSchemaAsync(connection, transaction, cancellationToken);
            }

            return new SqliteSearchIndexWriterLease(connection, transaction);
        }
        catch (SqliteException exception) when (IsWriterContention(exception))
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            throw new SqliteSearchIndexWriterLeaseUnavailableException(timeout, exception);
        }
        catch (SqliteException exception) when (IsInvalidDatabase(exception))
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            throw CreateInvalidDatabaseException(exception);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            throw;
        }
    }

    /// <summary>
    /// Reads metadata for one target without creating a database when no index exists yet.
    /// </summary>
    public async Task<SqliteSearchIndexMetadata?> ReadMetadataAsync(
        RepositoryRelativePath targetIdentity,
        CancellationToken cancellationToken)
    {
        ValidateTargetIdentity(targetIdentity);
        if (!File.Exists(_databasePath))
        {
            return null;
        }

        var readConnection = await TryOpenReadConnectionAsync(cancellationToken);
        if (readConnection is null)
        {
            return null;
        }

        await using var connection = readConnection;
        return await ReadMetadataAsync(connection, null, targetIdentity, cancellationToken);
    }

    internal static async Task<SqliteSearchIndexMetadata?> ReadMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RepositoryRelativePath targetIdentity,
        CancellationToken cancellationToken)
    {
        ValidateTargetIdentity(targetIdentity);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT schema_version,
                   target_identity,
                   fingerprint,
                   indexed_at_utc,
                   symbol_count
            FROM search_index_targets
            WHERE target_identity = $targetIdentity;
            """;
        command.Parameters.AddWithValue("$targetIdentity", targetIdentity.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SqliteSearchIndexMetadata(
            reader.GetInt32(0),
            RepositoryRelativePath.FromStoredValue(reader.GetString(1), "Persisted target identity"),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetInt32(4));
    }

    /// <summary>
    /// Searches one target partition with pre-tokenized natural-language terms.
    /// </summary>
    public async Task<SqliteSearchIndexSearchResult> SearchAsync(
        SqliteSearchIndexQuery query,
        CancellationToken cancellationToken)
    {
        return (await ReadSearchSnapshotAsync(query, cancellationToken)).SearchResult;
    }

    /// <summary>
    /// Captures target metadata and ranked results from one SQLite read transaction.
    /// </summary>
    public async Task<SqliteSearchIndexSearchSnapshot> ReadSearchSnapshotAsync(
        SqliteSearchIndexQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);
        if (query.MaxResults <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "The maximum result count must be positive.");
        }

        if (!File.Exists(_databasePath))
        {
            return new SqliteSearchIndexSearchSnapshot(null, new SqliteSearchIndexSearchResult(0, []));
        }

        var readConnection = await TryOpenReadConnectionAsync(cancellationToken);
        if (readConnection is null)
        {
            return new SqliteSearchIndexSearchSnapshot(null, new SqliteSearchIndexSearchResult(0, []));
        }

        await using var connection = readConnection;
        await using var transaction = connection.BeginTransaction(deferred: true);
        var metadata = await ReadMetadataAsync(connection, transaction, query.TargetIdentity, cancellationToken);
        if (metadata is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new SqliteSearchIndexSearchSnapshot(null, new SqliteSearchIndexSearchResult(0, []));
        }

        var queryExpression = BuildMatchExpression(query.Tokens);
        if (queryExpression is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new SqliteSearchIndexSearchSnapshot(metadata, new SqliteSearchIndexSearchResult(0, []));
        }

        var totalMatchCount = await CountMatchesAsync(connection, transaction, query, queryExpression, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BuildSearchCommandText(query, command);
        command.Parameters.AddWithValue("$match", queryExpression);
        command.Parameters.AddWithValue("$targetIdentity", query.TargetIdentity.Value);
        command.Parameters.AddWithValue("$maxResults", query.MaxResults);

        var results = new List<SqliteSearchIndexMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var symbolKey = reader.GetString(0);
            var projectPath = RepositoryRelativePath.FromStoredValue(reader.GetString(1), "Persisted project path");
            var sourcePath = RepositoryRelativePath.FromStoredValue(reader.GetString(7), "Persisted source path");
            ValidateSymbolKey(symbolKey, query.TargetIdentity, projectPath, sourcePath);
            var documentation = reader.IsDBNull(12) ? null : reader.GetString(12);
            var signature = reader.IsDBNull(13) ? null : reader.GetString(13);
            var comments = reader.IsDBNull(14) ? null : reader.GetString(14);
            var body = reader.IsDBNull(15) ? null : reader.GetString(15);
            results.Add(new SqliteSearchIndexMatch(
                symbolKey,
                projectPath,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                sourcePath,
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                documentation,
                signature,
                SelectExcerpt(
                    documentation,
                    comments,
                    signature,
                    body,
                    query.Tokens),
                reader.GetInt32(17),
                reader.GetDouble(16)));
        }

        await transaction.CommitAsync(cancellationToken);
        return new SqliteSearchIndexSearchSnapshot(
            metadata,
            new SqliteSearchIndexSearchResult(totalMatchCount, results));
    }

    /// <summary>
    /// Reads the database journal mode for storage verification and focused tests.
    /// </summary>
    public async Task<string> ReadJournalModeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture)
            ?? string.Empty;
    }

    private async Task<SqliteConnection> OpenWriteConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            connection.DefaultTimeout = GetCommandTimeoutSeconds(timeout);
            await ConfigureWriteConnectionAsync(connection, timeout, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<SqliteConnection> OpenReadConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_readConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ConfigureReadConnectionAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<SqliteConnection?> TryOpenReadConnectionAsync(CancellationToken cancellationToken)
    {
        SqliteConnection? connection = null;
        try
        {
            connection = await OpenReadConnectionAsync(cancellationToken);
            await ValidateSchemaAsync(connection, null, cancellationToken);
            return connection;
        }
        catch (SqliteException exception) when (IsMissingSchema(exception))
        {
            if (connection is not null)
            {
                var isEmpty = await IsDatabaseEmptyAsync(connection, cancellationToken);
                await connection.DisposeAsync();
                if (isEmpty)
                {
                    return null;
                }
            }

            throw CreateIncompleteSchemaException("search_index_schema", exception);
        }
        catch (SqliteException exception) when (IsInvalidDatabase(exception))
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            if (IsEmptyDatabaseFile())
            {
                return null;
            }

            throw CreateInvalidDatabaseException(exception);
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            throw;
        }
    }

    private static async Task<bool> HasSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await ValidateSchemaAsync(connection, null, cancellationToken);
            return true;
        }
        catch (SqliteException exception) when (IsMissingSchema(exception))
        {
            if (await IsDatabaseEmptyAsync(connection, cancellationToken))
            {
                return false;
            }

            throw CreateIncompleteSchemaException("search_index_schema", exception);
        }
    }

    private static async Task ConfigureWriteConnectionAsync(
        SqliteConnection connection,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, null, $"PRAGMA busy_timeout = {GetBusyTimeoutMilliseconds(timeout)};", cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "PRAGMA foreign_keys = ON;", cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "PRAGMA journal_mode = WAL;", cancellationToken);
    }

    private static async Task ConfigureReadConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, null, "PRAGMA busy_timeout = 5000;", cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "PRAGMA foreign_keys = ON;", cancellationToken);
    }

    private static async Task EnsureSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS search_index_schema (
                schema_key INTEGER PRIMARY KEY CHECK (schema_key = 1),
                schema_version INTEGER NOT NULL
            );

            INSERT OR IGNORE INTO search_index_schema (schema_key, schema_version)
            VALUES (1, {CurrentSchemaVersion});

            CREATE TABLE IF NOT EXISTS search_index_targets (
                target_identity TEXT PRIMARY KEY,
                fingerprint TEXT NULL,
                indexed_at_utc TEXT NOT NULL,
                symbol_count INTEGER NOT NULL CHECK (symbol_count >= 0),
                schema_version INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS search_index_symbols (
                id INTEGER PRIMARY KEY,
                target_identity TEXT NOT NULL,
                symbol_key TEXT NOT NULL,
                project_path TEXT NOT NULL,
                project_name TEXT NOT NULL,
                kind TEXT NOT NULL,
                name TEXT NOT NULL,
                display_name TEXT NOT NULL,
                symbol_id TEXT NULL,
                path TEXT NOT NULL,
                line INTEGER NOT NULL CHECK (line >= 1),
                column_number INTEGER NOT NULL CHECK (column_number >= 1),
                end_line INTEGER NOT NULL CHECK (end_line >= 1),
                end_column_number INTEGER NOT NULL CHECK (end_column_number >= 1),
                documentation TEXT NULL,
                signature TEXT NULL,
                comments TEXT NULL,
                body TEXT NULL,
                UNIQUE (target_identity, symbol_key)
            );

            CREATE INDEX IF NOT EXISTS ix_search_index_symbols_target_project_kind
                ON search_index_symbols (target_identity, project_path, kind);

            CREATE VIRTUAL TABLE IF NOT EXISTS search_index_fts USING fts5(
                name_tokens,
                containing_tokens,
                details_tokens,
                path_tokens,
                body_tokens,
                tokenize = 'unicode61 remove_diacritics 2'
            );
            """, cancellationToken);

        await ValidateSchemaAsync(connection, transaction, cancellationToken);
    }

    private static async Task ValidateSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT schema_version FROM search_index_schema WHERE schema_key = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            throw new InvalidOperationException("The SQLite search index schema metadata is missing.");
        }

        var schemaVersion = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"The SQLite search index uses schema version {schemaVersion}, but RoslynKit requires version {CurrentSchemaVersion}. Delete the index database and run index again.");
        }

        await ValidateSchemaObjectAsync(connection, transaction, "search_index_targets", false, TargetColumns, cancellationToken);
        await ValidateSchemaObjectAsync(connection, transaction, "search_index_symbols", false, SymbolColumns, cancellationToken);
        await ValidateSchemaObjectAsync(connection, transaction, "search_index_fts", true, FtsColumns, cancellationToken);
    }

    private static async Task ValidateSchemaObjectAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string objectName,
        bool requireFts5,
        IReadOnlyCollection<string> expectedColumns,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $objectName;";
        command.Parameters.AddWithValue("$objectName", objectName);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string sql)
        {
            throw CreateIncompleteSchemaException(objectName, null);
        }

        if (requireFts5
            && (!sql.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("USING fts5", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "The SQLite search index schema is incompatible because search_index_fts is not an FTS5 virtual table. Delete the index database and run index again.");
        }

        await ValidateSchemaColumnsAsync(connection, transaction, objectName, expectedColumns, cancellationToken);
    }

    private static async Task ValidateSchemaColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string objectName,
        IReadOnlyCollection<string> expectedColumns,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({objectName});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var actualColumns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            actualColumns.Add(reader.GetString(1));
        }

        if (!actualColumns.SetEquals(expectedColumns))
        {
            throw CreateIncompatibleSchemaException(
                objectName,
                actualColumns,
                expectedColumns);
        }
    }

    private static async Task<bool> IsDatabaseEmptyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT NOT EXISTS (SELECT 1 FROM sqlite_master WHERE name NOT LIKE 'sqlite_%');";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
    }

    internal static async Task ReplaceTargetWithinLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteSearchIndexTarget target,
        IReadOnlyCollection<SqliteSearchIndexSymbol> symbols,
        CancellationToken cancellationToken)
    {
        ValidateTarget(target);
        ArgumentNullException.ThrowIfNull(symbols);
        ValidateSymbols(symbols, target.TargetIdentity);

        await DeleteTargetAsync(connection, transaction, target.TargetIdentity, cancellationToken);
        await InsertSymbolsAsync(connection, transaction, target.TargetIdentity, symbols, cancellationToken);
        await UpsertMetadataAsync(connection, transaction, target, symbols.Count, cancellationToken);
    }

    internal static async Task ReplaceProjectsWithinLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteSearchIndexTarget target,
        IReadOnlyCollection<RepositoryRelativePath> projectPaths,
        IReadOnlyCollection<SqliteSearchIndexSymbol> symbols,
        CancellationToken cancellationToken)
    {
        ValidateTarget(target);
        ArgumentNullException.ThrowIfNull(projectPaths);
        ArgumentNullException.ThrowIfNull(symbols);
        var normalizedProjectPaths = NormalizeProjectPaths(projectPaths);
        if (normalizedProjectPaths.Count == 0)
        {
            throw new ArgumentException("At least one project path must be provided for a project refresh.", nameof(projectPaths));
        }

        ValidateSymbols(symbols, target.TargetIdentity);
        var knownProjectPaths = normalizedProjectPaths.ToHashSet();
        if (symbols.Any(symbol => !knownProjectPaths.Contains(symbol.ProjectPath)))
        {
            throw new ArgumentException(
                "Every replacement symbol must belong to one of the selected project paths.",
                nameof(symbols));
        }

        await DeleteProjectsAsync(connection, transaction, target.TargetIdentity, normalizedProjectPaths, cancellationToken);
        await InsertSymbolsAsync(connection, transaction, target.TargetIdentity, symbols, cancellationToken);
        var symbolCount = await CountTargetSymbolsAsync(connection, transaction, target.TargetIdentity, cancellationToken);
        await UpsertMetadataAsync(connection, transaction, target, symbolCount, cancellationToken);
    }

    internal static async Task UpdateTargetMetadataWithinLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteSearchIndexTarget target,
        CancellationToken cancellationToken)
    {
        ValidateTarget(target);
        var symbolCount = await CountTargetSymbolsAsync(connection, transaction, target.TargetIdentity, cancellationToken);
        await UpsertMetadataAsync(connection, transaction, target, symbolCount, cancellationToken);
    }

    private static async Task DeleteTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepositoryRelativePath targetIdentity,
        CancellationToken cancellationToken)
    {
        await using var deleteFtsCommand = connection.CreateCommand();
        deleteFtsCommand.Transaction = transaction;
        deleteFtsCommand.CommandText = """
            DELETE FROM search_index_fts
            WHERE rowid IN (
                SELECT id
                FROM search_index_symbols
                WHERE target_identity = $targetIdentity
            );
            """;
        deleteFtsCommand.Parameters.AddWithValue("$targetIdentity", targetIdentity.Value);
        await deleteFtsCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var deleteSymbolsCommand = connection.CreateCommand();
        deleteSymbolsCommand.Transaction = transaction;
        deleteSymbolsCommand.CommandText = "DELETE FROM search_index_symbols WHERE target_identity = $targetIdentity;";
        deleteSymbolsCommand.Parameters.AddWithValue("$targetIdentity", targetIdentity.Value);
        await deleteSymbolsCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var deleteMetadataCommand = connection.CreateCommand();
        deleteMetadataCommand.Transaction = transaction;
        deleteMetadataCommand.CommandText = "DELETE FROM search_index_targets WHERE target_identity = $targetIdentity;";
        deleteMetadataCommand.Parameters.AddWithValue("$targetIdentity", targetIdentity.Value);
        await deleteMetadataCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteProjectsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepositoryRelativePath targetIdentity,
        IReadOnlyCollection<RepositoryRelativePath> projectPaths,
        CancellationToken cancellationToken)
    {
        await using var deleteFtsCommand = connection.CreateCommand();
        deleteFtsCommand.Transaction = transaction;
        var ftsFilters = new List<string> { "target_identity = $targetIdentity" };
        deleteFtsCommand.Parameters.AddWithValue("$targetIdentity", targetIdentity.Value);
        AddFilterValues(deleteFtsCommand, ftsFilters, "project_path", "$projectPath", projectPaths.Select(path => path.Value).ToArray());
        deleteFtsCommand.CommandText = $"""
            DELETE FROM search_index_fts
            WHERE rowid IN (
                SELECT id
                FROM search_index_symbols
                WHERE {string.Join(" AND ", ftsFilters)}
            );
            """;
        await deleteFtsCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var deleteSymbolsCommand = connection.CreateCommand();
        deleteSymbolsCommand.Transaction = transaction;
        var symbolFilters = new List<string> { "target_identity = $targetIdentity" };
        deleteSymbolsCommand.Parameters.AddWithValue("$targetIdentity", targetIdentity.Value);
        AddFilterValues(deleteSymbolsCommand, symbolFilters, "project_path", "$projectPath", projectPaths.Select(path => path.Value).ToArray());
        deleteSymbolsCommand.CommandText = $"DELETE FROM search_index_symbols WHERE {string.Join(" AND ", symbolFilters)};";
        await deleteSymbolsCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSymbolsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepositoryRelativePath targetIdentity,
        IReadOnlyCollection<SqliteSearchIndexSymbol> symbols,
        CancellationToken cancellationToken)
    {
        await using var symbolCommand = connection.CreateCommand();
        symbolCommand.Transaction = transaction;
        symbolCommand.CommandText = """
            INSERT INTO search_index_symbols (
                target_identity,
                symbol_key,
                project_path,
                project_name,
                kind,
                name,
                display_name,
                symbol_id,
                path,
                line,
                column_number,
                end_line,
                end_column_number,
                documentation,
                signature,
                comments,
                body)
            VALUES (
                $targetIdentity,
                $symbolKey,
                $projectPath,
                $projectName,
                $kind,
                $name,
                $displayName,
                $symbolId,
                $path,
                $line,
                $column,
                $endLine,
                $endColumn,
                $documentation,
                $signature,
                $comments,
                $body)
            RETURNING id;
            """;
        var targetIdentityParameter = symbolCommand.Parameters.Add("$targetIdentity", SqliteType.Text);
        var symbolKeyParameter = symbolCommand.Parameters.Add("$symbolKey", SqliteType.Text);
        var projectPathParameter = symbolCommand.Parameters.Add("$projectPath", SqliteType.Text);
        var projectNameParameter = symbolCommand.Parameters.Add("$projectName", SqliteType.Text);
        var kindParameter = symbolCommand.Parameters.Add("$kind", SqliteType.Text);
        var nameParameter = symbolCommand.Parameters.Add("$name", SqliteType.Text);
        var displayNameParameter = symbolCommand.Parameters.Add("$displayName", SqliteType.Text);
        var symbolIdParameter = symbolCommand.Parameters.Add("$symbolId", SqliteType.Text);
        var pathParameter = symbolCommand.Parameters.Add("$path", SqliteType.Text);
        var lineParameter = symbolCommand.Parameters.Add("$line", SqliteType.Integer);
        var columnParameter = symbolCommand.Parameters.Add("$column", SqliteType.Integer);
        var endLineParameter = symbolCommand.Parameters.Add("$endLine", SqliteType.Integer);
        var endColumnParameter = symbolCommand.Parameters.Add("$endColumn", SqliteType.Integer);
        var documentationParameter = symbolCommand.Parameters.Add("$documentation", SqliteType.Text);
        var signatureParameter = symbolCommand.Parameters.Add("$signature", SqliteType.Text);
        var commentsParameter = symbolCommand.Parameters.Add("$comments", SqliteType.Text);
        var bodyParameter = symbolCommand.Parameters.Add("$body", SqliteType.Text);

        await using var ftsCommand = connection.CreateCommand();
        ftsCommand.Transaction = transaction;
        ftsCommand.CommandText = """
            INSERT INTO search_index_fts (
                rowid,
                name_tokens,
                containing_tokens,
                details_tokens,
                path_tokens,
                body_tokens)
            VALUES (
                $rowId,
                $nameTokens,
                $containingTokens,
                $detailsTokens,
                $pathTokens,
                $bodyTokens);
            """;
        var rowIdParameter = ftsCommand.Parameters.Add("$rowId", SqliteType.Integer);
        var nameTokensParameter = ftsCommand.Parameters.Add("$nameTokens", SqliteType.Text);
        var containingTokensParameter = ftsCommand.Parameters.Add("$containingTokens", SqliteType.Text);
        var detailsTokensParameter = ftsCommand.Parameters.Add("$detailsTokens", SqliteType.Text);
        var pathTokensParameter = ftsCommand.Parameters.Add("$pathTokens", SqliteType.Text);
        var bodyTokensParameter = ftsCommand.Parameters.Add("$bodyTokens", SqliteType.Text);

        foreach (var symbol in symbols.OrderBy(static symbol => symbol.SymbolKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            targetIdentityParameter.Value = targetIdentity.Value;
            symbolKeyParameter.Value = symbol.SymbolKey;
            projectPathParameter.Value = symbol.ProjectPath.Value;
            projectNameParameter.Value = symbol.ProjectName;
            kindParameter.Value = symbol.Kind;
            nameParameter.Value = symbol.Name;
            displayNameParameter.Value = symbol.DisplayName;
            symbolIdParameter.Value = ToDatabaseValue(symbol.SymbolId);
            pathParameter.Value = symbol.Path.Value;
            lineParameter.Value = symbol.Line;
            columnParameter.Value = symbol.Column;
            endLineParameter.Value = symbol.EndLine;
            endColumnParameter.Value = symbol.EndColumn;
            documentationParameter.Value = ToDatabaseValue(symbol.Documentation);
            signatureParameter.Value = ToDatabaseValue(symbol.Signature);
            commentsParameter.Value = ToDatabaseValue(symbol.Comments);
            bodyParameter.Value = ToDatabaseValue(symbol.Body);

            var rowIdValue = await symbolCommand.ExecuteScalarAsync(cancellationToken);
            if (rowIdValue is null || rowIdValue is DBNull)
            {
                throw new InvalidOperationException("SQLite did not return an inserted search symbol row ID.");
            }

            rowIdParameter.Value = Convert.ToInt64(rowIdValue, CultureInfo.InvariantCulture);
            nameTokensParameter.Value = NormalizeSearchText(symbol.NameTokens);
            containingTokensParameter.Value = NormalizeSearchText(symbol.ContainingTokens);
            detailsTokensParameter.Value = NormalizeSearchText(symbol.DetailsTokens);
            pathTokensParameter.Value = NormalizeSearchText(symbol.PathTokens);
            bodyTokensParameter.Value = NormalizeSearchText(symbol.BodyTokens);
            await ftsCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpsertMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteSearchIndexTarget target,
        int symbolCount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO search_index_targets (
                target_identity,
                fingerprint,
                indexed_at_utc,
                symbol_count,
                schema_version)
            VALUES (
                $targetIdentity,
                $fingerprint,
                $indexedAtUtc,
                $symbolCount,
                $schemaVersion)
            ON CONFLICT (target_identity) DO UPDATE SET
                fingerprint = excluded.fingerprint,
                indexed_at_utc = excluded.indexed_at_utc,
                symbol_count = excluded.symbol_count,
                schema_version = excluded.schema_version;
            """;
        command.Parameters.AddWithValue("$targetIdentity", target.TargetIdentity.Value);
        command.Parameters.AddWithValue("$fingerprint", ToDatabaseValue(target.Fingerprint));
        command.Parameters.AddWithValue("$indexedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$symbolCount", symbolCount);
        command.Parameters.AddWithValue("$schemaVersion", CurrentSchemaVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> CountTargetSymbolsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepositoryRelativePath targetIdentity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM search_index_symbols WHERE target_identity = $targetIdentity;";
        command.Parameters.AddWithValue("$targetIdentity", targetIdentity.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteSearchIndexQuery query,
        string queryExpression,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BuildCountCommandText(query, command);
        command.Parameters.AddWithValue("$match", queryExpression);
        command.Parameters.AddWithValue("$targetIdentity", query.TargetIdentity.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static string BuildCountCommandText(SqliteSearchIndexQuery query, SqliteCommand command)
    {
        return $"""
            SELECT COUNT(*)
            FROM search_index_fts
            INNER JOIN search_index_symbols AS symbols ON symbols.id = search_index_fts.rowid
            WHERE {BuildSearchFilters(query, command)};
            """;
    }

    private static string BuildSearchCommandText(SqliteSearchIndexQuery query, SqliteCommand command)
    {
        var coverageExpression = BuildQueryTermCoverageExpression(command, query.Tokens);
        return $"""
            SELECT symbols.symbol_key,
                   symbols.project_path,
                   symbols.project_name,
                   symbols.kind,
                   symbols.name,
                   symbols.display_name,
                   symbols.symbol_id,
                   symbols.path,
                   symbols.line,
                   symbols.column_number,
                   symbols.end_line,
                   symbols.end_column_number,
                   symbols.documentation,
                   symbols.signature,
                   symbols.comments,
                   symbols.body,
                   bm25(search_index_fts, {NameWeight}, {ContainingWeight}, {DetailsWeight}, {PathWeight}, {BodyWeight}) AS bm25_score,
                   {coverageExpression} AS query_term_coverage
            FROM search_index_fts
            INNER JOIN search_index_symbols AS symbols ON symbols.id = search_index_fts.rowid
            WHERE {BuildSearchFilters(query, command)}
            ORDER BY query_term_coverage DESC,
                     bm25_score ASC,
                     symbols.display_name COLLATE BINARY ASC,
                     symbols.path COLLATE BINARY ASC,
                     symbols.line ASC,
                     symbols.column_number ASC,
                     symbols.symbol_key COLLATE BINARY ASC
            LIMIT $maxResults;
            """;
    }

    private static string BuildQueryTermCoverageExpression(SqliteCommand command, IReadOnlyList<string> tokens)
    {
        var normalizedTokens = GetNormalizedSearchTokens(tokens);
        if (normalizedTokens.Length == 0)
        {
            return "0";
        }

        var termExpressions = new List<string>(normalizedTokens.Length);
        for (var index = 0; index < normalizedTokens.Length; index++)
        {
            var parameterName = $"$coveragePattern{index}";
            command.Parameters.AddWithValue(parameterName, $"% {normalizedTokens[index]}%");
            termExpressions.Add($"""
                CASE WHEN (' ' || lower(search_index_fts.name_tokens)) LIKE {parameterName}
                           OR (' ' || lower(search_index_fts.containing_tokens)) LIKE {parameterName}
                           OR (' ' || lower(search_index_fts.details_tokens)) LIKE {parameterName}
                           OR (' ' || lower(search_index_fts.path_tokens)) LIKE {parameterName}
                           OR (' ' || lower(search_index_fts.body_tokens)) LIKE {parameterName}
                     THEN 1 ELSE 0 END
                """);
        }

        return $"({string.Join(" + ", termExpressions)})";
    }

    private static string BuildSearchFilters(SqliteSearchIndexQuery query, SqliteCommand command)
    {
        var filters = new List<string>
        {
            "search_index_fts MATCH $match",
            "symbols.target_identity = $targetIdentity",
        };

        AddFilterValues(
            command,
            filters,
            "symbols.project_path",
            "$projectPath",
            query.ProjectPaths?.Select(path => path.Value).ToArray());
        AddFilterValues(command, filters, "symbols.kind", "$kind", query.Kinds);
        return string.Join(" AND ", filters);
    }

    private static void AddFilterValues(
        SqliteCommand command,
        ICollection<string> filters,
        string fieldName,
        string parameterPrefix,
        IReadOnlyCollection<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return;
        }

        var normalizedValues = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (normalizedValues.Length == 0)
        {
            return;
        }

        var parameterNames = new List<string>(normalizedValues.Length);
        for (var index = 0; index < normalizedValues.Length; index++)
        {
            var parameterName = $"{parameterPrefix}{index}";
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, normalizedValues[index]);
        }

        filters.Add($"{fieldName} IN ({string.Join(", ", parameterNames)})");
    }

    private static IReadOnlyCollection<RepositoryRelativePath> NormalizeProjectPaths(
        IReadOnlyCollection<RepositoryRelativePath> projectPaths)
    {
        ValidateRepositoryRelativePaths(projectPaths, "Search index project path");
        return projectPaths
            .Distinct()
            .OrderBy(path => path.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? BuildMatchExpression(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var normalizedTokens = GetNormalizedSearchTokens(tokens);
        return normalizedTokens.Length == 0
            ? null
            : string.Join(" OR ", normalizedTokens.Select(static token => $"\"{token}\"*"));
    }

    private static string[] GetNormalizedSearchTokens(IReadOnlyList<string> tokens)
    {
        return tokens
            .Select(NormalizeSearchToken)
            .Where(static token => token.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? SelectExcerpt(
        string? documentation,
        string? comments,
        string? signature,
        string? body,
        IReadOnlyList<string> queryTokens)
    {
        var normalizedTokens = GetNormalizedSearchTokens(queryTokens);
        if (normalizedTokens.Length == 0)
        {
            return null;
        }

        foreach (var candidate in new[] { documentation, comments, signature, body })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var normalizedCandidate = NormalizeExcerptWhitespace(candidate);
            var matchIndex = FindFirstMatchIndex(normalizedCandidate, normalizedTokens);
            if (matchIndex >= 0)
            {
                return BoundExcerptAroundMatch(normalizedCandidate, matchIndex);
            }
        }

        return null;
    }

    private static int FindFirstMatchIndex(string value, IReadOnlyList<string> tokens)
    {
        var matchIndex = int.MaxValue;
        foreach (var token in tokens)
        {
            var index = FindTokenPrefixIndex(value, token);
            if (index >= 0 && index < matchIndex)
            {
                matchIndex = index;
            }
        }

        return matchIndex == int.MaxValue ? -1 : matchIndex;
    }

    private static int FindTokenPrefixIndex(string value, string token)
    {
        for (var index = 0; index <= value.Length - token.Length; index++)
        {
            if (!IsSearchTokenBoundary(value, index)
                || !value.AsSpan(index, token.Length).Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return index;
        }

        return -1;
    }

    private static bool IsSearchTokenBoundary(string value, int index)
    {
        if (index == 0)
        {
            return true;
        }

        var previous = value[index - 1];
        var current = value[index];
        if (!char.IsAsciiLetterOrDigit(previous) || !char.IsAsciiLetterOrDigit(current))
        {
            return true;
        }

        if (char.IsUpper(current) && char.IsLower(previous))
        {
            return true;
        }

        if (char.IsDigit(current) != char.IsDigit(previous))
        {
            return true;
        }

        return char.IsUpper(current)
            && char.IsUpper(previous)
            && index + 1 < value.Length
            && char.IsLower(value[index + 1]);
    }

    private static string BoundExcerptAroundMatch(string value, int matchIndex)
    {
        const int maximumExcerptLength = 320;
        if (value.Length <= maximumExcerptLength)
        {
            return value;
        }

        const int preferredLeadingContextLength = 80;
        var startIndex = Math.Min(
            Math.Max(0, matchIndex - preferredLeadingContextLength),
            value.Length - maximumExcerptLength);
        return value.Substring(startIndex, maximumExcerptLength);
    }

    private static string NormalizeExcerptWhitespace(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string NormalizeSearchToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var characters = token.Where(static character => char.IsAsciiLetterOrDigit(character));
        return string.Concat(characters).ToLowerInvariant();
    }

    private static string NormalizeSearchText(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
    }

    private static object ToDatabaseValue(string? value)
    {
        return value is null ? DBNull.Value : value;
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateTarget(SqliteSearchIndexTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateTargetIdentity(target.TargetIdentity);
    }

    private static void ValidateTargetIdentity(RepositoryRelativePath targetIdentity)
    {
        ValidateRepositoryRelativePath(targetIdentity, "Search index target identity");
    }

    private static void ValidateQuery(SqliteSearchIndexQuery query)
    {
        ValidateTargetIdentity(query.TargetIdentity);
        ValidateRepositoryRelativePaths(query.ProjectPaths, "Search query project path");
    }

    private static void ValidateRepositoryRelativePaths(
        IReadOnlyCollection<RepositoryRelativePath>? paths,
        string pathDescription)
    {
        if (paths is null)
        {
            return;
        }

        foreach (var path in paths)
        {
            ValidateRepositoryRelativePath(path, pathDescription);
        }
    }

    private static void ValidateRepositoryRelativePath(RepositoryRelativePath path, string pathDescription)
    {
        if (string.IsNullOrWhiteSpace(path.Value))
        {
            throw new ArgumentException(
                $"{pathDescription} must not be empty.",
                nameof(path));
        }

        _ = RepositoryRelativePath.FromStoredValue(path.Value, pathDescription);
    }

    private static void ValidateSymbolKey(
        string symbolKey,
        RepositoryRelativePath targetIdentity,
        RepositoryRelativePath projectPath,
        RepositoryRelativePath sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolKey);
        var expectedPathPrefix = $"{targetIdentity.Value}|{projectPath.Value}|{sourcePath.Value}|";
        if (!symbolKey.StartsWith(expectedPathPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Search symbol key path components must match the target identity, project path, and source path stored with the symbol.",
                nameof(symbolKey));
        }

        var identityAndSpan = symbolKey.AsSpan(expectedPathPrefix.Length);
        var spanSeparator = identityAndSpan.LastIndexOf('|');
        if (spanSeparator <= 0 || spanSeparator == identityAndSpan.Length - 1)
        {
            throw new ArgumentException(
                "Search symbol keys must use the '<target>|<project>|<source>|<symbol>|<span>' structure.",
                nameof(symbolKey));
        }
    }

    private static void ValidateLeaseTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromMilliseconds(int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The writer lease timeout must be between zero and the maximum SQLite busy timeout.");
        }
    }

    private static int GetBusyTimeoutMilliseconds(TimeSpan timeout)
    {
        return (int)Math.Ceiling(timeout.TotalMilliseconds);
    }

    private static int GetCommandTimeoutSeconds(TimeSpan timeout)
    {
        return Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
    }

    private static bool IsWriterContention(SqliteException exception)
    {
        return exception.SqliteErrorCode is 5 or 6;
    }

    private static bool IsMissingSchema(SqliteException exception)
    {
        return exception.SqliteErrorCode == 1
            && exception.Message.Contains("no such table: search_index_schema", StringComparison.Ordinal);
    }

    private static bool IsInvalidDatabase(SqliteException exception)
    {
        return exception.SqliteErrorCode == 26;
    }

    private bool IsEmptyDatabaseFile()
    {
        return File.Exists(_databasePath) && new FileInfo(_databasePath).Length == 0;
    }

    private InvalidOperationException CreateInvalidDatabaseException(SqliteException exception)
    {
        return new InvalidOperationException(
            $"The search index path '{_databasePath}' is not a valid SQLite database. Delete or replace the file, then run index again.",
            exception);
    }

    private static InvalidOperationException CreateIncompleteSchemaException(string objectName, Exception? innerException)
    {
        return new InvalidOperationException(
            $"The SQLite search index schema is incomplete because required object '{objectName}' is missing. Delete the index database and run index again.",
            innerException);
    }

    private static InvalidOperationException CreateIncompatibleSchemaException(
        string objectName,
        IEnumerable<string> actualColumns,
        IEnumerable<string> expectedColumns)
    {
        return new InvalidOperationException(
            $"The SQLite search index schema is incompatible because table '{objectName}' has columns [{string.Join(", ", actualColumns.Order(StringComparer.Ordinal))}] but requires [{string.Join(", ", expectedColumns.Order(StringComparer.Ordinal))}]. Delete the index database and run index again.");
    }

    private static void ValidateSymbols(
        IReadOnlyCollection<SqliteSearchIndexSymbol> symbols,
        RepositoryRelativePath targetIdentity)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            ArgumentNullException.ThrowIfNull(symbol);
            ArgumentException.ThrowIfNullOrWhiteSpace(symbol.SymbolKey);
            ValidateRepositoryRelativePath(symbol.ProjectPath, "Search symbol project path");
            ArgumentException.ThrowIfNullOrWhiteSpace(symbol.ProjectName);
            ArgumentException.ThrowIfNullOrWhiteSpace(symbol.Kind);
            ArgumentException.ThrowIfNullOrWhiteSpace(symbol.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(symbol.DisplayName);
            ValidateRepositoryRelativePath(symbol.Path, "Search symbol source path");
            ValidateSymbolKey(symbol.SymbolKey, targetIdentity, symbol.ProjectPath, symbol.Path);
            if (symbol.Line <= 0 || symbol.Column <= 0 || symbol.EndLine <= 0 || symbol.EndColumn <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(symbols), "Search symbol ranges must use one-based positive line and column values.");
            }

            if (symbol.EndLine < symbol.Line
                || (symbol.EndLine == symbol.Line && symbol.EndColumn < symbol.Column))
            {
                throw new ArgumentOutOfRangeException(nameof(symbols), "Search symbol ranges must end at or after their start location.");
            }

            if (!keys.Add(symbol.SymbolKey))
            {
                throw new ArgumentException($"The search index target contains duplicate symbol key '{symbol.SymbolKey}'.", nameof(symbols));
            }
        }
    }
}
