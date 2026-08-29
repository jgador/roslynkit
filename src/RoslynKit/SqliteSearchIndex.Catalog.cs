using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace RoslynKit;

internal sealed partial class SqliteSearchIndex
{
    /// <summary>
    /// Reports whether one target has a populated semantic catalog.
    /// </summary>
    public async Task<bool> HasCatalogTargetAsync(
        RepositoryRelativePath targetIdentity,
        CancellationToken cancellationToken)
    {
        ValidateTargetIdentity(targetIdentity);
        if (!File.Exists(_databasePath))
        {
            return false;
        }

        var readConnection = await TryOpenReadConnectionAsync(cancellationToken);
        if (readConnection is null)
        {
            return false;
        }

        await using var connection = readConnection;
        if (!await HasCatalogSchemaAsync(connection, cancellationToken))
        {
            return false;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM semantic_catalog_projects
                WHERE target_identity = $targetIdentity
            );
            """;
        command.Parameters.AddWithValue("$targetIdentity", targetIdentity.Value);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>
    /// Reads declaration rows matching an exact documentation-comment ID or qualified display name.
    /// </summary>
    public async Task<IReadOnlyList<SqliteSearchIndexSymbol>> ReadCatalogSymbolsAsync(
        RepositoryRelativePath targetIdentity,
        string selector,
        CancellationToken cancellationToken)
    {
        ValidateTargetIdentity(targetIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        if (!File.Exists(_databasePath))
        {
            return [];
        }

        var readConnection = await TryOpenReadConnectionAsync(cancellationToken);
        if (readConnection is null)
        {
            return [];
        }

        await using var connection = readConnection;
        if (!await HasCatalogSchemaAsync(connection, cancellationToken))
        {
            return [];
        }

        var isDeclarationId = IsDeclarationId(selector);
        var parameters = new DynamicParameters();
        parameters.Add("targetIdentity", targetIdentity.Value);
        parameters.Add("selector", selector);
        parameters.Add("selectorWithReturnType", $"{selector}~%");
        var command = new CommandDefinition(
            $"""
            SELECT target_identity AS TargetIdentity,
                   symbol_key AS SymbolKey,
                   project_path AS ProjectPath,
                   project_name AS ProjectName,
                   kind AS Kind,
                   name AS Name,
                   metadata_name AS MetadataName,
                   display_name AS DisplayName,
                   symbol_id AS SymbolId,
                   symbol_kind AS SymbolKind,
                   accessibility AS Accessibility,
                   is_static AS IsStatic,
                   containing_type AS ContainingType,
                   containing_namespace AS ContainingNamespace,
                   path AS Path,
                   line AS Line,
                   column_number AS Column,
                   end_line AS EndLine,
                   end_column_number AS EndColumn,
                   span_start AS SpanStart,
                   span_length AS SpanLength,
                   documentation AS Documentation,
                   comments_json AS CommentsJson
            FROM semantic_catalog_symbols
            WHERE target_identity = @targetIdentity
              AND {(isDeclarationId
                  ? "(symbol_id = @selector OR (instr(@selector, '~') = 0 AND symbol_id LIKE @selectorWithReturnType))"
                  : "display_name = @selector")}
            ORDER BY project_name,
                     project_path,
                     path,
                     line,
                     column_number;
            """,
            parameters,
            cancellationToken: cancellationToken);
        var rows = await connection.QueryAsync<SemanticCatalogSymbolRow>(command);
        return rows.Select(ToCatalogSymbol).ToArray();
    }

    /// <summary>
    /// Reads exact-name declarations from one target for cache-backed <c>symbols --exact</c>.
    /// </summary>
    public async Task<IReadOnlyList<SqliteSearchIndexSymbol>> ReadCatalogSymbolsByNameAsync(
        RepositoryRelativePath targetIdentity,
        string name,
        bool caseSensitive,
        IReadOnlyCollection<string>? kinds,
        CancellationToken cancellationToken)
    {
        ValidateTargetIdentity(targetIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!File.Exists(_databasePath))
        {
            return [];
        }

        var readConnection = await TryOpenReadConnectionAsync(cancellationToken);
        if (readConnection is null)
        {
            return [];
        }

        await using var connection = readConnection;
        if (!await HasCatalogSchemaAsync(connection, cancellationToken))
        {
            return [];
        }

        var parameters = new DynamicParameters();
        parameters.Add("targetIdentity", targetIdentity.Value);
        parameters.Add("name", name);
        var filters = new List<string>
        {
            "target_identity = @targetIdentity",
            caseSensitive ? "name = @name COLLATE BINARY" : "name = @name COLLATE NOCASE",
        };
        if (kinds is { Count: > 0 })
        {
            var kindParameters = kinds
                .Order(StringComparer.Ordinal)
                .Select((kind, index) =>
                {
                    var parameterName = $"kind{index}";
                    parameters.Add(parameterName, kind);
                    return $"@{parameterName}";
                });
            filters.Add($"kind IN ({string.Join(", ", kindParameters)})");
        }

        var command = new CommandDefinition(
            $"""
            SELECT target_identity AS TargetIdentity,
                   symbol_key AS SymbolKey,
                   project_path AS ProjectPath,
                   project_name AS ProjectName,
                   kind AS Kind,
                   name AS Name,
                   metadata_name AS MetadataName,
                   display_name AS DisplayName,
                   symbol_id AS SymbolId,
                   symbol_kind AS SymbolKind,
                   accessibility AS Accessibility,
                   is_static AS IsStatic,
                   containing_type AS ContainingType,
                   containing_namespace AS ContainingNamespace,
                   path AS Path,
                   line AS Line,
                   column_number AS Column,
                   end_line AS EndLine,
                   end_column_number AS EndColumn,
                   span_start AS SpanStart,
                   span_length AS SpanLength,
                   documentation AS Documentation,
                   comments_json AS CommentsJson
            FROM semantic_catalog_symbols
            WHERE {string.Join(" AND ", filters)}
            ORDER BY display_name,
                     kind,
                     project_name,
                     project_path,
                     path,
                     line,
                     column_number;
            """,
            parameters,
            cancellationToken: cancellationToken);
        var rows = await connection.QueryAsync<SemanticCatalogSymbolRow>(command);
        return rows.Select(ToCatalogSymbol).ToArray();
    }

    /// <summary>
    /// Reads symbols that implement, inherit, or override one documentation-comment ID.
    /// </summary>
    public async Task<IReadOnlyList<SqliteSearchIndexSymbol>> ReadCatalogImplementationsAsync(
        RepositoryRelativePath targetIdentity,
        string targetSymbolId,
        CancellationToken cancellationToken)
    {
        ValidateTargetIdentity(targetIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSymbolId);
        if (!File.Exists(_databasePath))
        {
            return [];
        }

        var readConnection = await TryOpenReadConnectionAsync(cancellationToken);
        if (readConnection is null)
        {
            return [];
        }

        await using var connection = readConnection;
        if (!await HasCatalogSchemaAsync(connection, cancellationToken))
        {
            return [];
        }

        var parameters = new DynamicParameters();
        parameters.Add("targetIdentity", targetIdentity.Value);
        parameters.Add("targetSymbolId", targetSymbolId);
        var command = new CommandDefinition(
            """
            SELECT symbol.target_identity AS TargetIdentity,
                   symbol.symbol_key AS SymbolKey,
                   symbol.project_path AS ProjectPath,
                   symbol.project_name AS ProjectName,
                   symbol.kind AS Kind,
                   symbol.name AS Name,
                   symbol.metadata_name AS MetadataName,
                   symbol.display_name AS DisplayName,
                   symbol.symbol_id AS SymbolId,
                   symbol.symbol_kind AS SymbolKind,
                   symbol.accessibility AS Accessibility,
                   symbol.is_static AS IsStatic,
                   symbol.containing_type AS ContainingType,
                   symbol.containing_namespace AS ContainingNamespace,
                   symbol.path AS Path,
                   symbol.line AS Line,
                   symbol.column_number AS Column,
                   symbol.end_line AS EndLine,
                   symbol.end_column_number AS EndColumn,
                   symbol.span_start AS SpanStart,
                   symbol.span_length AS SpanLength,
                   symbol.documentation AS Documentation,
                   symbol.comments_json AS CommentsJson
            FROM semantic_catalog_relations AS relation
            JOIN semantic_catalog_symbols AS symbol
              ON symbol.target_identity = relation.target_identity
             AND symbol.symbol_key = relation.source_symbol_key
            WHERE relation.target_identity = @targetIdentity
              AND relation.target_symbol_id = @targetSymbolId
              AND relation.relation_kind IN ('implements', 'inherits', 'overrides')
            ORDER BY symbol.display_name,
                     symbol.kind,
                     symbol.project_name,
                     symbol.project_path,
                     symbol.path,
                     symbol.line,
                     symbol.column_number;
            """,
            parameters,
            cancellationToken: cancellationToken);
        var rows = await connection.QueryAsync<SemanticCatalogSymbolRow>(command);
        return rows.Select(ToCatalogSymbol).ToArray();
    }

    /// <summary>
    /// Reads one versioned, exact-invocation semantic operation result.
    /// </summary>
    public async Task<string?> ReadCatalogOperationAsync(
        RepositoryRelativePath targetIdentity,
        string operationKey,
        string resultType,
        int formatVersion,
        CancellationToken cancellationToken)
    {
        ValidateTargetIdentity(targetIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultType);
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
        if (!await HasCatalogSchemaAsync(connection, cancellationToken))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json
            FROM semantic_catalog_operation_cache
            WHERE target_identity = $targetIdentity
              AND operation_key = $operationKey
              AND result_type = $resultType
              AND format_version = $formatVersion;
            """;
        command.Parameters.AddWithValue("$targetIdentity", targetIdentity.Value);
        command.Parameters.AddWithValue("$operationKey", operationKey);
        command.Parameters.AddWithValue("$resultType", resultType);
        command.Parameters.AddWithValue("$formatVersion", formatVersion);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>
    /// Writes one versioned, exact-invocation semantic operation result atomically.
    /// </summary>
    public async Task WriteCatalogOperationAsync(
        RepositoryRelativePath targetIdentity,
        string operationKey,
        string resultType,
        int formatVersion,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        ValidateTargetIdentity(targetIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        await using var lease = await AcquireWriterLeaseAsync(
            TimeSpan.FromMilliseconds(BusyTimeoutMilliseconds),
            cancellationToken);
        await lease.WriteCatalogOperationAsync(
            targetIdentity,
            operationKey,
            resultType,
            formatVersion,
            payloadJson,
            cancellationToken);
        await lease.CommitAsync(cancellationToken);
    }

    internal static async Task WriteCatalogOperationWithinLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepositoryRelativePath targetIdentity,
        string operationKey,
        string resultType,
        int formatVersion,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO semantic_catalog_operation_cache (
                target_identity,
                operation_key,
                result_type,
                format_version,
                payload_json)
            VALUES (
                $targetIdentity,
                $operationKey,
                $resultType,
                $formatVersion,
                $payloadJson)
            ON CONFLICT (target_identity, operation_key) DO UPDATE SET
                result_type = excluded.result_type,
                format_version = excluded.format_version,
                payload_json = excluded.payload_json;
            """;
        command.Parameters.AddWithValue("$targetIdentity", targetIdentity.Value);
        command.Parameters.AddWithValue("$operationKey", operationKey);
        command.Parameters.AddWithValue("$resultType", resultType);
        command.Parameters.AddWithValue("$formatVersion", formatVersion);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasCatalogSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN (
                  'semantic_catalog_projects',
                  'semantic_catalog_symbols',
                  'semantic_catalog_relations',
                  'semantic_catalog_operation_cache');
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture) == 4;
    }

    private static bool IsDeclarationId(string selector)
    {
        return selector.Length > 2
            && selector[1] == ':'
            && selector[0] is 'T' or 'M' or 'P' or 'F' or 'E' or 'N';
    }

    private static SqliteSearchIndexSymbol ToCatalogSymbol(SemanticCatalogSymbolRow row)
    {
        return new SqliteSearchIndexSymbol(
            row.SymbolKey,
            RepositoryRelativePath.FromStoredValue(row.ProjectPath, "Catalog project path"),
            row.ProjectName,
            row.Kind,
            row.Name,
            row.DisplayName,
            row.SymbolId,
            RepositoryRelativePath.FromStoredValue(row.Path, "Catalog source path"),
            row.Line,
            row.Column,
            row.EndLine,
            row.EndColumn,
            row.Documentation,
            Signature: null,
            Comments: null,
            Body: null,
            NameTokens: string.Empty,
            ContainingTokens: string.Empty,
            DetailsTokens: string.Empty,
            PathTokens: string.Empty,
            BodyTokens: string.Empty,
            row.MetadataName,
            row.SymbolKind,
            row.Accessibility,
            row.IsStatic,
            row.ContainingType,
            row.ContainingNamespace,
            row.SpanStart,
            row.SpanLength,
            JsonSerializer.Deserialize<SqliteSearchIndexComment[]>(row.CommentsJson) ?? [],
            Relations: null);
    }

    private sealed class SemanticCatalogSymbolRow
    {
        public string TargetIdentity { get; set; } = string.Empty;

        public string SymbolKey { get; set; } = string.Empty;

        public string ProjectPath { get; set; } = string.Empty;

        public string ProjectName { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string MetadataName { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string? SymbolId { get; set; }

        public string SymbolKind { get; set; } = string.Empty;

        public string Accessibility { get; set; } = string.Empty;

        public bool IsStatic { get; set; }

        public string? ContainingType { get; set; }

        public string? ContainingNamespace { get; set; }

        public string Path { get; set; } = string.Empty;

        public int Line { get; set; }

        public int Column { get; set; }

        public int EndLine { get; set; }

        public int EndColumn { get; set; }

        public int SpanStart { get; set; }

        public int SpanLength { get; set; }

        public string? Documentation { get; set; }

        public string CommentsJson { get; set; } = "[]";
    }
}
