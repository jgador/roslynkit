namespace RoslynKit;

/// <summary>
/// Represents the <c>symbol-source</c> command payload with the full declaration text for one resolved symbol.
/// </summary>
public sealed class SymbolSourceResult
{
    public SymbolSourceResult(
        string targetPath,
        string selector,
        SymbolItem symbol,
        IReadOnlyList<SymbolSourceDeclaration> declarations,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        TargetPath = targetPath;
        Selector = selector;
        Symbol = symbol;
        Declarations = declarations;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    /// <summary>
    /// Absolute path to the solution or project inspected for the selector.
    /// </summary>
    public string TargetPath { get; }

    /// <summary>
    /// Symbol selector used to resolve the declaration source.
    /// </summary>
    public string Selector { get; }

    /// <summary>
    /// Symbol resolved from the selector before declaration text extraction.
    /// </summary>
    public SymbolItem Symbol { get; }

    /// <summary>
    /// Declaration blocks returned for the resolved symbol, including partial declarations.
    /// </summary>
    public IReadOnlyList<SymbolSourceDeclaration> Declarations { get; }

    /// <summary>
    /// Non-fatal workspace load diagnostics emitted while opening the target.
    /// </summary>
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Represents one declaring syntax block surfaced by the <c>symbol-source</c> command.
/// </summary>
public sealed class SymbolSourceDeclaration
{
    public SymbolSourceDeclaration(DocumentDescriptor document, DocumentRange range, string text)
    {
        Document = document;
        Range = range;
        Text = text;
    }

    /// <summary>
    /// Descriptor for the document containing this declaration block.
    /// </summary>
    public DocumentDescriptor Document { get; }

    /// <summary>
    /// One-based span covering the returned declaration text.
    /// </summary>
    public DocumentRange Range { get; }

    /// <summary>
    /// Full declaration source text returned for the symbol.
    /// </summary>
    public string Text { get; }
}

/// <summary>
/// Represents the <c>signature-help</c> command payload for the call site at one document position.
/// </summary>
public sealed class SignatureHelpResult
{
    public SignatureHelpResult(
        DocumentDescriptor document,
        int line,
        int column,
        DocumentRange resolvedRange,
        int activeSignature,
        int activeParameter,
        IReadOnlyList<SignatureHelpSignatureItem> signatures,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        Document = document;
        Line = line;
        Column = column;
        ResolvedRange = resolvedRange;
        ActiveSignature = activeSignature;
        ActiveParameter = activeParameter;
        Signatures = signatures;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    /// <summary>
    /// Descriptor for the document containing the signature-help position.
    /// </summary>
    public DocumentDescriptor Document { get; }

    /// <summary>
    /// One-based source line used for the signature-help request.
    /// </summary>
    public int Line { get; }

    /// <summary>
    /// One-based source column used for the signature-help request.
    /// </summary>
    public int Column { get; }

    /// <summary>
    /// One-based argument-list span Roslyn associated with the signature-help result.
    /// </summary>
    public DocumentRange ResolvedRange { get; }

    /// <summary>
    /// Zero-based index of the selected signature in the returned signature list.
    /// </summary>
    public int ActiveSignature { get; }

    /// <summary>
    /// Zero-based index of the active parameter within the selected signature.
    /// </summary>
    public int ActiveParameter { get; }

    /// <summary>
    /// Callable signatures available at the requested source position.
    /// </summary>
    public IReadOnlyList<SignatureHelpSignatureItem> Signatures { get; }

    /// <summary>
    /// Non-fatal workspace load diagnostics emitted while opening the target.
    /// </summary>
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}
