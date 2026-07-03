using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;

namespace RoslynKit;

/// <summary>
/// Executes parsed RoslynKit semantic commands by loading workspaces, resolving documents, and projecting command results.
/// </summary>
public static class RoslynCommandExecutor
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
    /// Dispatches a parsed command name to the corresponding Roslyn-backed command handler.
    /// </summary>
    public static async Task<object> ExecuteAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        return command.Name switch
        {
            "diagnostics" => await DiagnosticsAsync(command, cancellationToken).ConfigureAwait(false),
            "definition" => await DefinitionAsync(command, cancellationToken).ConfigureAwait(false),
            "document-symbols" => await DocumentSymbolsAsync(command, cancellationToken).ConfigureAwait(false),
            "document-text" => await DocumentTextAsync(command, cancellationToken).ConfigureAwait(false),
            "implementations" => await ImplementationsAsync(command, cancellationToken).ConfigureAwait(false),
            "quick-info" => await QuickInfoAsync(command, cancellationToken).ConfigureAwait(false),
            "references" => await ReferencesAsync(command, cancellationToken).ConfigureAwait(false),
            "signature-help" => await SignatureHelpAsync(command, cancellationToken).ConfigureAwait(false),
            "symbol-source" => await SymbolSourceAsync(command, cancellationToken).ConfigureAwait(false),
            "symbols" => await SymbolsAsync(command, cancellationToken).ConfigureAwait(false),
            "type-definition" => await TypeDefinitionAsync(command, cancellationToken).ConfigureAwait(false),
            "workspace" => await WorkspaceAsync(command, cancellationToken).ConfigureAwait(false),
            _ => throw new CliUsageException(command.Name, $"Unknown command '{command.Name}'."),
        };
    }

    private static async Task<object> WorkspaceAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
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

    private static async Task<object> DiagnosticsAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var maxResults = command.OptionalInt("max-results", 200, 1);
        var includeGenerated = command.Flag("include-generated");
        var includeHidden = command.Flag("include-hidden");
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
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

    private static async Task<object> SymbolsAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var query = command.Required("query");
        var maxResults = command.OptionalInt("max-results", 200, 1);
        var exact = command.Flag("exact");
        var caseSensitive = command.Flag("case-sensitive");
        var kind = command.Optional("kind");
        var symbolFilter = GetSymbolFilter(command.Name, kind);
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
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

    private static async Task<object> DocumentTextAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
        var context = await loaded.FindTextDocumentAsync(command.Optional("file"), command.Optional("document-key"), command.Name, cancellationToken).ConfigureAwait(false);
        var text = await context.TextDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var resolvedRange = PositionResolver.ToDocumentRange(text, new TextSpan(0, text.Length));

        return new DocumentTextResult(
            context.Descriptor,
            resolvedRange,
            text.ToString(),
            truncated: false,
            loaded.WorkspaceDiagnostics);
    }

    private static async Task<object> DocumentSymbolsAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
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
    private static async Task<object> DefinitionAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
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

    private static async Task<object> TypeDefinitionAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
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

    private static async Task<object> ReferencesAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var maxResults = command.OptionalInt("max-results", 200, 1);
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
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

    private static async Task<object> ImplementationsAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var maxResults = command.OptionalInt("max-results", 200, 1);
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
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
    private static async Task<object> QuickInfoAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
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

    private static async Task<object> SignatureHelpAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
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
    private static async Task<object> SymbolSourceAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var selector = command.Required("symbol");
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Resolves the workspace document for a declaring syntax tree, falling back to source-generated documents when the tree has no regular document.
    /// </summary>
    private static async Task<WorkspaceDocumentContext?> ResolveDeclaringDocumentAsync(RoslynWorkspaceLoader loaded, SyntaxTree tree, CancellationToken cancellationToken)
    {
        if (loaded.Solution.GetDocument(tree) is { } document)
        {
            var documentKind = document is SourceGeneratedDocument ? DocumentKindNames.SourceGenerated : DocumentKindNames.Source;
            return await WorkspaceDocumentContext.CreateAsync(loaded, document, documentKind, cancellationToken).ConfigureAwait(false);
        }

        foreach (var project in loaded.Solution.Projects
                     .OrderBy(project => project.Name, StringComparer.Ordinal)
                     .ThenBy(project => project.FilePath, StringComparer.Ordinal))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null || !compilation.ContainsSyntaxTree(tree))
            {
                continue;
            }

            foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await generated.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false) == tree)
                {
                    return await WorkspaceDocumentContext.CreateAsync(loaded, generated, DocumentKindNames.SourceGenerated, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return null;
    }

    private sealed record ResolvedSymbolTarget(ISymbol Symbol, DocumentDescriptor? Document, int? Line, int? Column, string? Selector);

    /// <summary>
    /// Resolves the command's symbol from a <c>--symbol</c> selector when present, otherwise from the requested document position.
    /// </summary>
    private static async Task<ResolvedSymbolTarget> ResolveCommandSymbolAsync(ParsedCommand command, RoslynWorkspaceLoader loaded, CancellationToken cancellationToken)
    {
        var selector = command.Optional("symbol");
        if (selector is not null)
        {
            var symbol = await RoslynSymbolResolver.ResolveAsync(loaded.Solution, selector, command.Name, cancellationToken).ConfigureAwait(false);
            return new ResolvedSymbolTarget(symbol, null, null, null, selector);
        }

        var context = await ResolveSemanticDocumentAsync(command, loaded, cancellationToken).ConfigureAwait(false);
        var positionSymbol = await SymbolAtPositionAsync(command, context.Document!, cancellationToken).ConfigureAwait(false);
        return new ResolvedSymbolTarget(
            positionSymbol,
            context.Descriptor,
            command.OptionalInt("line", 1, 1),
            command.OptionalInt("column", 1, 1),
            Selector: null);
    }

    /// <summary>
    /// Resolves the requested document selector and rejects non-C# or non-semantic documents before symbol commands run.
    /// </summary>
    private static async Task<WorkspaceDocumentContext> ResolveSemanticDocumentAsync(ParsedCommand command, RoslynWorkspaceLoader loaded, CancellationToken cancellationToken)
    {
        var context = await loaded.FindTextDocumentAsync(command.Optional("file"), command.Optional("document-key"), command.Name, cancellationToken).ConfigureAwait(false);
        if (context.Document is null || !RoslynDocumentFilters.IsSemanticDocument(context.Document, context.DocumentKind))
        {
            throw new CliUsageException(command.Name, $"Command '{command.Name}' only supports C# source and source-generated documents.");
        }

        return context;
    }

    /// <summary>
    /// Resolves the symbol at the requested position, falling back to syntax and semantic-model lookup when direct lookup fails.
    /// </summary>
    private static async Task<ISymbol> SymbolAtPositionAsync(ParsedCommand command, Document document, CancellationToken cancellationToken)
    {
        var line = command.OptionalInt("line", 1, 1);
        var column = command.OptionalInt("column", 1, 1);
        var position = await PositionResolver.GetPositionAsync(document, line, column, command.Name, cancellationToken).ConfigureAwait(false);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken).ConfigureAwait(false);
        if (symbol is not null)
        {
            return symbol;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CliUsageException(command.Name, $"Could not parse '{document.Name}'.");
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CliUsageException(command.Name, $"Could not create semantic model for '{document.Name}'.");
        var token = root.FindToken(position, findInsideTrivia: true);

        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            symbol = model.GetSymbolInfo(node, cancellationToken).Symbol ?? model.GetDeclaredSymbol(node, cancellationToken);
            if (symbol is not null)
            {
                return symbol;
            }
        }

        throw new CliUsageException(command.Name, $"No symbol found at line {line}, column {column} in '{document.Name}'.");
    }

    private static bool SymbolMatches(ISymbol symbol, string query, StringComparison comparison)
    {
        return symbol.Name.Contains(query, comparison)
            || symbol.MetadataName.Contains(query, comparison)
            || (!RoslynSymbolSearch.IsConstructor(symbol) && symbol.ToDisplayString(SymbolDisplayFormats.Qualified).Contains(query, comparison));
    }

    private static SymbolFilter GetSymbolFilter(string commandName, string? kind)
    {
        return kind switch
        {
            null => SymbolFilter.All,
            "namespace" => SymbolFilter.Namespace,
            "type" or "class" or "interface" or "struct" or "enum" or "delegate" => SymbolFilter.Type,
            "member" or "method" or "property" or "field" or "event" => SymbolFilter.Member,
            _ => throw new CliUsageException(commandName, $"Unknown symbol kind '{kind}'. Supported values: {string.Join(", ", SupportedSymbolKinds)}."),
        };
    }

    private static bool IsSpecificSymbolKindMatch(ISymbol symbol, string? kind)
    {
        return kind switch
        {
            null => true,
            "namespace" => symbol.Kind == SymbolKind.Namespace,
            "type" => symbol is ITypeSymbol,
            "member" => RoslynSymbolSearch.IsCodeSymbol(symbol) && symbol.Kind is not SymbolKind.Namespace and not SymbolKind.NamedType,
            "method" => symbol.Kind == SymbolKind.Method && !RoslynSymbolSearch.IsConstructor(symbol),
            "property" => symbol.Kind == SymbolKind.Property,
            "field" => symbol.Kind == SymbolKind.Field,
            "event" => symbol.Kind == SymbolKind.Event,
            "class" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Class },
            "interface" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface },
            "struct" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Struct },
            "enum" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Enum },
            "delegate" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Delegate },
            _ => false,
        };
    }

    private static ITypeSymbol? GetTypeDefinitionSymbol(ISymbol? symbol)
    {
        return symbol switch
        {
            ILocalSymbol localSymbol => localSymbol.Type,
            IFieldSymbol fieldSymbol => fieldSymbol.Type,
            IPropertySymbol propertySymbol => propertySymbol.Type,
            IParameterSymbol parameterSymbol => parameterSymbol.Type,
            IAliasSymbol aliasSymbol => aliasSymbol.Target as ITypeSymbol,
            ITypeSymbol typeSymbol => typeSymbol,
            _ => null,
        };
    }

    private static string GetProjectName(ISymbol symbol, Solution solution)
    {
        foreach (var location in symbol.Locations.Where(location => location.IsInSource))
        {
            if (location.SourceTree is not null && solution.GetDocument(location.SourceTree) is { } document)
            {
                return document.Project.Name;
            }
        }

        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            if (solution.GetDocument(reference.SyntaxTree) is { } document)
            {
                return document.Project.Name;
            }
        }

        return symbol.ContainingAssembly?.Name ?? string.Empty;
    }
}
