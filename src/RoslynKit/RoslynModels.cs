using System.Text;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

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

/// <summary>
/// Describes one document resolved from the loaded workspace, including project and path context.
/// </summary>
public sealed class DocumentDescriptor
{
    public DocumentDescriptor(
        string projectName,
        string? projectPath,
        string? targetFramework,
        string documentKind,
        string name,
        string? path,
        string? displayProjectPath = null,
        string? displayPath = null)
        : this(
            documentKey: string.Empty,
            projectName,
            projectPath,
            targetFramework,
            documentKind,
            name,
            path,
            displayProjectPath,
            displayPath)
    {
    }

    internal DocumentDescriptor(
        string documentKey,
        string projectName,
        string? projectPath,
        string? targetFramework,
        string documentKind,
        string name,
        string? path,
        string? displayProjectPath = null,
        string? displayPath = null)
    {
        DocumentKey = documentKey;
        ProjectName = projectName;
        ProjectPath = projectPath;
        TargetFramework = targetFramework;
        DocumentKind = documentKind;
        Name = name;
        Path = path;
        DisplayProjectPath = displayProjectPath ?? projectPath;
        DisplayPath = displayPath ?? path;
    }

    /// <summary>
    /// Private stable key used only for deterministic internal ordering and de-duplication.
    /// </summary>
    internal string DocumentKey { get; }

    /// <summary>
    /// Name of the project that owns the document.
    /// </summary>
    public string ProjectName { get; }

    /// <summary>
    /// Absolute path to the owning project file, when Roslyn exposes one.
    /// </summary>
    public string? ProjectPath { get; }

    /// <summary>
    /// User-facing owning project path, relative to the loaded root when possible.
    /// </summary>
    public string? DisplayProjectPath { get; }

    /// <summary>
    /// Target framework label for the project context, when the load supplied one.
    /// </summary>
    public string? TargetFramework { get; }

    /// <summary>
    /// RoslynKit document-kind name used to route semantic and text commands.
    /// </summary>
    public string DocumentKind { get; }

    /// <summary>
    /// Roslyn document name as reported by the loaded workspace.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Absolute file path for path-backed documents, or <c>null</c> for generated documents without a path.
    /// </summary>
    public string? Path { get; }

    /// <summary>
    /// User-facing document path, relative to the loaded root when possible.
    /// </summary>
    public string? DisplayPath { get; }
}

/// <summary>
/// Represents a one-based span inside a resolved document.
/// </summary>
public sealed class DocumentRange
{
    public DocumentRange(int line, int column, int endLine, int endColumn)
    {
        Line = line;
        Column = column;
        EndLine = endLine;
        EndColumn = endColumn;
    }

    /// <summary>
    /// One-based starting line of the span.
    /// </summary>
    public int Line { get; }

    /// <summary>
    /// One-based starting column of the span.
    /// </summary>
    public int Column { get; }

    /// <summary>
    /// One-based ending line of the span.
    /// </summary>
    public int EndLine { get; }

    /// <summary>
    /// One-based ending column of the span.
    /// </summary>
    public int EndColumn { get; }
}

/// <summary>
/// Represents one loaded project entry in the <c>workspace</c> command payload.
/// </summary>
public sealed class WorkspaceProject
{
    public WorkspaceProject(
        string name,
        string? path,
        string? targetFramework,
        string language,
        int documentCount,
        IReadOnlyList<string> projectReferences)
    {
        Name = name;
        Path = path;
        TargetFramework = targetFramework;
        Language = language;
        DocumentCount = documentCount;
        ProjectReferences = projectReferences;
    }

    /// <summary>
    /// Project display name from the loaded Roslyn workspace.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Absolute project file path, when Roslyn exposes one.
    /// </summary>
    public string? Path { get; }

    /// <summary>
    /// Target framework label associated with this project context, when available.
    /// </summary>
    public string? TargetFramework { get; }

    /// <summary>
    /// Roslyn language name for the project.
    /// </summary>
    public string Language { get; }

    /// <summary>
    /// Count of command-addressable documents owned by this project.
    /// </summary>
    public int DocumentCount { get; }

    /// <summary>
    /// Project references listed by project name in deterministic order.
    /// </summary>
    public IReadOnlyList<string> ProjectReferences { get; }
}

/// <summary>
/// Represents one <c>MSBuildWorkspace</c> diagnostic captured while loading the target.
/// </summary>
public sealed class WorkspaceLoadDiagnostic
{
    public WorkspaceLoadDiagnostic(string kind, string message)
    {
        Kind = kind;
        Message = message;
    }

    /// <summary>
    /// Workspace diagnostic severity or category reported by Roslyn.
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// Workspace diagnostic message emitted during target load.
    /// </summary>
    public string Message { get; }
}

/// <summary>
/// Represents one compiler diagnostic surfaced by the <c>diagnostics</c> command.
/// </summary>
public sealed class DiagnosticItem
{
    public DiagnosticItem(
        string projectName,
        string id,
        string severity,
        string message,
        string? path,
        int? line,
        int? column,
        int? endLine,
        int? endColumn)
    {
        ProjectName = projectName;
        Id = id;
        Severity = severity;
        Message = message;
        Path = path;
        Line = line;
        Column = column;
        EndLine = endLine;
        EndColumn = endColumn;
    }

    /// <summary>
    /// Project name associated with the diagnostic.
    /// </summary>
    public string ProjectName { get; }

    /// <summary>
    /// Compiler diagnostic identifier, such as <c>CS1002</c>.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Roslyn diagnostic severity projected as text.
    /// </summary>
    public string Severity { get; }

    /// <summary>
    /// Diagnostic message text from Roslyn.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Absolute source path for source diagnostics, or <c>null</c> for non-source diagnostics.
    /// </summary>
    public string? Path { get; }

    /// <summary>
    /// One-based starting line for source diagnostics.
    /// </summary>
    public int? Line { get; }

    /// <summary>
    /// One-based starting column for source diagnostics.
    /// </summary>
    public int? Column { get; }

    /// <summary>
    /// One-based ending line for source diagnostics.
    /// </summary>
    public int? EndLine { get; }

    /// <summary>
    /// One-based ending column for source diagnostics.
    /// </summary>
    public int? EndColumn { get; }

    /// <summary>
    /// Converts a Roslyn diagnostic into the command-output shape with normalized source coordinates.
    /// </summary>
    public static DiagnosticItem FromDiagnostic(string projectName, Diagnostic diagnostic)
    {
        var span = diagnostic.Location.IsInSource ? diagnostic.Location.GetLineSpan() : default;
        return new DiagnosticItem(
            projectName,
            diagnostic.Id,
            diagnostic.Severity.ToString(),
            diagnostic.GetMessage(),
            diagnostic.Location.IsInSource ? NormalizePath(span.Path) : null,
            diagnostic.Location.IsInSource ? span.StartLinePosition.Line + 1 : null,
            diagnostic.Location.IsInSource ? span.StartLinePosition.Character + 1 : null,
            diagnostic.Location.IsInSource ? span.EndLinePosition.Line + 1 : null,
            diagnostic.Location.IsInSource ? span.EndLinePosition.Character + 1 : null);
    }

    private static string? NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : global::System.IO.Path.GetFullPath(path);
    }
}

/// <summary>
/// Represents one source-declared symbol surfaced by symbol-based RoslynKit commands.
/// </summary>
public sealed class SymbolItem
{
    public SymbolItem(
        string projectName,
        string name,
        string metadataName,
        string displayName,
        string kind,
        string accessibility,
        bool isStatic,
        string? containingType,
        string? containingNamespace,
        SourceRange? primaryLocation,
        IReadOnlyList<SourceRange> declarations,
        string? symbolId,
        string? documentation = null)
    {
        ProjectName = projectName;
        Name = name;
        MetadataName = metadataName;
        DisplayName = displayName;
        Kind = kind;
        Accessibility = accessibility;
        IsStatic = isStatic;
        ContainingType = containingType;
        ContainingNamespace = containingNamespace;
        PrimaryLocation = primaryLocation;
        Declarations = declarations;
        SymbolId = symbolId;
        Documentation = string.IsNullOrWhiteSpace(documentation) ? null : documentation;
    }

    /// <summary>
    /// Project name used to scope the symbol in command output.
    /// </summary>
    public string ProjectName { get; }

    /// <summary>
    /// Simple symbol name reported by Roslyn.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Metadata name used by Roslyn for overloads, generics, and emitted identity.
    /// </summary>
    public string MetadataName { get; }

    /// <summary>
    /// Fully qualified display name rendered with RoslynKit's deterministic symbol format.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Roslyn symbol kind projected as text.
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// Declared accessibility projected as text.
    /// </summary>
    public string Accessibility { get; }

    /// <summary>
    /// Indicates whether the Roslyn symbol is static.
    /// </summary>
    public bool IsStatic { get; }

    /// <summary>
    /// Fully qualified containing type name for member symbols.
    /// </summary>
    public string? ContainingType { get; }

    /// <summary>
    /// Fully qualified containing namespace name, excluding the global namespace.
    /// </summary>
    public string? ContainingNamespace { get; }

    /// <summary>
    /// First declaration location after RoslynKit filtering and deterministic ordering.
    /// </summary>
    public SourceRange? PrimaryLocation { get; }

    /// <summary>
    /// Declaration locations that remain after command-specific source filtering.
    /// </summary>
    public IReadOnlyList<SourceRange> Declarations { get; }

    /// <summary>
    /// Documentation-comment ID that can be reused as a symbol selector when Roslyn can create one.
    /// </summary>
    public string? SymbolId { get; }

    /// <summary>
    /// Plain-text summary documentation extracted from the Roslyn symbol's XML documentation comment.
    /// </summary>
    public string? Documentation { get; }

    /// <summary>
    /// Converts a Roslyn symbol into command-output metadata with all source declarations included.
    /// </summary>
    public static SymbolItem FromSymbol(ISymbol symbol, string projectName)
    {
        return FromSymbol(symbol, projectName, includeDeclaration: static location => location.IsInSource);
    }

    /// <summary>
    /// Converts a Roslyn symbol while keeping declaration locations only from one normalized path.
    /// </summary>
    public static SymbolItem FromSymbol(ISymbol symbol, string projectName, string? restrictDeclarationsToPath)
    {
        return FromSymbol(
            symbol,
            projectName,
            location => restrictDeclarationsToPath is null || RoslynDocumentFilters.LocationMatchesPath(location, restrictDeclarationsToPath));
    }

    /// <summary>
    /// Converts a Roslyn symbol while keeping declaration locations only from a project or solution source path set.
    /// </summary>
    public static SymbolItem FromSymbol(ISymbol symbol, string projectName, ISet<string> restrictDeclarationsToPaths)
    {
        return FromSymbol(
            symbol,
            projectName,
            location => RoslynDocumentFilters.LocationMatchesAnyPath(location, restrictDeclarationsToPaths));
    }

    /// <summary>
    /// Converts a Roslyn symbol while keeping declaration locations only from one syntax tree.
    /// </summary>
    public static SymbolItem FromSymbol(ISymbol symbol, string projectName, SyntaxTree restrictDeclarationsToSyntaxTree)
    {
        return FromSymbol(
            symbol,
            projectName,
            location => location.IsInSource && location.SourceTree == restrictDeclarationsToSyntaxTree);
    }

    private static SymbolItem FromSymbol(ISymbol symbol, string projectName, Func<Location, bool> includeDeclaration)
    {
        var declarations = symbol.Locations
            .Where(location => location.IsInSource)
            .Where(includeDeclaration)
            .Select(SourceRange.FromLocation)
            .OrderBy(location => location.Path, StringComparer.Ordinal)
            .ThenBy(location => location.Line)
            .ThenBy(location => location.Column)
            .ToArray();

        return new SymbolItem(
            projectName,
            symbol.Name,
            symbol.MetadataName,
            symbol.ToDisplayString(SymbolDisplayFormats.Qualified),
            symbol.Kind.ToString(),
            symbol.DeclaredAccessibility.ToString(),
            symbol.IsStatic,
            symbol.ContainingType?.ToDisplayString(SymbolDisplayFormats.Qualified),
            symbol.ContainingNamespace is { IsGlobalNamespace: false } ? symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormats.Qualified) : null,
            declarations.FirstOrDefault(),
            declarations,
            RoslynSymbolSearch.IsCodeSymbol(symbol) ? DocumentationCommentId.CreateDeclarationId(symbol) : null,
            GetSummaryDocumentation(symbol));
    }

    private static string? GetSummaryDocumentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var document = XDocument.Parse(xml);
            var summary = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "summary");
            return summary is null ? null : NormalizeDocumentationText(RenderDocumentationNodes(summary.Nodes()));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string RenderDocumentationNodes(IEnumerable<XNode> nodes)
    {
        var builder = new StringBuilder();
        foreach (var node in nodes)
        {
            AppendDocumentationNode(builder, node);
        }

        return builder.ToString();
    }

    private static void AppendDocumentationNode(StringBuilder builder, XNode node)
    {
        switch (node)
        {
            case XCData cdata:
                builder.Append(cdata.Value);
                break;

            case XText text:
                builder.Append(text.Value);
                break;

            case XElement element:
                AppendDocumentationElement(builder, element);
                break;
        }
    }

    private static void AppendDocumentationElement(StringBuilder builder, XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "see":
            case "seealso":
                builder.Append(SimplifyDocumentationReference(
                    (string?)element.Attribute("cref")
                    ?? (string?)element.Attribute("langword")
                    ?? (string?)element.Attribute("href")
                    ?? element.Value));
                break;

            case "paramref":
            case "typeparamref":
                builder.Append((string?)element.Attribute("name") ?? element.Value);
                break;

            default:
                foreach (var child in element.Nodes())
                {
                    AppendDocumentationNode(builder, child);
                }

                break;
        }
    }

    private static string SimplifyDocumentationReference(string value)
    {
        if (value.Length > 2 && value[1] == ':' && value[0] is 'T' or 'M' or 'P' or 'F' or 'E' or 'N')
        {
            return value[2..];
        }

        return value.StartsWith("!:", StringComparison.Ordinal) ? value[2..] : value;
    }

    private static string? NormalizeDocumentationText(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text)
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

        return builder.Length == 0 ? null : builder.ToString();
    }
}

/// <summary>
/// Represents a one-based source span for a declaration, definition, or reference location.
/// </summary>
public sealed class SourceRange
{
    public SourceRange(string? path, int line, int column, int endLine, int endColumn)
    {
        Path = path;
        Line = line;
        Column = column;
        EndLine = endLine;
        EndColumn = endColumn;
    }

    /// <summary>
    /// Absolute source path for the location, when available.
    /// </summary>
    public string? Path { get; }

    /// <summary>
    /// One-based starting line of the source span.
    /// </summary>
    public int Line { get; }

    /// <summary>
    /// One-based starting column of the source span.
    /// </summary>
    public int Column { get; }

    /// <summary>
    /// One-based ending line of the source span.
    /// </summary>
    public int EndLine { get; }

    /// <summary>
    /// One-based ending column of the source span.
    /// </summary>
    public int EndColumn { get; }

    /// <summary>
    /// Converts a Roslyn source location into normalized output coordinates.
    /// </summary>
    public static SourceRange FromLocation(Location location)
    {
        var span = location.GetLineSpan();
        return new SourceRange(
            NormalizePath(span.Path, location.SourceTree?.FilePath),
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1);
    }

    private static string? NormalizePath(string? path, string? fallbackPath)
    {
        var resolvedPath = !string.IsNullOrWhiteSpace(path)
            ? path
            : fallbackPath;

        return string.IsNullOrWhiteSpace(resolvedPath)
            ? null
            : global::System.IO.Path.GetFullPath(resolvedPath);
    }
}

/// <summary>
/// Represents one source reference location returned by the <c>references</c> command.
/// </summary>
public sealed class ReferenceItem
{
    public ReferenceItem(
        string? path,
        int line,
        int column,
        int endLine,
        int endColumn,
        bool isImplicit,
        string definition)
    {
        Path = path;
        Line = line;
        Column = column;
        EndLine = endLine;
        EndColumn = endColumn;
        IsImplicit = isImplicit;
        Definition = definition;
    }

    /// <summary>
    /// Absolute source path for the reference location, when available.
    /// </summary>
    public string? Path { get; }

    /// <summary>
    /// One-based starting line for the reference span.
    /// </summary>
    public int Line { get; }

    /// <summary>
    /// One-based starting column for the reference span.
    /// </summary>
    public int Column { get; }

    /// <summary>
    /// One-based ending line for the reference span.
    /// </summary>
    public int EndLine { get; }

    /// <summary>
    /// One-based ending column for the reference span.
    /// </summary>
    public int EndColumn { get; }

    /// <summary>
    /// Indicates whether Roslyn reported the reference as implicit rather than explicit source text.
    /// </summary>
    public bool IsImplicit { get; }

    /// <summary>
    /// Fully qualified display name of the referenced definition symbol.
    /// </summary>
    public string Definition { get; }

    /// <summary>
    /// Converts a Roslyn reference location into deterministic command-output coordinates.
    /// </summary>
    public static ReferenceItem FromReferenceLocation(ISymbol definition, ReferenceLocation referenceLocation)
    {
        var location = SourceRange.FromLocation(referenceLocation.Location);
        return new ReferenceItem(
            location.Path,
            location.Line,
            location.Column,
            location.EndLine,
            location.EndColumn,
            referenceLocation.IsImplicit,
            definition.ToDisplayString(SymbolDisplayFormats.Qualified));
    }
}

/// <summary>
/// Represents one formatted section returned in a <c>quick-info</c> result.
/// </summary>
public sealed class QuickInfoSectionItem
{
    public QuickInfoSectionItem(string kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    /// <summary>
    /// Quick-info section kind, such as description or documentation.
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// Formatted section text returned by Roslyn quick-info.
    /// </summary>
    public string Text { get; }
}

/// <summary>
/// Represents one callable signature returned by <c>signature-help</c>.
/// </summary>
public sealed class SignatureHelpSignatureItem
{
    public SignatureHelpSignatureItem(
        string label,
        string documentation,
        bool isVariadic,
        IReadOnlyList<SignatureHelpParameterItem> parameters)
    {
        Label = label;
        Documentation = documentation;
        IsVariadic = isVariadic;
        Parameters = parameters;
    }

    /// <summary>
    /// Display label for the callable signature.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Documentation text associated with the signature, when Roslyn provides it.
    /// </summary>
    public string Documentation { get; }

    /// <summary>
    /// Indicates whether the signature accepts a variadic or <c>params</c> argument list.
    /// </summary>
    public bool IsVariadic { get; }

    /// <summary>
    /// Parameter entries rendered under this signature.
    /// </summary>
    public IReadOnlyList<SignatureHelpParameterItem> Parameters { get; }
}

/// <summary>
/// Represents one parameter entry inside a <c>signature-help</c> signature.
/// </summary>
public sealed class SignatureHelpParameterItem
{
    public SignatureHelpParameterItem(
        string name,
        string label,
        string documentation,
        bool isOptional)
    {
        Name = name;
        Label = label;
        Documentation = documentation;
        IsOptional = isOptional;
    }

    /// <summary>
    /// Parameter name reported by Roslyn.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Display label for the parameter, including type and modifiers when available.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Documentation text associated with the parameter, when Roslyn provides it.
    /// </summary>
    public string Documentation { get; }

    /// <summary>
    /// Indicates whether callers may omit this parameter.
    /// </summary>
    public bool IsOptional { get; }
}

/// <summary>
/// Provides shared symbol display formats for deterministic RoslynKit output.
/// </summary>
public static class SymbolDisplayFormats
{
    /// <summary>
    /// Fully qualified format used for stable symbol names without global namespace prefixes.
    /// </summary>
    public static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>
    /// Fully qualified member format that includes the containing type for member identities.
    /// </summary>
    public static readonly SymbolDisplayFormat QualifiedMember = Qualified
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType);
}
