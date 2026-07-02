namespace RoslynKit;

/// <summary>
/// Projects verbose command result payloads into the typed compact DTOs in <c>CompactModels</c> for the
/// <c>--format compact</c> output mode. The result is wrapped in the shared <see cref="JsonEnvelope"/>, so
/// compact and default json share an identical envelope frame; only the data projection and minification
/// differ. Compact drops the per-item metadata that agents rarely need and collapses source locations into
/// a single <c>path:line:column</c> string to minimize token usage. No anonymous objects are produced;
/// every shape is a typed record with explicit <c>[JsonPropertyName]</c>.
/// </summary>
public static class CompactProjection
{
    /// <summary>
    /// Projects a verbose command result payload into its trimmed compact DTO.
    /// </summary>
    public static object ProjectData(object data)
    {
        return data switch
        {
            SymbolsResult result => new CompactSymbolsData(
                result.TargetPath,
                result.Query,
                result.TotalCount,
                result.ReturnedCount,
                TrueOrNull(result.Truncated),
                result.Symbols.Select(Symbol).ToArray(),
                WorkspaceDiagnosticCount(result.WorkspaceDiagnostics)),
            DefinitionResult result => new CompactDefinitionData(
                Position(result.Document, result.Line, result.Column),
                Symbol(result.Symbol),
                WorkspaceDiagnosticCount(result.WorkspaceDiagnostics)),
            TypeDefinitionResult result => new CompactDefinitionData(
                Position(result.Document, result.Line, result.Column),
                Symbol(result.Symbol),
                WorkspaceDiagnosticCount(result.WorkspaceDiagnostics)),
            ReferencesResult result => new CompactReferencesData(
                Symbol(result.Symbol),
                result.TotalCount,
                result.ReturnedCount,
                TrueOrNull(result.Truncated),
                result.Locations.Select(Reference).ToArray(),
                WorkspaceDiagnosticCount(result.WorkspaceDiagnostics)),
            ImplementationsResult result => new CompactImplementationsData(
                Symbol(result.Symbol),
                result.TotalCount,
                result.ReturnedCount,
                TrueOrNull(result.Truncated),
                result.Symbols.Select(Symbol).ToArray(),
                WorkspaceDiagnosticCount(result.WorkspaceDiagnostics)),
            QuickInfoResult result => new CompactQuickInfoData(
                Position(result.Document, result.Line, result.Column),
                result.Tags.Count > 0 ? result.Tags : null,
                string.Join("\n", result.Sections.Select(section => section.Text)),
                WorkspaceDiagnosticCount(result.WorkspaceDiagnostics)),
            SignatureHelpResult result => new CompactSignatureHelpData(
                Position(result.Document, result.Line, result.Column),
                result.ActiveSignature,
                result.Signatures.Select(signature => signature.Label).ToArray(),
                WorkspaceDiagnosticCount(result.WorkspaceDiagnostics)),
            SymbolSourceResult result => new CompactSymbolSourceData(
                Symbol(result.Symbol),
                result.Declarations
                    .Select(declaration => new CompactSymbolSourceDeclaration(
                        $"{DocumentPath(declaration.Document)}:{declaration.Range.Line}:{declaration.Range.Column}",
                        declaration.Text))
                    .ToArray(),
                WorkspaceDiagnosticCount(result.WorkspaceDiagnostics)),
            DocumentSymbolsResult result => new CompactDocumentSymbolsData(
                DocumentPath(result.Document),
                result.Symbols.Select(Symbol).ToArray(),
                WorkspaceDiagnosticCount(result.WorkspaceDiagnostics)),
            DocumentTextResult result => new CompactDocumentTextData(
                DocumentPath(result.Document),
                TrueOrNull(result.Truncated),
                result.Text,
                WorkspaceDiagnosticCount(result.WorkspaceDiagnostics)),
            DiagnosticsResult result => new CompactDiagnosticsData(
                result.TotalCount,
                result.ReturnedCount,
                TrueOrNull(result.Truncated),
                result.Diagnostics.Select(Diagnostic).ToArray(),
                WorkspaceDiagnosticCount(result.WorkspaceDiagnostics)),
            WorkspaceResult result => new CompactWorkspaceData(
                result.TargetPath,
                result.TargetKind,
                result.Projects.Select(project => new CompactProject(project.Name, project.TargetFramework, project.DocumentCount)).ToArray(),
                result.Documents.Select(document => new CompactDocument(document.DocumentKey, document.DocumentKind, DocumentPath(document))).ToArray(),
                WorkspaceDiagnosticCount(result.WorkspaceDiagnostics)),
            _ => data,
        };
    }

    private static CompactSymbol Symbol(SymbolItem symbol)
    {
        return new CompactSymbol(
            symbol.Kind,
            symbol.Name,
            symbol.ContainingType ?? symbol.ContainingNamespace,
            Location(symbol.PrimaryLocation),
            symbol.Declarations.Count > 1 ? symbol.Declarations.Select(declaration => Location(declaration)!).ToArray() : null,
            symbol.SymbolId);
    }

    private static string Reference(ReferenceItem reference)
    {
        var location = $"{reference.Path}:{reference.Line}:{reference.Column}";
        return reference.IsImplicit ? $"{location} (implicit)" : location;
    }

    private static CompactDiagnostic Diagnostic(DiagnosticItem diagnostic)
    {
        return new CompactDiagnostic(
            diagnostic.Severity,
            diagnostic.Id,
            diagnostic.Path is null ? null : $"{diagnostic.Path}:{diagnostic.Line}:{diagnostic.Column}",
            diagnostic.Message);
    }

    private static string? Location(SourceRange? range)
    {
        return range is null ? null : $"{range.Path}:{range.Line}:{range.Column}";
    }

    private static string Position(DocumentDescriptor document, int line, int column)
    {
        return $"{DocumentPath(document)}:{line}:{column}";
    }

    private static string? Position(DocumentDescriptor? document, int? line, int? column)
    {
        return document is null || line is null || column is null
            ? null
            : Position(document, line.Value, column.Value);
    }

    private static string DocumentPath(DocumentDescriptor document)
    {
        return document.Path ?? document.Name;
    }

    private static int? WorkspaceDiagnosticCount(IReadOnlyList<WorkspaceLoadDiagnostic> diagnostics)
    {
        // Compact mode reports only how many workspace-load diagnostics occurred; the full messages remain
        // available in the default json output. On large targets these lists can dominate the payload.
        return diagnostics.Count == 0 ? null : diagnostics.Count;
    }

    private static bool? TrueOrNull(bool value)
    {
        return value ? true : null;
    }
}
