using System.Text;

namespace RoslynKit;

/// <summary>
/// Renders command result payloads as deterministic plain text for the <c>--format text</c> output mode.
/// Successful results bypass the JSON envelope entirely: output is line-oriented with one header line per
/// payload, one line per item, <c>path:line:column</c> locations, and raw payload text with no JSON string
/// escaping. Failures still emit the standard JSON <c>errors</c> envelope, so a zero exit code means stdout
/// is plain text and a non-zero exit code means stdout is JSON.
/// </summary>
public static class TextProjection
{
    public static string Render(object data)
    {
        return data switch
        {
            SymbolsResult result => RenderSymbols(result),
            DefinitionResult result => RenderDefinition(result),
            TypeDefinitionResult result => RenderTypeDefinition(result),
            ReferencesResult result => RenderReferences(result),
            ImplementationsResult result => RenderImplementations(result),
            QuickInfoResult result => RenderQuickInfo(result),
            SignatureHelpResult result => RenderSignatureHelp(result),
            SymbolSourceResult result => RenderSymbolSource(result),
            DocumentSymbolsResult result => RenderDocumentSymbols(result),
            DocumentTextResult result => RenderDocumentText(result),
            DiagnosticsResult result => RenderDiagnostics(result),
            WorkspaceResult result => RenderWorkspace(result),
            _ => data.ToString() ?? string.Empty,
        };
    }

    private static string RenderSymbols(SymbolsResult result)
    {
        var builder = new StringBuilder();
        builder.Append("symbols ").Append(result.Query);
        AppendCounts(builder, result.TotalCount, result.ReturnedCount, result.Truncated);
        foreach (var symbol in result.Symbols)
        {
            AppendSymbolLines(builder, symbol);
        }

        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderDefinition(DefinitionResult result)
    {
        var builder = new StringBuilder();
        builder.Append("definition ").Append(result.Selector ?? Position(result.Document, result.Line, result.Column) ?? "-");
        AppendSymbolLines(builder, result.Symbol);
        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderTypeDefinition(TypeDefinitionResult result)
    {
        var builder = new StringBuilder();
        builder.Append("type-definition ").Append(Position(result.Document, result.Line, result.Column) ?? "-");
        AppendSymbolLines(builder, result.Symbol);
        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderReferences(ReferencesResult result)
    {
        var builder = new StringBuilder();
        builder.Append("references ").Append(result.Selector ?? Position(result.Document, result.Line, result.Column) ?? "-");
        AppendCounts(builder, result.TotalCount, result.ReturnedCount, result.Truncated);
        builder.Append("\nsymbol");
        AppendSymbolFields(builder, result.Symbol);
        foreach (var reference in result.Locations)
        {
            builder.Append('\n').Append(reference.Path ?? "-").Append(':').Append(reference.Line).Append(':').Append(reference.Column);
            if (reference.IsImplicit)
            {
                builder.Append(" implicit");
            }
        }

        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderImplementations(ImplementationsResult result)
    {
        var builder = new StringBuilder();
        builder.Append("implementations ").Append(result.Selector ?? Position(result.Document, result.Line, result.Column) ?? "-");
        AppendCounts(builder, result.TotalCount, result.ReturnedCount, result.Truncated);
        builder.Append("\nsymbol");
        AppendSymbolFields(builder, result.Symbol);
        foreach (var symbol in result.Symbols)
        {
            AppendSymbolLines(builder, symbol);
        }

        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderQuickInfo(QuickInfoResult result)
    {
        var builder = new StringBuilder();
        builder.Append("quick-info ").Append(Position(result.Document, result.Line, result.Column));
        if (result.Tags.Count > 0)
        {
            builder.Append(" [").Append(string.Join(",", result.Tags)).Append(']');
        }

        foreach (var section in result.Sections)
        {
            builder.Append('\n').Append(section.Text);
        }

        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderSignatureHelp(SignatureHelpResult result)
    {
        var builder = new StringBuilder();
        builder.Append("signature-help ").Append(Position(result.Document, result.Line, result.Column))
            .Append(" active ").Append(result.ActiveSignature)
            .Append(" parameter ").Append(result.ActiveParameter);
        foreach (var signature in result.Signatures)
        {
            builder.Append('\n').Append(signature.Label);
        }

        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderSymbolSource(SymbolSourceResult result)
    {
        var builder = new StringBuilder();
        builder.Append("symbol-source ").Append(result.Selector);
        builder.Append("\nsymbol");
        AppendSymbolFields(builder, result.Symbol);
        foreach (var declaration in result.Declarations)
        {
            builder.Append("\n== ").Append(DocumentPath(declaration.Document))
                .Append(':').Append(declaration.Range.Line).Append(':').Append(declaration.Range.Column)
                .Append('-').Append(declaration.Range.EndLine).Append(':').Append(declaration.Range.EndColumn);
            builder.Append('\n').Append(declaration.Text);
        }

        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderDocumentSymbols(DocumentSymbolsResult result)
    {
        var builder = new StringBuilder();
        builder.Append("document-symbols ").Append(DocumentPath(result.Document));
        foreach (var symbol in result.Symbols)
        {
            AppendSymbolLines(builder, symbol);
        }

        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderDocumentText(DocumentTextResult result)
    {
        var builder = new StringBuilder();
        builder.Append("document-text ").Append(DocumentPath(result.Document));
        if (result.Truncated)
        {
            builder.Append(" truncated");
        }

        builder.Append('\n').Append(result.Text);
        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderDiagnostics(DiagnosticsResult result)
    {
        var builder = new StringBuilder();
        builder.Append("diagnostics");
        AppendCounts(builder, result.TotalCount, result.ReturnedCount, result.Truncated);
        foreach (var diagnostic in result.Diagnostics)
        {
            builder.Append('\n').Append(diagnostic.Severity).Append(' ').Append(diagnostic.Id).Append(' ');
            builder.Append(diagnostic.Path is null ? "-" : $"{diagnostic.Path}:{diagnostic.Line}:{diagnostic.Column}");
            builder.Append(' ').Append(diagnostic.Message);
        }

        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderWorkspace(WorkspaceResult result)
    {
        var builder = new StringBuilder();
        builder.Append("workspace ").Append(result.TargetPath).Append(' ').Append(result.TargetKind);
        foreach (var project in result.Projects)
        {
            builder.Append("\nproject ").Append(project.Name)
                .Append(' ').Append(project.TargetFramework ?? "-")
                .Append(" docs ").Append(project.DocumentCount);
        }

        foreach (var document in result.Documents)
        {
            builder.Append("\ndocument ").Append(document.DocumentKey)
                .Append(' ').Append(document.DocumentKind)
                .Append(' ').Append(DocumentPath(document));
        }

        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static void AppendSymbolLines(StringBuilder builder, SymbolItem symbol)
    {
        builder.Append('\n').Append(symbol.Kind);
        AppendSymbolFields(builder, symbol);
        if (symbol.Declarations.Count > 1)
        {
            foreach (var declaration in symbol.Declarations)
            {
                builder.Append("\ndecl ").Append(Location(declaration));
            }
        }
    }

    private static void AppendSymbolFields(StringBuilder builder, SymbolItem symbol)
    {
        builder.Append(' ').Append(symbol.DisplayName)
            .Append(' ').Append(Location(symbol.PrimaryLocation) ?? "-")
            .Append(' ').Append(symbol.SymbolId ?? "-");
    }

    private static void AppendCounts(StringBuilder builder, int total, int returned, bool truncated)
    {
        builder.Append(" total ").Append(total).Append(" returned ").Append(returned);
        if (truncated)
        {
            builder.Append(" truncated");
        }
    }

    private static void AppendWorkspaceDiagnostics(StringBuilder builder, IReadOnlyList<WorkspaceLoadDiagnostic> diagnostics)
    {
        // Text mode reports only how many workspace-load diagnostics occurred; the full messages remain
        // available in the default json output.
        if (diagnostics.Count > 0)
        {
            builder.Append("\nworkspace-diagnostics ").Append(diagnostics.Count);
        }
    }

    private static string? Location(SourceRange? range)
    {
        return range is null ? null : $"{range.Path}:{range.Line}:{range.Column}";
    }

    private static string? Position(DocumentDescriptor? document, int? line, int? column)
    {
        return document is null || line is null || column is null
            ? null
            : $"{DocumentPath(document)}:{line.Value}:{column.Value}";
    }

    private static string DocumentPath(DocumentDescriptor document)
    {
        return document.Path ?? document.Name;
    }
}
