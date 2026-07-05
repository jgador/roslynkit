namespace RoslynKit;

/// <summary>
/// Represents the <c>document-text</c> command payload for one resolved document read.
/// </summary>
public sealed class DocumentTextResult
{
    public DocumentTextResult(
        DocumentDescriptor document,
        DocumentRange resolvedRange,
        string text,
        bool truncated,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        Document = document;
        ResolvedRange = resolvedRange;
        Text = text;
        Truncated = truncated;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    /// <summary>
    /// Descriptor for the resolved document whose full text was read.
    /// </summary>
    public DocumentDescriptor Document { get; }

    /// <summary>
    /// One-based span covered by the returned document text.
    /// </summary>
    public DocumentRange ResolvedRange { get; }

    /// <summary>
    /// Returned document text, fenced verbatim by the markdown renderer.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Indicates whether the returned text was shortened for output safety.
    /// </summary>
    public bool Truncated { get; }

    /// <summary>
    /// Non-fatal workspace load diagnostics emitted while opening the target.
    /// </summary>
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Represents the <c>document-lines</c> command payload for a bounded range in one resolved document.
/// </summary>
public sealed class DocumentLinesResult
{
    public DocumentLinesResult(
        DocumentDescriptor document,
        DocumentRange range,
        string text,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        Document = document;
        Range = range;
        Text = text;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    /// <summary>
    /// Descriptor for the resolved document whose line range was read.
    /// </summary>
    public DocumentDescriptor Document { get; }

    /// <summary>
    /// One-based line span actually returned after range validation.
    /// </summary>
    public DocumentRange Range { get; }

    /// <summary>
    /// Returned line-window text, fenced verbatim by the markdown renderer.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Non-fatal workspace load diagnostics emitted while opening the target.
    /// </summary>
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Represents the <c>document-symbols</c> command payload for declarations in one semantic document.
/// </summary>
public sealed class DocumentSymbolsResult
{
    public DocumentSymbolsResult(
        DocumentDescriptor document,
        IReadOnlyList<SymbolItem> symbols,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        Document = document;
        Symbols = symbols;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    /// <summary>
    /// Descriptor for the semantic document whose declarations were listed.
    /// </summary>
    public DocumentDescriptor Document { get; }

    /// <summary>
    /// Source-declared symbols found inside the selected document.
    /// </summary>
    public IReadOnlyList<SymbolItem> Symbols { get; }

    /// <summary>
    /// Non-fatal workspace load diagnostics emitted while opening the target.
    /// </summary>
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}
