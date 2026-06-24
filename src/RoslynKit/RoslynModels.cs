using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynKit;

public sealed record WorkspaceResult(
    string TargetPath,
    string TargetKind,
    IReadOnlyList<ProjectInfoDto> Projects,
    IReadOnlyList<DocumentInfoDto> Documents,
    IReadOnlyList<WorkspaceDiagnosticDto> WorkspaceDiagnostics);

public sealed record DiagnosticsResult(
    string TargetPath,
    int TotalCount,
    int ReturnedCount,
    bool Truncated,
    IReadOnlyList<DiagnosticDto> Diagnostics,
    IReadOnlyList<WorkspaceDiagnosticDto> WorkspaceDiagnostics);

public sealed record SymbolsResult(
    string TargetPath,
    string Query,
    int TotalCount,
    int ReturnedCount,
    bool Truncated,
    IReadOnlyList<SymbolDto> Symbols,
    IReadOnlyList<WorkspaceDiagnosticDto> WorkspaceDiagnostics);

public sealed record DocumentSymbolsResult(
    string FilePath,
    IReadOnlyList<SymbolDto> Symbols,
    IReadOnlyList<WorkspaceDiagnosticDto> WorkspaceDiagnostics);

public sealed record DefinitionResult(
    string FilePath,
    int Line,
    int Column,
    SymbolDto Symbol,
    IReadOnlyList<WorkspaceDiagnosticDto> WorkspaceDiagnostics);

public sealed record ReferencesResult(
    string FilePath,
    int Line,
    int Column,
    SymbolDto Symbol,
    int TotalCount,
    int ReturnedCount,
    bool Truncated,
    IReadOnlyList<ReferenceLocationDto> Locations,
    IReadOnlyList<WorkspaceDiagnosticDto> WorkspaceDiagnostics);

public sealed record ProjectInfoDto(
    string Name,
    string? Path,
    string Language,
    int DocumentCount,
    IReadOnlyList<string> ProjectReferences);

public sealed record DocumentInfoDto(string ProjectName, string Name, string? Path);

public sealed record WorkspaceDiagnosticDto(string Kind, string Message);

public sealed record DiagnosticDto(
    string ProjectName,
    string Id,
    string Severity,
    string Message,
    string? Path,
    int? Line,
    int? Column,
    int? EndLine,
    int? EndColumn)
{
    public static DiagnosticDto FromDiagnostic(string projectName, Diagnostic diagnostic)
    {
        var span = diagnostic.Location.IsInSource ? diagnostic.Location.GetLineSpan() : default;
        return new DiagnosticDto(
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

public sealed record SymbolDto(
    string ProjectName,
    string Name,
    string MetadataName,
    string DisplayName,
    string Kind,
    string Accessibility,
    bool IsStatic,
    string? ContainingType,
    string? ContainingNamespace,
    SourceLocationDto? PrimaryLocation,
    IReadOnlyList<SourceLocationDto> Declarations)
{
    public static SymbolDto FromSymbol(ISymbol symbol, string projectName)
    {
        return FromSymbol(symbol, projectName, restrictDeclarationsToPath: null);
    }

    public static SymbolDto FromSymbol(ISymbol symbol, string projectName, string? restrictDeclarationsToPath)
    {
        return FromSymbol(
            symbol,
            projectName,
            location => restrictDeclarationsToPath is null || RoslynDocumentFilters.LocationMatchesPath(location, restrictDeclarationsToPath));
    }

    public static SymbolDto FromSymbol(ISymbol symbol, string projectName, ISet<string> restrictDeclarationsToPaths)
    {
        return FromSymbol(
            symbol,
            projectName,
            location => RoslynDocumentFilters.LocationMatchesAnyPath(location, restrictDeclarationsToPaths));
    }

    private static SymbolDto FromSymbol(ISymbol symbol, string projectName, Func<Location, bool> includeDeclaration)
    {
        var declarations = symbol.Locations
            .Where(location => location.IsInSource)
            .Where(includeDeclaration)
            .Select(SourceLocationDto.FromLocation)
            .OrderBy(location => location.Path, StringComparer.Ordinal)
            .ThenBy(location => location.Line)
            .ThenBy(location => location.Column)
            .ToArray();

        return new SymbolDto(
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

public sealed record SourceLocationDto(string Path, int Line, int Column, int EndLine, int EndColumn)
{
    public static SourceLocationDto FromLocation(Location location)
    {
        var span = location.GetLineSpan();
        return new SourceLocationDto(
            global::System.IO.Path.GetFullPath(span.Path),
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1);
    }
}

public sealed record ReferenceLocationDto(
    string Path,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    bool IsImplicit,
    string Definition)
{
    public static ReferenceLocationDto FromReferenceLocation(ISymbol definition, ReferenceLocation referenceLocation)
    {
        var location = SourceLocationDto.FromLocation(referenceLocation.Location);
        return new ReferenceLocationDto(
            location.Path,
            location.Line,
            location.Column,
            location.EndLine,
            location.EndColumn,
            referenceLocation.IsImplicit,
            definition.ToDisplayString(SymbolDisplayFormats.Qualified));
    }
}

public static class SymbolDisplayFormats
{
    public static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);
}
