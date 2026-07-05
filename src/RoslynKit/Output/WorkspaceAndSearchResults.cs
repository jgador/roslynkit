namespace RoslynKit;

/// <summary>
/// Names the RoslynKit document kinds surfaced in command output.
/// </summary>
public static class DocumentKindNames
{
    /// <summary>
    /// Workspace document kind for source files compiled directly by a project.
    /// </summary>
    public const string Source = "source";

    /// <summary>
    /// Workspace document kind for source files produced by generators during workspace load.
    /// </summary>
    public const string SourceGenerated = "sourceGenerated";

    /// <summary>
    /// Workspace document kind for non-source additional files supplied to analyzers.
    /// </summary>
    public const string Additional = "additional";

    /// <summary>
    /// Workspace document kind for analyzer configuration inputs such as <c>.editorconfig</c>.
    /// </summary>
    public const string AnalyzerConfig = "analyzerConfig";
}

/// <summary>
/// Represents the <c>workspace</c> command payload with loaded projects, document descriptors, and workspace diagnostics.
/// </summary>
public sealed class WorkspaceResult
{
    public WorkspaceResult(
        string targetPath,
        string targetKind,
        IReadOnlyList<WorkspaceProject> projects,
        IReadOnlyList<DocumentDescriptor> documents,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        TargetPath = targetPath;
        TargetKind = targetKind;
        Projects = projects;
        Documents = documents;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    /// <summary>
    /// Absolute path to the solution or project loaded for the command.
    /// </summary>
    public string TargetPath { get; }

    /// <summary>
    /// Target file kind reported by workspace output.
    /// </summary>
    public string TargetKind { get; }

    /// <summary>
    /// Loaded projects after RoslynKit ordering and target-framework labeling.
    /// </summary>
    public IReadOnlyList<WorkspaceProject> Projects { get; }

    /// <summary>
    /// Command-addressable documents discovered in the loaded workspace.
    /// </summary>
    public IReadOnlyList<DocumentDescriptor> Documents { get; }

    /// <summary>
    /// Non-fatal workspace load diagnostics emitted while opening the target.
    /// </summary>
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Represents the <c>diagnostics</c> command payload with ordered source diagnostics from the loaded target.
/// </summary>
public sealed class DiagnosticsResult
{
    public DiagnosticsResult(
        string targetPath,
        int totalCount,
        int returnedCount,
        bool truncated,
        IReadOnlyList<DiagnosticItem> diagnostics,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        TargetPath = targetPath;
        TotalCount = totalCount;
        ReturnedCount = returnedCount;
        Truncated = truncated;
        Diagnostics = diagnostics;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    /// <summary>
    /// Absolute path to the solution or project inspected for compiler diagnostics.
    /// </summary>
    public string TargetPath { get; }

    /// <summary>
    /// Total matching diagnostic count before <c>--max-results</c> limiting.
    /// </summary>
    public int TotalCount { get; }

    /// <summary>
    /// Number of diagnostics included in this command payload.
    /// </summary>
    public int ReturnedCount { get; }

    /// <summary>
    /// Indicates whether diagnostics were omitted because of the result limit.
    /// </summary>
    public bool Truncated { get; }

    /// <summary>
    /// Ordered compiler diagnostics projected into RoslynKit output.
    /// </summary>
    public IReadOnlyList<DiagnosticItem> Diagnostics { get; }

    /// <summary>
    /// Non-fatal workspace load diagnostics emitted while opening the target.
    /// </summary>
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Represents the <c>symbols</c> command payload with matching declarations from the loaded target.
/// </summary>
public sealed class SymbolsResult
{
    public SymbolsResult(
        string targetPath,
        string query,
        int totalCount,
        int returnedCount,
        bool truncated,
        IReadOnlyList<SymbolItem> symbols,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        TargetPath = targetPath;
        Query = query;
        TotalCount = totalCount;
        ReturnedCount = returnedCount;
        Truncated = truncated;
        Symbols = symbols;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    /// <summary>
    /// Absolute path to the solution or project searched for declarations.
    /// </summary>
    public string TargetPath { get; }

    /// <summary>
    /// Symbol-name query text after parser binding.
    /// </summary>
    public string Query { get; }

    /// <summary>
    /// Total matching declaration count before <c>--max-results</c> limiting.
    /// </summary>
    public int TotalCount { get; }

    /// <summary>
    /// Number of symbol items included in this command payload.
    /// </summary>
    public int ReturnedCount { get; }

    /// <summary>
    /// Indicates whether symbol matches were omitted because of the result limit.
    /// </summary>
    public bool Truncated { get; }

    /// <summary>
    /// Matching source declarations projected into RoslynKit symbol output.
    /// </summary>
    public IReadOnlyList<SymbolItem> Symbols { get; }

    /// <summary>
    /// Non-fatal workspace load diagnostics emitted while opening the target.
    /// </summary>
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}
