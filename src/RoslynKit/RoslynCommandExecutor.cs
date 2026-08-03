using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;

namespace RoslynKit;

/// <summary>
/// Executes parsed RoslynKit semantic commands against standalone or caller-owned workspaces.
/// </summary>
public static partial class RoslynCommandExecutor
{
    private static readonly string[] SupportedSymbolKinds =
    [
        "namespace",
        "type",
        "member",
        "method",
        "property",
        "field",
        "event",
        "class",
        "interface",
        "struct",
        "enum",
        "delegate",
    ];

    /// <summary>
    /// Loads and owns a standalone workspace, then dispatches the parsed command to its Roslyn-backed handler.
    /// </summary>
    public static async Task<object> ExecuteAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        ValidateBeforeWorkspaceLoad(command);
        using var loaded = command.Name is "index" or "search"
            ? await SearchCommandService.LoadStableWorkspaceAsync(command, cancellationToken).ConfigureAwait(false)
            : await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync(command, loaded, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatches a parsed command against a caller-owned workspace that remains valid after execution completes.
    /// </summary>
    public static async Task<object> ExecuteAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loaded);

        return command.Name switch
        {
            "diagnostics" => await DiagnosticsAsync(command, loaded, cancellationToken).ConfigureAwait(false),
            "definition" => await DefinitionAsync(command, loaded, cancellationToken).ConfigureAwait(false),
            "document-lines" => await DocumentLinesAsync(command, loaded, cancellationToken).ConfigureAwait(false),
            "document-symbols" => await DocumentSymbolsAsync(command, loaded, cancellationToken).ConfigureAwait(false),
            "document-text" => await DocumentTextAsync(command, loaded, cancellationToken).ConfigureAwait(false),
            "implementations" => await ImplementationsAsync(command, loaded, cancellationToken).ConfigureAwait(false),
            "index" => await SearchCommandService.IndexAsync(command, loaded, cancellationToken).ConfigureAwait(false),
            "quick-info" => await QuickInfoAsync(command, loaded, cancellationToken).ConfigureAwait(false),
            "references" => await ReferencesAsync(command, loaded, cancellationToken).ConfigureAwait(false),
            "search" => await SearchCommandService.SearchAsync(command, loaded, cancellationToken).ConfigureAwait(false),
            "signature-help" => await SignatureHelpAsync(command, loaded, cancellationToken).ConfigureAwait(false),
            "symbol-source" => await SymbolSourceAsync(command, loaded, cancellationToken).ConfigureAwait(false),
            "symbols" => await SymbolsAsync(command, loaded, cancellationToken).ConfigureAwait(false),
            "type-definition" => await TypeDefinitionAsync(command, loaded, cancellationToken).ConfigureAwait(false),
            "workspace" => await WorkspaceAsync(command, loaded, cancellationToken).ConfigureAwait(false),
            _ => throw new CliUsageException(command.Name, $"Unknown command '{command.Name}'."),
        };
    }

    private static void ValidateBeforeWorkspaceLoad(ParsedCommand command)
    {
        switch (command.Name)
        {
            case "diagnostics":
                _ = command.OptionalInt("max-results", 200, 1);
                break;
            case "symbols":
                _ = command.Required("query");
                _ = command.OptionalInt("max-results", 200, 1);
                _ = GetSymbolFilter(command.Name, command.Optional("kind"));
                break;
            case "index":
                _ = command.Required("index-path");
                break;
            case "search":
                _ = command.Required("index-path");
                _ = command.Required("query");
                _ = command.OptionalInt("max-results", 20, 1);
                _ = GetSymbolFilter(command.Name, command.Optional("kind"));
                break;
            case "document-lines":
                var startLine = command.OptionalInt("start-line", 1, 1);
                var endLine = command.OptionalInt("end-line", 1, 1);
                if (endLine < startLine)
                {
                    throw new CliUsageException(command.Name, "Option '--end-line' must be greater than or equal to '--start-line'.");
                }

                break;
            case "references":
            case "implementations":
                _ = command.OptionalInt("max-results", 200, 1);
                break;
            case "symbol-source":
                _ = command.Required("symbol");
                break;
            case "definition":
            case "document-symbols":
            case "document-text":
            case "quick-info":
            case "signature-help":
            case "type-definition":
            case "workspace":
                break;
            default:
                throw new CliUsageException(command.Name, $"Unknown command '{command.Name}'.");
        }
    }

    private static async Task<object> WorkspaceAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var documents = await loaded.EnumerateDocumentsAsync(
            new DocumentEnumerationOptions(
                IncludeGenerated: command.Flag("include-generated"),
                IncludeAdditional: command.Flag("include-additional"),
                IncludeAnalyzerConfig: command.Flag("include-analyzer-config"),
                RepositoryRelevantOnly: true),
            cancellationToken).ConfigureAwait(false);
        var documentCounts = documents
            .GroupBy(document => document.Project.Id)
            .ToDictionary(group => group.Key, group => group.Count());

        var projects = loaded.Solution.Projects
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .ThenBy(project => project.FilePath, StringComparer.Ordinal)
            .Select(project => new WorkspaceProject(
                project.Name,
                RoslynDocumentFilters.NormalizePath(project.FilePath),
                loaded.GetTargetFramework(project),
                project.Language,
                documentCounts.TryGetValue(project.Id, out var count) ? count : 0,
                project.ProjectReferences
                    .Select(reference => project.Solution.GetProject(reference.ProjectId)?.Name)
                    .Where(referenceName => !string.IsNullOrWhiteSpace(referenceName))
                    .Order(StringComparer.Ordinal)
                    .ToArray()!))
            .ToArray();

        return new WorkspaceResult(
            loaded.TargetPath,
            loaded.TargetKind,
            projects,
            documents.Select(document => document.Descriptor).ToArray(),
            loaded.WorkspaceDiagnostics);
    }

    private static async Task<object> DiagnosticsAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var maxResults = command.OptionalInt("max-results", 200, 1);
        var includeGenerated = command.Flag("include-generated");
        var includeHidden = command.Flag("include-hidden");
        var diagnostics = new List<DiagnosticItem>();

        foreach (var project in loaded.Solution.Projects.OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            var projectSourcePaths = RoslynDocumentFilters.GetProjectSourcePaths(project);
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
            {
                if (!includeHidden && diagnostic.Severity == DiagnosticSeverity.Hidden)
                {
                    continue;
                }

                if (!includeGenerated && diagnostic.Location.IsInSource && !RoslynDocumentFilters.LocationMatchesAnyPath(diagnostic.Location, projectSourcePaths))
                {
                    continue;
                }

                diagnostics.Add(DiagnosticItem.FromDiagnostic(project.Name, diagnostic));
            }
        }

        var ordered = diagnostics
            .OrderBy(diagnostic => diagnostic.Path, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Line)
            .ThenBy(diagnostic => diagnostic.Column)
            .ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .Take(maxResults)
            .ToArray();

        return new DiagnosticsResult(loaded.TargetPath, diagnostics.Count, ordered.Length, diagnostics.Count > ordered.Length, ordered, loaded.WorkspaceDiagnostics);
    }

    private static async Task<object> SymbolsAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var query = command.Required("query");
        var maxResults = command.OptionalInt("max-results", 200, 1);
        var exact = command.Flag("exact");
        var caseSensitive = command.Flag("case-sensitive");
        var kind = command.Optional("kind");
        var symbolFilter = GetSymbolFilter(command.Name, kind);
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var sourcePaths = RoslynDocumentFilters.GetSolutionSourcePaths(loaded.Solution);
        var foundSymbols = exact
            ? await SymbolFinder.FindSourceDeclarationsAsync(loaded.Solution, query, ignoreCase: !caseSensitive, symbolFilter, cancellationToken).ConfigureAwait(false)
            : await SymbolFinder.FindSourceDeclarationsWithPatternAsync(loaded.Solution, query, symbolFilter, cancellationToken).ConfigureAwait(false);

        var symbols = foundSymbols
            .Where(RoslynSymbolSearch.IsCodeSymbol)
            .Where(symbol => IsSpecificSymbolKindMatch(symbol, kind))
            .Where(symbol => RoslynDocumentFilters.IsDeclaredInProject(symbol, sourcePaths))
            .Where(symbol => exact || !caseSensitive || SymbolMatches(symbol, query, comparison))
            .Select(symbol => SymbolItem.FromSymbol(symbol, GetProjectName(symbol, loaded.Solution), sourcePaths))
            .Where(symbol => symbol.Declarations.Count > 0)
            .DistinctBy(symbol => string.Concat(symbol.ProjectName, "|", symbol.Kind, "|", symbol.DisplayName, "|", symbol.PrimaryLocation?.Path, "|", symbol.PrimaryLocation?.Line, "|", symbol.PrimaryLocation?.Column))
            .ToArray();

        var ordered = symbols
            .OrderBy(symbol => symbol.DisplayName, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Kind, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.PrimaryLocation?.Path, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.PrimaryLocation?.Line)
            .Take(maxResults)
            .ToArray();

        return new SymbolsResult(loaded.TargetPath, query, symbols.Length, ordered.Length, symbols.Length > ordered.Length, ordered, loaded.WorkspaceDiagnostics);
    }

    private static async Task<object> DocumentTextAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var context = await ResolveTextDocumentAsync(command, loaded, cancellationToken).ConfigureAwait(false);
        var text = await context.TextDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var resolvedRange = PositionResolver.ToDocumentRange(text, new TextSpan(0, text.Length));

        return new DocumentTextResult(
            context.Descriptor,
            resolvedRange,
            text.ToString(),
            truncated: false,
            loaded.WorkspaceDiagnostics);
    }

    private static async Task<object> DocumentLinesAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var startLine = command.OptionalInt("start-line", 1, 1);
        var endLine = command.OptionalInt("end-line", 1, 1);
        if (endLine < startLine)
        {
            throw new CliUsageException(command.Name, "Option '--end-line' must be greater than or equal to '--start-line'.");
        }

        var context = await ResolveTextDocumentAsync(command, loaded, cancellationToken).ConfigureAwait(false);
        var text = await context.TextDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (startLine > text.Lines.Count)
        {
            var hint = $"Retry with --start-line between 1 and {text.Lines.Count}, or run document-lines with an in-range --start-line and oversized --end-line to inspect the document end.";
            throw new CliUsageException(command.Name, $"Line {startLine} is outside the document range 1..{text.Lines.Count}.", hint);
        }

        var resolvedEndLine = Math.Min(endLine, text.Lines.Count);
        var startTextLine = text.Lines[startLine - 1];
        var endTextLine = text.Lines[resolvedEndLine - 1];
        var span = TextSpan.FromBounds(startTextLine.Span.Start, endTextLine.Span.End);
        var range = new DocumentRange(startLine, 1, resolvedEndLine, endTextLine.Span.Length + 1);

        return new DocumentLinesResult(
            context.Descriptor,
            range,
            text.ToString(span),
            loaded.WorkspaceDiagnostics);
    }

    private static async Task<object> DocumentSymbolsAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var context = await ResolveSemanticDocumentAsync(command, loaded, cancellationToken).ConfigureAwait(false);
        var document = context.Document!;
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CliUsageException(command.Name, $"Could not create semantic model for '{context.Descriptor.Name}'.");
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CliUsageException(command.Name, $"Could not parse '{context.Descriptor.Name}'.");

        var symbols = root.DescendantNodesAndSelf()
            .Select(node => model.GetDeclaredSymbol(node, cancellationToken))
            .Where(symbol => symbol is not null && RoslynSymbolSearch.IsDocumentSymbol(symbol) && symbol.Locations.Any(location => location.IsInSource && location.SourceTree == root.SyntaxTree))
            .Select(symbol => SymbolItem.FromSymbol(symbol!, GetProjectName(symbol!, loaded.Solution), root.SyntaxTree))
            .Where(symbol => symbol.Declarations.Count > 0)
            .DistinctBy(symbol => string.Concat(symbol.Kind, "|", symbol.DisplayName, "|", symbol.PrimaryLocation?.Path, "|", symbol.PrimaryLocation?.Line, "|", symbol.PrimaryLocation?.Column))
            .OrderBy(symbol => symbol.PrimaryLocation?.Line)
            .ThenBy(symbol => symbol.PrimaryLocation?.Column)
            .ThenBy(symbol => symbol.DisplayName, StringComparer.Ordinal)
            .ToArray();

        return new DocumentSymbolsResult(context.Descriptor, symbols, loaded.WorkspaceDiagnostics);
    }

    /// <summary>
    /// Resolves the selected symbol, from either a symbol selector or a document position, to its source definition payload.
    /// </summary>
    private static async Task<object> DefinitionAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var target = await ResolveCommandSymbolAsync(command, loaded, cancellationToken).ConfigureAwait(false);
        var sourceSymbol = await SymbolFinder.FindSourceDefinitionAsync(target.Symbol, loaded.Solution, cancellationToken).ConfigureAwait(false) ?? target.Symbol;

        return new DefinitionResult(
            target.Document,
            target.Line,
            target.Column,
            target.Selector,
            SymbolItem.FromSymbol(sourceSymbol, GetProjectName(sourceSymbol, loaded.Solution)),
            loaded.WorkspaceDiagnostics);
    }

    private static async Task<object> TypeDefinitionAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var context = await ResolveSemanticDocumentAsync(command, loaded, cancellationToken).ConfigureAwait(false);
        var symbol = await SymbolAtPositionAsync(command, context.Document!, cancellationToken).ConfigureAwait(false);
        var typeSymbol = GetTypeDefinitionSymbol(symbol);
        if (typeSymbol is null)
        {
            throw new CliUsageException(command.Name, $"No type definition found at line {command.OptionalInt("line", 1, 1)}, column {command.OptionalInt("column", 1, 1)} in '{context.Descriptor.Name}'.");
        }

        var sourceSymbol = await SymbolFinder.FindSourceDefinitionAsync(typeSymbol, loaded.Solution, cancellationToken).ConfigureAwait(false) ?? typeSymbol;
        return new TypeDefinitionResult(
            context.Descriptor,
            command.OptionalInt("line", 1, 1),
            command.OptionalInt("column", 1, 1),
            SymbolItem.FromSymbol(sourceSymbol, GetProjectName(sourceSymbol, loaded.Solution)),
            loaded.WorkspaceDiagnostics);
    }

    private static async Task<object> ReferencesAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var maxResults = command.OptionalInt("max-results", 200, 1);
        var target = await ResolveCommandSymbolAsync(command, loaded, cancellationToken).ConfigureAwait(false);
        var sourceSymbol = await SymbolFinder.FindSourceDefinitionAsync(target.Symbol, loaded.Solution, cancellationToken).ConfigureAwait(false) ?? target.Symbol;
        var references = await SymbolFinder.FindReferencesAsync(sourceSymbol, loaded.Solution, cancellationToken).ConfigureAwait(false);

        var locations = references
            .SelectMany(reference => reference.Locations.Select(location => ReferenceItem.FromReferenceLocation(reference.Definition, location)))
            .OrderBy(location => location.Path, StringComparer.Ordinal)
            .ThenBy(location => location.Line)
            .ThenBy(location => location.Column)
            .Take(maxResults)
            .ToArray();

        var totalCount = references.Sum(reference => reference.Locations.Count());

        return new ReferencesResult(
            target.Document,
            target.Line,
            target.Column,
            target.Selector,
            SymbolItem.FromSymbol(sourceSymbol, GetProjectName(sourceSymbol, loaded.Solution)),
            totalCount,
            locations.Length,
            totalCount > locations.Length,
            locations,
            loaded.WorkspaceDiagnostics);
    }

    private static async Task<object> ImplementationsAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var maxResults = command.OptionalInt("max-results", 200, 1);
        var target = await ResolveCommandSymbolAsync(command, loaded, cancellationToken).ConfigureAwait(false);
        var sourceSymbol = await SymbolFinder.FindSourceDefinitionAsync(target.Symbol, loaded.Solution, cancellationToken).ConfigureAwait(false) ?? target.Symbol;
        var implementations = await SymbolFinder.FindImplementationsAsync(sourceSymbol, loaded.Solution, cancellationToken: cancellationToken).ConfigureAwait(false);

        var resolvedSymbols = new List<ISymbol>();
        foreach (var implementation in implementations)
        {
            resolvedSymbols.Add(await SymbolFinder.FindSourceDefinitionAsync(implementation, loaded.Solution, cancellationToken).ConfigureAwait(false) ?? implementation);
        }

        var symbols = resolvedSymbols
            .Where(RoslynSymbolSearch.IsCodeSymbol)
            .Select(implementation => SymbolItem.FromSymbol(implementation, GetProjectName(implementation, loaded.Solution)))
            .Where(symbolItem => symbolItem.Declarations.Count > 0)
            .DistinctBy(symbolItem => string.Concat(symbolItem.ProjectName, "|", symbolItem.Kind, "|", symbolItem.DisplayName, "|", symbolItem.PrimaryLocation?.Path, "|", symbolItem.PrimaryLocation?.Line, "|", symbolItem.PrimaryLocation?.Column))
            .ToArray();

        var ordered = symbols
            .OrderBy(symbolItem => symbolItem.DisplayName, StringComparer.Ordinal)
            .ThenBy(symbolItem => symbolItem.Kind, StringComparer.Ordinal)
            .ThenBy(symbolItem => symbolItem.PrimaryLocation?.Path, StringComparer.Ordinal)
            .ThenBy(symbolItem => symbolItem.PrimaryLocation?.Line)
            .Take(maxResults)
            .ToArray();

        return new ImplementationsResult(
            target.Document,
            target.Line,
            target.Column,
            target.Selector,
            SymbolItem.FromSymbol(sourceSymbol, GetProjectName(sourceSymbol, loaded.Solution)),
            symbols.Length,
            ordered.Length,
            symbols.Length > ordered.Length,
            ordered,
            loaded.WorkspaceDiagnostics);
    }

    /// <summary>
    /// Returns quick-info tags, formatted sections, and the resolved span for the requested document position.
    /// </summary>
    private static async Task<object> QuickInfoAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var context = await ResolveSemanticDocumentAsync(command, loaded, cancellationToken).ConfigureAwait(false);
        var document = context.Document!;
        var line = command.OptionalInt("line", 1, 1);
        var column = command.OptionalInt("column", 1, 1);
        var position = await PositionResolver.GetPositionAsync(document, line, column, command.Name, cancellationToken).ConfigureAwait(false);
        var quickInfoService = QuickInfoService.GetService(document)
            ?? throw new InvalidOperationException("The Roslyn quick info service is unavailable for this document.");
        var quickInfo = await quickInfoService.GetQuickInfoAsync(document, position, cancellationToken).ConfigureAwait(false);
        if (quickInfo is null)
        {
            throw new CliUsageException(command.Name, $"No quick info found at line {line}, column {column} in '{context.Descriptor.Name}'.");
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return new QuickInfoResult(
            context.Descriptor,
            line,
            column,
            PositionResolver.ToDocumentRange(text, quickInfo.Span),
            quickInfo.Tags.ToArray(),
            quickInfo.Sections.Select(section => new QuickInfoSectionItem(section.Kind, section.Text)).ToArray(),
            loaded.WorkspaceDiagnostics);
    }

    private static async Task<object> SignatureHelpAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var context = await ResolveSemanticDocumentAsync(command, loaded, cancellationToken).ConfigureAwait(false);
        var document = context.Document!;
        var line = command.OptionalInt("line", 1, 1);
        var column = command.OptionalInt("column", 1, 1);
        var position = await PositionResolver.GetPositionAsync(document, line, column, command.Name, cancellationToken).ConfigureAwait(false);
        var signatureHelp = await RoslynSignatureHelpService.GetSignatureHelpAsync(document, position, cancellationToken).ConfigureAwait(false);
        if (signatureHelp is null)
        {
            throw new CliUsageException(command.Name, $"No signature help found at line {line}, column {column} in '{context.Descriptor.Name}'.");
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return new SignatureHelpResult(
            context.Descriptor,
            line,
            column,
            PositionResolver.ToDocumentRange(text, signatureHelp.ApplicableSpan),
            signatureHelp.ActiveSignature,
            signatureHelp.ActiveParameter,
            signatureHelp.Signatures,
            loaded.WorkspaceDiagnostics);
    }

    /// <summary>
    /// Returns the full declaring source blocks for one symbol resolved from a <c>--symbol</c> selector.
    /// </summary>
    private static async Task<object> SymbolSourceAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var selector = command.Required("symbol");
        var symbol = await RoslynSymbolResolver.ResolveAsync(loaded.Solution, selector, command.Name, cancellationToken).ConfigureAwait(false);
        var sourceSymbol = await SymbolFinder.FindSourceDefinitionAsync(symbol, loaded.Solution, cancellationToken).ConfigureAwait(false) ?? symbol;

        var syntaxReferences = new List<SyntaxReference>(sourceSymbol.DeclaringSyntaxReferences);
        if (sourceSymbol is IMethodSymbol { PartialImplementationPart: { } implementationPart })
        {
            syntaxReferences.AddRange(implementationPart.DeclaringSyntaxReferences);
        }

        var declarations = new List<SymbolSourceDeclaration>();
        var seenSpans = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in syntaxReferences)
        {
            var node = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
            var context = await ResolveDeclaringDocumentAsync(loaded, node.SyntaxTree, cancellationToken).ConfigureAwait(false);
            if (context is null)
            {
                continue;
            }

            var text = await node.SyntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var spanKey = string.Concat(context.Descriptor.Path ?? context.Descriptor.DocumentKey, "|", node.Span.Start, "|", node.Span.Length);
            if (!seenSpans.Add(spanKey))
            {
                continue;
            }

            declarations.Add(new SymbolSourceDeclaration(
                context.Descriptor,
                PositionResolver.ToDocumentRange(text, node.Span),
                text.ToString(node.Span)));
        }

        if (declarations.Count == 0)
        {
            throw new CliUsageException(command.Name, $"Symbol '{selector}' has no source declarations in the loaded target.");
        }

        var ordered = declarations
            .OrderBy(declaration => declaration.Document.Path is null)
            .ThenBy(declaration => declaration.Document.Path, StringComparer.Ordinal)
            .ThenBy(declaration => declaration.Range.Line)
            .ThenBy(declaration => declaration.Range.Column)
            .ThenBy(declaration => declaration.Document.DocumentKey, StringComparer.Ordinal)
            .ToArray();

        return new SymbolSourceResult(
            loaded.TargetPath,
            selector,
            SymbolItem.FromSymbol(sourceSymbol, GetProjectName(sourceSymbol, loaded.Solution)),
            ordered,
            loaded.WorkspaceDiagnostics);
    }
}
