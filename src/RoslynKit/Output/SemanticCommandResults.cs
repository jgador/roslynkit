namespace RoslynKit;

/// <summary>
/// Represents the <c>definition</c> command payload for the symbol resolved from one document position.
/// </summary>
public sealed class DefinitionResult
{
    public DefinitionResult(
        DocumentDescriptor? document,
        int? line,
        int? column,
        string? selector,
        SymbolItem symbol,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        Document = document;
        Line = line;
        Column = column;
        Selector = selector;
        Symbol = symbol;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    /// <summary>
    /// Document descriptor for position-mode lookups, or <c>null</c> when a symbol selector was used.
    /// </summary>
    public DocumentDescriptor? Document { get; }

    /// <summary>
    /// One-based source line for position-mode lookups, or <c>null</c> for selector-mode lookups.
    /// </summary>
    public int? Line { get; }

    /// <summary>
    /// One-based source column for position-mode lookups, or <c>null</c> for selector-mode lookups.
    /// </summary>
    public int? Column { get; }

    /// <summary>
    /// Symbol selector used for selector-mode lookups, or <c>null</c> for position-mode lookups.
    /// </summary>
    public string? Selector { get; }

    /// <summary>
    /// Resolved definition symbol projected into RoslynKit output.
    /// </summary>
    public SymbolItem Symbol { get; }

    /// <summary>
    /// Non-fatal workspace load diagnostics emitted while opening the target.
    /// </summary>
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Represents the <c>type-definition</c> command payload for the type resolved from one document position.
/// </summary>
public sealed class TypeDefinitionResult
{
    public TypeDefinitionResult(
        DocumentDescriptor document,
        int line,
        int column,
        SymbolItem symbol,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        Document = document;
        Line = line;
        Column = column;
        Symbol = symbol;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    /// <summary>
    /// Descriptor for the document containing the original position lookup.
    /// </summary>
    public DocumentDescriptor Document { get; }

    /// <summary>
    /// One-based source line used to resolve the expression or symbol type.
    /// </summary>
    public int Line { get; }

    /// <summary>
    /// One-based source column used to resolve the expression or symbol type.
    /// </summary>
    public int Column { get; }

    /// <summary>
    /// Type symbol resolved from the selected source position.
    /// </summary>
    public SymbolItem Symbol { get; }

    /// <summary>
    /// Non-fatal workspace load diagnostics emitted while opening the target.
    /// </summary>
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Represents the <c>quick-info</c> command payload with the resolved span, tags, and formatted sections at one document position.
/// </summary>
public sealed class QuickInfoResult
{
    public QuickInfoResult(
        DocumentDescriptor document,
        int line,
        int column,
        DocumentRange resolvedRange,
        IReadOnlyList<string> tags,
        IReadOnlyList<QuickInfoSectionItem> sections,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        Document = document;
        Line = line;
        Column = column;
        ResolvedRange = resolvedRange;
        Tags = tags;
        Sections = sections;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    /// <summary>
    /// Descriptor for the document containing the quick-info position.
    /// </summary>
    public DocumentDescriptor Document { get; }

    /// <summary>
    /// One-based source line used for the quick-info request.
    /// </summary>
    public int Line { get; }

    /// <summary>
    /// One-based source column used for the quick-info request.
    /// </summary>
    public int Column { get; }

    /// <summary>
    /// One-based span Roslyn associated with the returned quick-info item.
    /// </summary>
    public DocumentRange ResolvedRange { get; }

    /// <summary>
    /// Roslyn quick-info tags that classify the returned item.
    /// </summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>
    /// Formatted quick-info sections such as description and documentation.
    /// </summary>
    public IReadOnlyList<QuickInfoSectionItem> Sections { get; }

    /// <summary>
    /// Non-fatal workspace load diagnostics emitted while opening the target.
    /// </summary>
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Represents the <c>references</c> command payload for source references to the symbol at one document position.
/// </summary>
public sealed class ReferencesResult
{
    public ReferencesResult(
        DocumentDescriptor? document,
        int? line,
        int? column,
        string? selector,
        SymbolItem symbol,
        int totalCount,
        int returnedCount,
        bool truncated,
        IReadOnlyList<ReferenceItem> locations,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        Document = document;
        Line = line;
        Column = column;
        Selector = selector;
        Symbol = symbol;
        TotalCount = totalCount;
        ReturnedCount = returnedCount;
        Truncated = truncated;
        Locations = locations;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    /// <summary>
    /// Document descriptor for position-mode lookups, or <c>null</c> when a symbol selector was used.
    /// </summary>
    public DocumentDescriptor? Document { get; }

    /// <summary>
    /// One-based source line for position-mode lookups, or <c>null</c> for selector-mode lookups.
    /// </summary>
    public int? Line { get; }

    /// <summary>
    /// One-based source column for position-mode lookups, or <c>null</c> for selector-mode lookups.
    /// </summary>
    public int? Column { get; }

    /// <summary>
    /// Symbol selector used for selector-mode lookups, or <c>null</c> for position-mode lookups.
    /// </summary>
    public string? Selector { get; }

    /// <summary>
    /// Symbol whose references were searched.
    /// </summary>
    public SymbolItem Symbol { get; }

    /// <summary>
    /// Total matching reference count before <c>--max-results</c> limiting.
    /// </summary>
    public int TotalCount { get; }

    /// <summary>
    /// Number of reference locations included in this command payload.
    /// </summary>
    public int ReturnedCount { get; }

    /// <summary>
    /// Indicates whether reference locations were omitted because of the result limit.
    /// </summary>
    public bool Truncated { get; }

    /// <summary>
    /// Source reference locations projected into deterministic order.
    /// </summary>
    public IReadOnlyList<ReferenceItem> Locations { get; }

    /// <summary>
    /// Non-fatal workspace load diagnostics emitted while opening the target.
    /// </summary>
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Represents the <c>implementations</c> command payload for source implementations of the symbol at one document position.
/// </summary>
public sealed class ImplementationsResult
{
    public ImplementationsResult(
        DocumentDescriptor? document,
        int? line,
        int? column,
        string? selector,
        SymbolItem symbol,
        int totalCount,
        int returnedCount,
        bool truncated,
        IReadOnlyList<SymbolItem> symbols,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        Document = document;
        Line = line;
        Column = column;
        Selector = selector;
        Symbol = symbol;
        TotalCount = totalCount;
        ReturnedCount = returnedCount;
        Truncated = truncated;
        Symbols = symbols;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    /// <summary>
    /// Document descriptor for position-mode lookups, or <c>null</c> when a symbol selector was used.
    /// </summary>
    public DocumentDescriptor? Document { get; }

    /// <summary>
    /// One-based source line for position-mode lookups, or <c>null</c> for selector-mode lookups.
    /// </summary>
    public int? Line { get; }

    /// <summary>
    /// One-based source column for position-mode lookups, or <c>null</c> for selector-mode lookups.
    /// </summary>
    public int? Column { get; }

    /// <summary>
    /// Symbol selector used for selector-mode lookups, or <c>null</c> for position-mode lookups.
    /// </summary>
    public string? Selector { get; }

    /// <summary>
    /// Symbol whose implementations were searched.
    /// </summary>
    public SymbolItem Symbol { get; }

    /// <summary>
    /// Total matching implementation count before <c>--max-results</c> limiting.
    /// </summary>
    public int TotalCount { get; }

    /// <summary>
    /// Number of implementation symbols included in this command payload.
    /// </summary>
    public int ReturnedCount { get; }

    /// <summary>
    /// Indicates whether implementation symbols were omitted because of the result limit.
    /// </summary>
    public bool Truncated { get; }

    /// <summary>
    /// Implementation symbols projected into RoslynKit symbol output.
    /// </summary>
    public IReadOnlyList<SymbolItem> Symbols { get; }

    /// <summary>
    /// Non-fatal workspace load diagnostics emitted while opening the target.
    /// </summary>
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}
