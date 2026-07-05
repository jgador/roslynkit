using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynKit;

/// <summary>
/// Provides shared symbol and document resolution helpers for Roslyn command execution.
/// </summary>
public static partial class RoslynCommandExecutor
{
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
        var context = await ResolveTextDocumentAsync(command, loaded, cancellationToken).ConfigureAwait(false);
        if (context.Document is null || !RoslynDocumentFilters.IsSemanticDocument(context.Document, context.DocumentKind))
        {
            throw new CliUsageException(command.Name, $"Command '{command.Name}' only supports C# source and source-generated documents.");
        }

        return context;
    }

    private static Task<WorkspaceDocumentContext> ResolveTextDocumentAsync(ParsedCommand command, RoslynWorkspaceLoader loaded, CancellationToken cancellationToken)
    {
        return loaded.FindTextDocumentAsync(
            command.Optional("file"),
            command.Optional("project"),
            command.Optional("tfm"),
            command.Optional("document-kind"),
            command.Name,
            cancellationToken);
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
