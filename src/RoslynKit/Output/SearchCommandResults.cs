namespace RoslynKit;

/// <summary>
/// Represents the coherent index state used to answer a search request.
/// </summary>
public enum SearchIndexState
{
    Fresh,
    Stale,
}

/// <summary>
/// Represents the <c>index</c> command payload after building or refreshing one target partition.
/// </summary>
public sealed record IndexResult(
    string TargetPath,
    string IndexPath,
    SearchIndexState IndexState,
    int SymbolCount,
    bool Rebuilt,
    IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics);

/// <summary>
/// Represents the <c>search</c> command payload with ranked symbol hits from one target index.
/// </summary>
public sealed record SearchResult(
    string TargetPath,
    string IndexPath,
    string Query,
    SearchIndexState IndexState,
    int TotalCount,
    int ReturnedCount,
    bool Truncated,
    IReadOnlyList<SearchHit> Hits,
    IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics);

/// <summary>
/// Represents one ranked C# symbol hit returned by a full-text search query.
/// </summary>
public sealed record SearchHit(
    string DisplayName,
    string Kind,
    SourceRange Location,
    string? SymbolId,
    string? Excerpt);
