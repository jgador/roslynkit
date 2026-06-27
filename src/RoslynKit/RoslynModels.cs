using System.Text.Json.Serialization;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynKit;

/// <summary>
/// Names the RoslynKit document kinds surfaced in JSON payloads.
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

    [JsonPropertyName("targetPath")]
    public string TargetPath { get; }

    [JsonPropertyName("targetKind")]
    public string TargetKind { get; }

    [JsonPropertyName("projects")]
    public IReadOnlyList<WorkspaceProject> Projects { get; }

    [JsonPropertyName("documents")]
    public IReadOnlyList<DocumentDescriptor> Documents { get; }

    [JsonPropertyName("workspaceDiagnostics")]
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

    [JsonPropertyName("targetPath")]
    public string TargetPath { get; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; }

    [JsonPropertyName("returnedCount")]
    public int ReturnedCount { get; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; }

    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<DiagnosticItem> Diagnostics { get; }

    [JsonPropertyName("workspaceDiagnostics")]
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

    [JsonPropertyName("targetPath")]
    public string TargetPath { get; }

    [JsonPropertyName("query")]
    public string Query { get; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; }

    [JsonPropertyName("returnedCount")]
    public int ReturnedCount { get; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; }

    [JsonPropertyName("symbols")]
    public IReadOnlyList<SymbolItem> Symbols { get; }

    [JsonPropertyName("workspaceDiagnostics")]
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Represents the <c>document-text</c> command payload for one resolved document range.
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

    [JsonPropertyName("document")]
    public DocumentDescriptor Document { get; }

    [JsonPropertyName("resolvedRange")]
    public DocumentRange ResolvedRange { get; }

    [JsonPropertyName("text")]
    public string Text { get; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; }

    [JsonPropertyName("workspaceDiagnostics")]
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

    [JsonPropertyName("document")]
    public DocumentDescriptor Document { get; }

    [JsonPropertyName("symbols")]
    public IReadOnlyList<SymbolItem> Symbols { get; }

    [JsonPropertyName("workspaceDiagnostics")]
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Represents the <c>definition</c> command payload for the symbol resolved from one document position.
/// </summary>
public sealed class DefinitionResult
{
    public DefinitionResult(
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

    [JsonPropertyName("document")]
    public DocumentDescriptor Document { get; }

    [JsonPropertyName("line")]
    public int Line { get; }

    [JsonPropertyName("column")]
    public int Column { get; }

    [JsonPropertyName("symbol")]
    public SymbolItem Symbol { get; }

    [JsonPropertyName("workspaceDiagnostics")]
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

    [JsonPropertyName("document")]
    public DocumentDescriptor Document { get; }

    [JsonPropertyName("line")]
    public int Line { get; }

    [JsonPropertyName("column")]
    public int Column { get; }

    [JsonPropertyName("symbol")]
    public SymbolItem Symbol { get; }

    [JsonPropertyName("workspaceDiagnostics")]
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

    [JsonPropertyName("document")]
    public DocumentDescriptor Document { get; }

    [JsonPropertyName("line")]
    public int Line { get; }

    [JsonPropertyName("column")]
    public int Column { get; }

    [JsonPropertyName("resolvedRange")]
    public DocumentRange ResolvedRange { get; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; }

    [JsonPropertyName("sections")]
    public IReadOnlyList<QuickInfoSectionItem> Sections { get; }

    [JsonPropertyName("workspaceDiagnostics")]
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Represents the <c>references</c> command payload for source references to the symbol at one document position.
/// </summary>
public sealed class ReferencesResult
{
    public ReferencesResult(
        DocumentDescriptor document,
        int line,
        int column,
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
        Symbol = symbol;
        TotalCount = totalCount;
        ReturnedCount = returnedCount;
        Truncated = truncated;
        Locations = locations;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    [JsonPropertyName("document")]
    public DocumentDescriptor Document { get; }

    [JsonPropertyName("line")]
    public int Line { get; }

    [JsonPropertyName("column")]
    public int Column { get; }

    [JsonPropertyName("symbol")]
    public SymbolItem Symbol { get; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; }

    [JsonPropertyName("returnedCount")]
    public int ReturnedCount { get; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; }

    [JsonPropertyName("locations")]
    public IReadOnlyList<ReferenceItem> Locations { get; }

    [JsonPropertyName("workspaceDiagnostics")]
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Represents the <c>implementations</c> command payload for source implementations of the symbol at one document position.
/// </summary>
public sealed class ImplementationsResult
{
    public ImplementationsResult(
        DocumentDescriptor document,
        int line,
        int column,
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
        Symbol = symbol;
        TotalCount = totalCount;
        ReturnedCount = returnedCount;
        Truncated = truncated;
        Symbols = symbols;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    [JsonPropertyName("document")]
    public DocumentDescriptor Document { get; }

    [JsonPropertyName("line")]
    public int Line { get; }

    [JsonPropertyName("column")]
    public int Column { get; }

    [JsonPropertyName("symbol")]
    public SymbolItem Symbol { get; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; }

    [JsonPropertyName("returnedCount")]
    public int ReturnedCount { get; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; }

    [JsonPropertyName("symbols")]
    public IReadOnlyList<SymbolItem> Symbols { get; }

    [JsonPropertyName("workspaceDiagnostics")]
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
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

    [JsonPropertyName("document")]
    public DocumentDescriptor Document { get; }

    [JsonPropertyName("line")]
    public int Line { get; }

    [JsonPropertyName("column")]
    public int Column { get; }

    [JsonPropertyName("resolvedRange")]
    public DocumentRange ResolvedRange { get; }

    [JsonPropertyName("activeSignature")]
    public int ActiveSignature { get; }

    [JsonPropertyName("activeParameter")]
    public int ActiveParameter { get; }

    [JsonPropertyName("signatures")]
    public IReadOnlyList<SignatureHelpSignatureItem> Signatures { get; }

    [JsonPropertyName("workspaceDiagnostics")]
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

    [JsonPropertyName("documentKey")]
    public string DocumentKey { get; }

    [JsonPropertyName("projectName")]
    public string ProjectName { get; }

    [JsonPropertyName("projectPath")]
    public string? ProjectPath { get; }

    [JsonPropertyName("targetFramework")]
    public string? TargetFramework { get; }

    [JsonPropertyName("documentKind")]
    public string DocumentKind { get; }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("path")]
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

    [JsonPropertyName("line")]
    public int Line { get; }

    [JsonPropertyName("column")]
    public int Column { get; }

    [JsonPropertyName("endLine")]
    public int EndLine { get; }

    [JsonPropertyName("endColumn")]
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

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("path")]
    public string? Path { get; }

    [JsonPropertyName("targetFramework")]
    public string? TargetFramework { get; }

    [JsonPropertyName("language")]
    public string Language { get; }

    [JsonPropertyName("documentCount")]
    public int DocumentCount { get; }

    [JsonPropertyName("projectReferences")]
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

    [JsonPropertyName("kind")]
    public string Kind { get; }

    [JsonPropertyName("message")]
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

    [JsonPropertyName("projectName")]
    public string ProjectName { get; }

    [JsonPropertyName("id")]
    public string Id { get; }

    [JsonPropertyName("severity")]
    public string Severity { get; }

    [JsonPropertyName("message")]
    public string Message { get; }

    [JsonPropertyName("path")]
    public string? Path { get; }

    [JsonPropertyName("line")]
    public int? Line { get; }

    [JsonPropertyName("column")]
    public int? Column { get; }

    [JsonPropertyName("endLine")]
    public int? EndLine { get; }

    [JsonPropertyName("endColumn")]
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
        IReadOnlyList<SourceRange> declarations)
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
    }

    [JsonPropertyName("projectName")]
    public string ProjectName { get; }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("metadataName")]
    public string MetadataName { get; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; }

    [JsonPropertyName("kind")]
    public string Kind { get; }

    [JsonPropertyName("accessibility")]
    public string Accessibility { get; }

    [JsonPropertyName("isStatic")]
    public bool IsStatic { get; }

    [JsonPropertyName("containingType")]
    public string? ContainingType { get; }

    [JsonPropertyName("containingNamespace")]
    public string? ContainingNamespace { get; }

    [JsonPropertyName("primaryLocation")]
    public SourceRange? PrimaryLocation { get; }

    [JsonPropertyName("declarations")]
    public IReadOnlyList<SourceRange> Declarations { get; }

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
            declarations);
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

    [JsonPropertyName("path")]
    public string? Path { get; }

    [JsonPropertyName("line")]
    public int Line { get; }

    [JsonPropertyName("column")]
    public int Column { get; }

    [JsonPropertyName("endLine")]
    public int EndLine { get; }

    [JsonPropertyName("endColumn")]
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

    [JsonPropertyName("path")]
    public string? Path { get; }

    [JsonPropertyName("line")]
    public int Line { get; }

    [JsonPropertyName("column")]
    public int Column { get; }

    [JsonPropertyName("endLine")]
    public int EndLine { get; }

    [JsonPropertyName("endColumn")]
    public int EndColumn { get; }

    [JsonPropertyName("isImplicit")]
    public bool IsImplicit { get; }

    [JsonPropertyName("definition")]
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

    [JsonPropertyName("kind")]
    public string Kind { get; }

    [JsonPropertyName("text")]
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

    [JsonPropertyName("label")]
    public string Label { get; }

    [JsonPropertyName("documentation")]
    public string Documentation { get; }

    [JsonPropertyName("isVariadic")]
    public bool IsVariadic { get; }

    [JsonPropertyName("parameters")]
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

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("label")]
    public string Label { get; }

    [JsonPropertyName("documentation")]
    public string Documentation { get; }

    [JsonPropertyName("isOptional")]
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
}
