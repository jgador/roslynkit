namespace RoslynKit;

/// <summary>
/// Identifies one fresh semantic catalog partition and its repository paths.
/// </summary>
internal sealed record SemanticCatalogContext(
    SearchIndexPath Path,
    RepositoryRelativePath TargetIdentity,
    SqliteSearchIndex Index);
