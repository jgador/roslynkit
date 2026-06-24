using System.Text.Json.Serialization;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynKit;

/// <summary>
/// Workspace command payload.
/// </summary>
public sealed class WorkspaceResult
{
    public WorkspaceResult(
        string targetPath,
        string targetKind,
        IReadOnlyList<WorkspaceProject> projects,
        IReadOnlyList<WorkspaceDocument> documents,
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
    public IReadOnlyList<WorkspaceDocument> Documents { get; }

    [JsonPropertyName("workspaceDiagnostics")]
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Diagnostics command payload.
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
/// Symbols command payload.
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
/// Document symbols command payload.
/// </summary>
public sealed class DocumentSymbolsResult
{
    public DocumentSymbolsResult(
        string filePath,
        IReadOnlyList<SymbolItem> symbols,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        FilePath = filePath;
        Symbols = symbols;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    [JsonPropertyName("filePath")]
    public string FilePath { get; }

    [JsonPropertyName("symbols")]
    public IReadOnlyList<SymbolItem> Symbols { get; }

    [JsonPropertyName("workspaceDiagnostics")]
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Definition command payload.
/// </summary>
public sealed class DefinitionResult
{
    public DefinitionResult(
        string filePath,
        int line,
        int column,
        SymbolItem symbol,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        FilePath = filePath;
        Line = line;
        Column = column;
        Symbol = symbol;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    [JsonPropertyName("filePath")]
    public string FilePath { get; }

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
/// References command payload.
/// </summary>
public sealed class ReferencesResult
{
    public ReferencesResult(
        string filePath,
        int line,
        int column,
        SymbolItem symbol,
        int totalCount,
        int returnedCount,
        bool truncated,
        IReadOnlyList<ReferenceItem> locations,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        FilePath = filePath;
        Line = line;
        Column = column;
        Symbol = symbol;
        TotalCount = totalCount;
        ReturnedCount = returnedCount;
        Truncated = truncated;
        Locations = locations;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    [JsonPropertyName("filePath")]
    public string FilePath { get; }

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
/// Workspace project metadata.
/// </summary>
public sealed class WorkspaceProject
{
    public WorkspaceProject(
        string name,
        string? path,
        string language,
        int documentCount,
        IReadOnlyList<string> projectReferences)
    {
        Name = name;
        Path = path;
        Language = language;
        DocumentCount = documentCount;
        ProjectReferences = projectReferences;
    }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("path")]
    public string? Path { get; }

    [JsonPropertyName("language")]
    public string Language { get; }

    [JsonPropertyName("documentCount")]
    public int DocumentCount { get; }

    [JsonPropertyName("projectReferences")]
    public IReadOnlyList<string> ProjectReferences { get; }
}

/// <summary>
/// Workspace document metadata.
/// </summary>
public sealed class WorkspaceDocument
{
    public WorkspaceDocument(string projectName, string name, string? path)
    {
        ProjectName = projectName;
        Name = name;
        Path = path;
    }

    [JsonPropertyName("projectName")]
    public string ProjectName { get; }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("path")]
    public string? Path { get; }
}

/// <summary>
/// Workspace load diagnostic emitted by MSBuildWorkspace.
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
/// Diagnostic item emitted by the diagnostics command.
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
            diagnostic.Location.IsInSource ? global::System.IO.Path.GetFullPath(span.Path) : null,
            diagnostic.Location.IsInSource ? span.StartLinePosition.Line + 1 : null,
            diagnostic.Location.IsInSource ? span.StartLinePosition.Character + 1 : null,
            diagnostic.Location.IsInSource ? span.EndLinePosition.Line + 1 : null,
            diagnostic.Location.IsInSource ? span.EndLinePosition.Character + 1 : null);
    }
}

/// <summary>
/// Symbol item emitted by symbol-based commands.
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
        return FromSymbol(symbol, projectName, restrictDeclarationsToPath: null);
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
/// One-based source span for a symbol or reference.
/// </summary>
public sealed class SourceRange
{
    public SourceRange(string path, int line, int column, int endLine, int endColumn)
    {
        Path = path;
        Line = line;
        Column = column;
        EndLine = endLine;
        EndColumn = endColumn;
    }

    [JsonPropertyName("path")]
    public string Path { get; }

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
            global::System.IO.Path.GetFullPath(span.Path),
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1);
    }
}

/// <summary>
/// Reference item emitted by the references command.
/// </summary>
public sealed class ReferenceItem
{
    public ReferenceItem(
        string path,
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
    public string Path { get; }

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
/// Provides shared symbol display formats for deterministic RoslynKit output.
/// </summary>
public static class SymbolDisplayFormats
{
    public static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);
}
