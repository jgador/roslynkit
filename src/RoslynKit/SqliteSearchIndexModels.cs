namespace RoslynKit;

/// <summary>
/// Identifies one target partition persisted in a shared SQLite search database.
/// </summary>
internal sealed record SqliteSearchIndexTarget(
    RepositoryRelativePath TargetIdentity,
    string? Fingerprint);

/// <summary>
/// Describes one navigable source symbol and its pre-tokenized searchable fields.
/// </summary>
internal sealed record SqliteSearchIndexSymbol(
    string SymbolKey,
    RepositoryRelativePath ProjectPath,
    string ProjectName,
    string Kind,
    string Name,
    string DisplayName,
    string? SymbolId,
    RepositoryRelativePath Path,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string? Documentation,
    string? Signature,
    string? Comments,
    string? Body,
    string NameTokens,
    string ContainingTokens,
    string DetailsTokens,
    string PathTokens,
    string BodyTokens);

/// <summary>
/// Represents a tokenized full-text search request over one target partition.
/// </summary>
internal sealed record SqliteSearchIndexQuery(
    RepositoryRelativePath TargetIdentity,
    IReadOnlyList<string> Tokens,
    IReadOnlyCollection<RepositoryRelativePath>? ProjectPaths = null,
    IReadOnlyCollection<string>? Kinds = null,
    int MaxResults = 20);

/// <summary>
/// Reports persistent metadata for one indexed target partition.
/// </summary>
internal sealed record SqliteSearchIndexMetadata(
    int SchemaVersion,
    RepositoryRelativePath TargetIdentity,
    string? Fingerprint,
    DateTimeOffset IndexedAtUtc,
    int SymbolCount);

/// <summary>
/// Represents one ranked persistent search match before markdown projection.
/// </summary>
internal sealed record SqliteSearchIndexMatch(
    string SymbolKey,
    RepositoryRelativePath ProjectPath,
    string ProjectName,
    string Kind,
    string Name,
    string DisplayName,
    string? SymbolId,
    RepositoryRelativePath Path,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string? Documentation,
    string? Signature,
    string? Excerpt,
    SearchExcerptSource? ExcerptSource,
    int QueryTermCoverage,
    double RawBm25Score);

/// <summary>
/// Contains bounded ranked matches and the total count before the result limit was applied.
/// </summary>
internal sealed record SqliteSearchIndexSearchResult(
    int TotalMatchCount,
    IReadOnlyList<SqliteSearchIndexMatch> Matches);

/// <summary>
/// Captures one target's metadata and ranked search results from the same SQLite read transaction.
/// </summary>
internal sealed record SqliteSearchIndexSearchSnapshot(
    SqliteSearchIndexMetadata? Metadata,
    SqliteSearchIndexSearchResult SearchResult);
