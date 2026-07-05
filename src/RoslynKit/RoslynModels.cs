using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynKit;

/// <summary>
/// Names the RoslynKit document kinds surfaced in command output.
/// </summary>
public static class DocumentKindNames
{
    public const string Source = "source";
    public const string SourceGenerated = "sourceGenerated";
    public const string Additional = "additional";
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

    public string TargetPath { get; }

    public string TargetKind { get; }

    public IReadOnlyList<WorkspaceProject> Projects { get; }

    public IReadOnlyList<DocumentDescriptor> Documents { get; }

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

    public string TargetPath { get; }

    public int TotalCount { get; }

    public int ReturnedCount { get; }

    public bool Truncated { get; }

    public IReadOnlyList<DiagnosticItem> Diagnostics { get; }

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

    public string TargetPath { get; }

    public string Query { get; }

    public int TotalCount { get; }

    public int ReturnedCount { get; }

    public bool Truncated { get; }

    public IReadOnlyList<SymbolItem> Symbols { get; }

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

    public DocumentDescriptor Document { get; }

    public DocumentRange ResolvedRange { get; }

    public string Text { get; }

    public bool Truncated { get; }

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

    public DocumentDescriptor Document { get; }

    public DocumentRange Range { get; }

    public string Text { get; }

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

    public DocumentDescriptor Document { get; }

    public IReadOnlyList<SymbolItem> Symbols { get; }

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

    public DocumentDescriptor? Document { get; }

    public int? Line { get; }

    public int? Column { get; }

    public string? Selector { get; }

    public SymbolItem Symbol { get; }

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

    public DocumentDescriptor Document { get; }

    public int Line { get; }

    public int Column { get; }

    public SymbolItem Symbol { get; }

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

    public DocumentDescriptor Document { get; }

    public int Line { get; }

    public int Column { get; }

    public DocumentRange ResolvedRange { get; }

    public IReadOnlyList<string> Tags { get; }

    public IReadOnlyList<QuickInfoSectionItem> Sections { get; }

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

    public DocumentDescriptor? Document { get; }

    public int? Line { get; }

    public int? Column { get; }

    public string? Selector { get; }

    public SymbolItem Symbol { get; }

    public int TotalCount { get; }

    public int ReturnedCount { get; }

    public bool Truncated { get; }

    public IReadOnlyList<ReferenceItem> Locations { get; }

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

    public DocumentDescriptor? Document { get; }

    public int? Line { get; }

    public int? Column { get; }

    public string? Selector { get; }

    public SymbolItem Symbol { get; }

    public int TotalCount { get; }

    public int ReturnedCount { get; }

    public bool Truncated { get; }

    public IReadOnlyList<SymbolItem> Symbols { get; }

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

    public string TargetPath { get; }

    public string Selector { get; }

    public SymbolItem Symbol { get; }

    public IReadOnlyList<SymbolSourceDeclaration> Declarations { get; }

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

    public DocumentDescriptor Document { get; }

    public DocumentRange Range { get; }

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

    public DocumentDescriptor Document { get; }

    public int Line { get; }

    public int Column { get; }

    public DocumentRange ResolvedRange { get; }

    public int ActiveSignature { get; }

    public int ActiveParameter { get; }

    public IReadOnlyList<SignatureHelpSignatureItem> Signatures { get; }

    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Describes one document resolved from the loaded workspace, including project context and document key.
/// </summary>
public sealed class DocumentDescriptor
{
    public DocumentDescriptor(
        string documentKey,
        string projectName,
        string? projectPath,
        string? targetFramework,
        string documentKind,
        string name,
        string? path)
    {
        DocumentKey = documentKey;
        ProjectName = projectName;
        ProjectPath = projectPath;
        TargetFramework = targetFramework;
        DocumentKind = documentKind;
        Name = name;
        Path = path;
    }

    public string DocumentKey { get; }

    public string ProjectName { get; }

    public string? ProjectPath { get; }

    public string? TargetFramework { get; }

    public string DocumentKind { get; }

    public string Name { get; }

    public string? Path { get; }
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

    public int Line { get; }

    public int Column { get; }

    public int EndLine { get; }

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

    public string Name { get; }

    public string? Path { get; }

    public string? TargetFramework { get; }

    public string Language { get; }

    public int DocumentCount { get; }

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

    public string Kind { get; }

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

    public string ProjectName { get; }

    public string Id { get; }

    public string Severity { get; }

    public string Message { get; }

    public string? Path { get; }

    public int? Line { get; }

    public int? Column { get; }

    public int? EndLine { get; }

    public int? EndColumn { get; }

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
        string? symbolId)
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
    }

    public string ProjectName { get; }

    public string Name { get; }

    public string MetadataName { get; }

    public string DisplayName { get; }

    public string Kind { get; }

    public string Accessibility { get; }

    public bool IsStatic { get; }

    public string? ContainingType { get; }

    public string? ContainingNamespace { get; }

    public SourceRange? PrimaryLocation { get; }

    public IReadOnlyList<SourceRange> Declarations { get; }

    public string? SymbolId { get; }

    public static SymbolItem FromSymbol(ISymbol symbol, string projectName)
    {
        return FromSymbol(symbol, projectName, includeDeclaration: static location => location.IsInSource);
    }

    public static SymbolItem FromSymbol(ISymbol symbol, string projectName, string? restrictDeclarationsToPath)
    {
        return FromSymbol(
            symbol,
            projectName,
            location => restrictDeclarationsToPath is null || RoslynDocumentFilters.LocationMatchesPath(location, restrictDeclarationsToPath));
    }

    public static SymbolItem FromSymbol(ISymbol symbol, string projectName, ISet<string> restrictDeclarationsToPaths)
    {
        return FromSymbol(
            symbol,
            projectName,
            location => RoslynDocumentFilters.LocationMatchesAnyPath(location, restrictDeclarationsToPaths));
    }

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
            RoslynSymbolSearch.IsCodeSymbol(symbol) ? DocumentationCommentId.CreateDeclarationId(symbol) : null);
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

    public string? Path { get; }

    public int Line { get; }

    public int Column { get; }

    public int EndLine { get; }

    public int EndColumn { get; }

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

    public string? Path { get; }

    public int Line { get; }

    public int Column { get; }

    public int EndLine { get; }

    public int EndColumn { get; }

    public bool IsImplicit { get; }

    public string Definition { get; }

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

    public string Kind { get; }

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

    public string Label { get; }

    public string Documentation { get; }

    public bool IsVariadic { get; }

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

    public string Name { get; }

    public string Label { get; }

    public string Documentation { get; }

    public bool IsOptional { get; }
}

/// <summary>
/// Provides shared symbol display formats for deterministic RoslynKit output.
/// </summary>
public static class SymbolDisplayFormats
{
    public static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public static readonly SymbolDisplayFormat QualifiedMember = Qualified
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType);
}
