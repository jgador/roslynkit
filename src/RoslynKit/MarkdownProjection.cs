using System.Text;

namespace RoslynKit;

/// <summary>
/// Renders command results and help as the single markdown-flavored text output format: key-value header
/// lines, labeled compact bullets, inline code spans, fenced code blocks for source text, and one-based
/// <c>path:line:column-endLine:endColumn</c> locations. Output is deterministic and payload text inside
/// fences is preserved verbatim.
/// </summary>
public static class MarkdownProjection
{
    /// <summary>
    /// Dispatches result models to the deterministic markdown renderer used by every successful command.
    /// </summary>
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
            DocumentLinesResult result => RenderDocumentLines(result),
            DiagnosticsResult result => RenderDiagnostics(result),
            WorkspaceResult result => RenderWorkspace(result),
            _ => data.ToString() ?? string.Empty,
        };
    }

    /// <summary>
    /// Renders top-level or command-specific help from built-in command metadata.
    /// </summary>
    public static string RenderHelp(BuiltinCommand? subject)
    {
        return subject is null ? RenderHelpOverview() : RenderCommandHelp(subject);
    }

    private static string RenderHelpOverview()
    {
        var builder = new StringBuilder();
        builder.Append("tool: roslynkit");
        builder.Append("\ndescription: Unofficial Roslyn-powered C# code intelligence CLI for coding agents and terminal workflows. This is not an MCP server and not an LSP client.");
        builder.Append('\n');
        foreach (var command in BuiltinCommandRegistry.Commands)
        {
            builder.Append("\n- command: ").Append(CodeSpan(command.Name)).Append(" description: ").Append(command.Description);
        }

        return builder.ToString();
    }

    private static string RenderCommandHelp(BuiltinCommand command)
    {
        var builder = new StringBuilder();
        builder.Append("command: ").Append(command.Name);
        builder.Append("\ndescription: ").Append(command.Description);
        foreach (var usage in command.Usage)
        {
            builder.Append("\nusage: ").Append(CodeSpan(usage));
        }

        if (command.Options.Count > 0)
        {
            builder.Append('\n');
            foreach (var option in command.Options)
            {
                builder.Append("\n- option: ").Append(CodeSpan($"--{option.LongName}"));
                if (option.ShortName is { } shortName)
                {
                    builder.Append(" short: ").Append(CodeSpan($"-{shortName}"));
                }

                if (option.ValueName is { } valueName)
                {
                    builder.Append(" value: ").Append(valueName);
                }

                if (option.Required)
                {
                    builder.Append(" required: true");
                }

                builder.Append(" description: ").Append(option.Description);
            }
        }

        return builder.ToString();
    }

    private static string RenderSymbols(SymbolsResult result)
    {
        var builder = new StringBuilder();
        builder.Append("command: symbols");
        builder.Append("\nquery: ").Append(CodeSpan(result.Query));
        AppendCounts(builder, result.TotalCount, result.ReturnedCount, result.Truncated);
        AppendSymbolBullets(builder, result.Symbols, includeDocumentation: true);
        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderDefinition(DefinitionResult result)
    {
        var builder = new StringBuilder();
        builder.Append("command: definition");
        AppendSelector(builder, result.Selector, result.Document, result.Line, result.Column);
        AppendSymbolBullets(builder, [result.Symbol], includeDocumentation: true);
        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderTypeDefinition(TypeDefinitionResult result)
    {
        var builder = new StringBuilder();
        builder.Append("command: type-definition");
        AppendSelector(builder, selector: null, result.Document, result.Line, result.Column);
        AppendSymbolBullets(builder, [result.Symbol], includeDocumentation: true);
        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderReferences(ReferencesResult result)
    {
        var builder = new StringBuilder();
        builder.Append("command: references");
        AppendSelector(builder, result.Selector, result.Document, result.Line, result.Column);
        builder.Append("\nsymbol: ").Append(CodeSpan(result.Symbol.SymbolId ?? result.Symbol.DisplayName));
        if (result.Symbol.Documentation is { Length: > 0 } documentation)
        {
            builder.Append("\ndocumentation: ").Append(documentation);
        }

        AppendCounts(builder, result.TotalCount, result.ReturnedCount, result.Truncated);
        if (result.Locations.Count > 0)
        {
            builder.Append('\n');
            foreach (var reference in result.Locations)
            {
                builder.Append("\n- loc: ").Append(CodeSpan(Location(reference.Path, reference.Line, reference.Column, reference.EndLine, reference.EndColumn)));
                if (reference.IsImplicit)
                {
                    builder.Append(" implicit: true");
                }
            }
        }

        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderImplementations(ImplementationsResult result)
    {
        var builder = new StringBuilder();
        builder.Append("command: implementations");
        AppendSelector(builder, result.Selector, result.Document, result.Line, result.Column);
        builder.Append("\nsymbol: ").Append(CodeSpan(result.Symbol.SymbolId ?? result.Symbol.DisplayName));
        AppendCounts(builder, result.TotalCount, result.ReturnedCount, result.Truncated);
        AppendSymbolBullets(builder, result.Symbols, includeDocumentation: true);
        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderQuickInfo(QuickInfoResult result)
    {
        var builder = new StringBuilder();
        builder.Append("command: quick-info");
        AppendSelector(builder, selector: null, result.Document, result.Line, result.Column);
        builder.Append("\nrange: ").Append(CodeSpan(Location(DocumentPath(result.Document), result.ResolvedRange)));
        if (result.Tags.Count > 0)
        {
            builder.Append("\ntags: ").Append(string.Join(", ", result.Tags.Select(CodeSpan)));
        }

        var description = result.Sections.FirstOrDefault(section => string.Equals(section.Kind, "Description", StringComparison.Ordinal));
        if (description is not null)
        {
            builder.Append("\n\ndescription:\n");
            AppendFence(builder, description.Text, "csharp");
        }

        var documentation = result.Sections
            .Where(section => !ReferenceEquals(section, description))
            .Select(section => section.Text)
            .ToArray();
        if (documentation.Length > 0)
        {
            builder.Append("\n\ndocumentation:\n");
            AppendFence(builder, string.Join("\n\n", documentation), "text");
        }

        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderSignatureHelp(SignatureHelpResult result)
    {
        var builder = new StringBuilder();
        builder.Append("command: signature-help");
        AppendSelector(builder, selector: null, result.Document, result.Line, result.Column);
        builder.Append("\nactive-signature: ").Append(result.ActiveSignature);
        builder.Append("\nactive-parameter: ").Append(result.ActiveParameter);
        if (result.Signatures.Count > 0)
        {
            builder.Append('\n');
            foreach (var signature in result.Signatures)
            {
                builder.Append("\n- signature: ").Append(CodeSpan(signature.Label));
            }
        }

        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderSymbolSource(SymbolSourceResult result)
    {
        var builder = new StringBuilder();
        builder.Append("command: symbol-source");
        builder.Append("\nsymbol: ").Append(CodeSpan(result.Selector));
        AppendSymbolBullets(builder, [result.Symbol]);
        foreach (var declaration in result.Declarations)
        {
            builder.Append("\n\nloc: ").Append(CodeSpan(Location(DocumentPath(declaration.Document), declaration.Range))).Append('\n');
            AppendFence(builder, declaration.Text, "csharp");
        }

        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderDocumentSymbols(DocumentSymbolsResult result)
    {
        var builder = new StringBuilder();
        builder.Append("command: document-symbols");
        builder.Append("\nfile: ").Append(CodeSpan(DocumentPath(result.Document)));
        AppendSymbolBullets(builder, result.Symbols, includeDocumentation: true);
        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderDocumentText(DocumentTextResult result)
    {
        var builder = new StringBuilder();
        builder.Append("command: document-text");
        builder.Append("\npath: ").Append(CodeSpan(DocumentPath(result.Document)));
        if (result.Truncated)
        {
            builder.Append("\ntruncated: true");
        }

        builder.Append("\n\n");
        AppendFence(builder, result.Text, FenceInfo(result.Document.DocumentKind));
        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderDocumentLines(DocumentLinesResult result)
    {
        var builder = new StringBuilder();
        builder.Append("command: document-lines");
        builder.Append("\npath: ").Append(CodeSpan(DocumentPath(result.Document)));
        builder.Append("\nrange: ").Append(CodeSpan(Location(DocumentPath(result.Document), result.Range)));
        builder.Append("\n\n");
        AppendFence(builder, result.Text, FenceInfo(result.Document.DocumentKind));
        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderDiagnostics(DiagnosticsResult result)
    {
        var builder = new StringBuilder();
        builder.Append("command: diagnostics");
        AppendCounts(builder, result.TotalCount, result.ReturnedCount, result.Truncated);
        if (result.Diagnostics.Count > 0)
        {
            builder.Append('\n');
            foreach (var diagnostic in result.Diagnostics)
            {
                builder.Append("\n- severity: ").Append(diagnostic.Severity)
                    .Append(" id: ").Append(CodeSpan(diagnostic.Id));
                if (diagnostic.Path is not null && diagnostic.Line is { } line && diagnostic.Column is { } column)
                {
                    builder.Append(" loc: ").Append(CodeSpan(Location(diagnostic.Path, line, column, diagnostic.EndLine ?? line, diagnostic.EndColumn ?? column)));
                }

                builder.Append(" message: ").Append(CodeSpan(diagnostic.Message));
            }
        }

        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static string RenderWorkspace(WorkspaceResult result)
    {
        var builder = new StringBuilder();
        builder.Append("command: workspace");
        builder.Append("\ndocuments: ").Append(result.Documents.Count);
        if (result.Projects.Count > 0 || result.Documents.Count > 0)
        {
            builder.Append('\n');
            foreach (var project in result.Projects)
            {
                builder.Append("\n- project: ").Append(CodeSpan(project.Name));
                if (project.TargetFramework is { } projectTfm)
                {
                    builder.Append(" tfm: ").Append(CodeSpan(projectTfm));
                }

                builder.Append(" documents: ").Append(project.DocumentCount);
            }

            foreach (var document in result.Documents)
            {
                builder.Append("\n- project: ").Append(CodeSpan(DocumentProjectPath(document)));
                if (document.TargetFramework is { } documentTfm)
                {
                    builder.Append(" tfm: ").Append(CodeSpan(documentTfm));
                }

                builder.Append(" kind: ").Append(document.DocumentKind)
                    .Append(" path: ").Append(CodeSpan(DocumentPath(document)));
            }
        }

        AppendWorkspaceDiagnostics(builder, result.WorkspaceDiagnostics);
        return builder.ToString();
    }

    private static void AppendSelector(StringBuilder builder, string? selector, DocumentDescriptor? document, int? line, int? column)
    {
        var value = selector
            ?? (document is not null && line is { } lineValue && column is { } columnValue
                ? Location(DocumentPath(document), lineValue, columnValue, lineValue, columnValue)
                : null);
        if (value is not null)
        {
            builder.Append("\nselector: ").Append(CodeSpan(value));
        }
    }

    private static void AppendSymbolBullets(StringBuilder builder, IReadOnlyList<SymbolItem> symbols, bool includeDocumentation = false)
    {
        if (symbols.Count == 0)
        {
            return;
        }

        builder.Append('\n');
        foreach (var symbol in symbols)
        {
            builder.Append("\n- kind: ").Append(symbol.Kind)
                .Append(" name: ").Append(CodeSpan(symbol.DisplayName));
            if (symbol.PrimaryLocation is { } location)
            {
                builder.Append(" loc: ").Append(CodeSpan(Location(location)));
            }

            if (symbol.SymbolId is { } symbolId)
            {
                builder.Append(" id: ").Append(CodeSpan(symbolId));
            }

            if (includeDocumentation && symbol.Documentation is { Length: > 0 } documentation)
            {
                builder.Append("\n  documentation: ").Append(documentation);
            }

            if (symbol.Declarations.Count > 1)
            {
                foreach (var declaration in symbol.Declarations)
                {
                    builder.Append("\n- decl: ").Append(CodeSpan(Location(declaration)));
                }
            }
        }
    }

    private static void AppendCounts(StringBuilder builder, int total, int returned, bool truncated)
    {
        builder.Append("\nreturned: ").Append(returned).Append('/').Append(total);
        builder.Append("\ntruncated: ").Append(truncated ? "true" : "false");
    }

    private static void AppendWorkspaceDiagnostics(StringBuilder builder, IReadOnlyList<WorkspaceLoadDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return;
        }

        builder.Append("\n\nworkspace-diagnostics: ").Append(diagnostics.Count);
        foreach (var diagnostic in diagnostics)
        {
            builder.Append("\n- severity: ").Append(diagnostic.Kind).Append(" message: ").Append(CodeSpan(diagnostic.Message));
        }
    }

    private static void AppendFence(StringBuilder builder, string text, string info)
    {
        var fence = new string('`', Math.Max(3, LongestBacktickRun(text) + 1));
        builder.Append(fence).Append(info).Append('\n');
        builder.Append(text);
        if (text.Length > 0 && !text.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        builder.Append(fence);
    }

    private static string CodeSpan(string value)
    {
        var delimiter = new string('`', LongestBacktickRun(value) + 1);
        var needsPadding = value.Length == 0 || value.StartsWith('`') || value.EndsWith('`');
        return needsPadding ? $"{delimiter} {value} {delimiter}" : $"{delimiter}{value}{delimiter}";
    }

    private static int LongestBacktickRun(string text)
    {
        var longest = 0;
        var current = 0;
        foreach (var character in text)
        {
            current = character == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    private static string Location(SourceRange range)
    {
        return Location(range.Path, range.Line, range.Column, range.EndLine, range.EndColumn);
    }

    private static string Location(string? path, DocumentRange range)
    {
        return Location(path, range.Line, range.Column, range.EndLine, range.EndColumn);
    }

    private static string Location(string? path, int line, int column, int endLine, int endColumn)
    {
        return $"{path ?? "-"}:{line}:{column}-{endLine}:{endColumn}";
    }

    private static string FenceInfo(string documentKind)
    {
        return documentKind is DocumentKindNames.Source or DocumentKindNames.SourceGenerated ? "csharp" : "text";
    }

    private static string DocumentPath(DocumentDescriptor document)
    {
        return document.DisplayPath ?? "-";
    }

    private static string DocumentProjectPath(DocumentDescriptor document)
    {
        return document.DisplayProjectPath ?? document.ProjectName;
    }
}
