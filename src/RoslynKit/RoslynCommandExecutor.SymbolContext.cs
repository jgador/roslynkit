using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynKit;

/// <summary>
/// Builds bounded syntax, semantic, and ordinary-comment context for one source symbol.
/// </summary>
public static partial class RoslynCommandExecutor
{
    private const int MaximumSymbolContextCommentCharacters = 4_000;

    private static async Task<object> SymbolContextAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var maxResults = command.OptionalInt("max-results", 20, 1);
        var maxComments = command.OptionalInt("max-comments", 3, 1);
        var target = await ResolveCommandSymbolAsync(command, loaded, cancellationToken).ConfigureAwait(false);
        var sourceSymbol = await SymbolFinder.FindSourceDefinitionAsync(target.Symbol, loaded.Solution, cancellationToken).ConfigureAwait(false) ?? target.Symbol;
        var primary = await ResolvePrimaryDeclarationAsync(command, sourceSymbol, loaded, cancellationToken).ConfigureAwait(false);
        var selected = target.Document is null
            ? new SymbolContextSelectedNode(primary.Node, primary.Model)
            : await ResolveSelectedNodeAsync(command, loaded, target.Document, cancellationToken).ConfigureAwait(false);
        var symbol = SymbolItem.FromSymbol(sourceSymbol, GetProjectName(sourceSymbol, loaded.Solution));
        var alternateDeclarations = await GetAlternateDeclarationsAsync(sourceSymbol, primary.Node, cancellationToken).ConfigureAwait(false);
        var ancestors = selected.Node.Ancestors()
            .Select(node => CreateSyntaxContextNode(node, selected.Model, cancellationToken))
            .ToArray();
        var descendants = CreateDescendants(primary.Node, primary.Model, cancellationToken)
            .OrderBy(descendant => descendant.Location.Path, StringComparer.Ordinal)
            .ThenBy(descendant => descendant.Location.Line)
            .ThenBy(descendant => descendant.Location.Column)
            .ThenBy(descendant => descendant.Location.EndLine)
            .ThenBy(descendant => descendant.Location.EndColumn)
            .ThenBy(descendant => descendant.Relation, StringComparer.Ordinal)
            .ThenBy(descendant => descendant.TargetSymbolId, StringComparer.Ordinal)
            .ToArray();
        var comments = CreateDeclarationComments(primary.Node, primary.Model, cancellationToken)
            .OrderBy(comment => comment.Location.Path, StringComparer.Ordinal)
            .ThenBy(comment => comment.Location.Line)
            .ThenBy(comment => comment.Location.Column)
            .ThenBy(comment => comment.Placement, StringComparer.Ordinal)
            .ToArray();

        return new SymbolContextResult(
            target.Document,
            target.Line,
            target.Column,
            target.Selector,
            CreateSyntaxContextNode(selected.Node, selected.Model, cancellationToken, target.Symbol),
            symbol,
            symbol.Documentation,
            alternateDeclarations,
            ancestors,
            descendants.Length,
            Math.Min(maxResults, descendants.Length),
            descendants.Length > maxResults,
            descendants.Take(maxResults).ToArray(),
            comments.Length,
            Math.Min(maxComments, comments.Length),
            comments.Length > maxComments,
            comments.Take(maxComments).ToArray(),
            loaded.WorkspaceDiagnostics);
    }

    private static async Task<SymbolContextPrimaryDeclaration> ResolvePrimaryDeclarationAsync(
        ParsedCommand command,
        ISymbol sourceSymbol,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var declarations = new List<SymbolContextPrimaryDeclaration>();
        foreach (var syntaxReference in GetDeclarationSyntaxReferences(sourceSymbol))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = await syntaxReference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
            var context = await ResolveDeclaringDocumentAsync(loaded, node.SyntaxTree, cancellationToken).ConfigureAwait(false);
            if (context?.Document is null || !RoslynDocumentFilters.IsSemanticDocument(context.Document, context.DocumentKind))
            {
                continue;
            }

            var model = await context.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                continue;
            }

            declarations.Add(new SymbolContextPrimaryDeclaration(node, context, model));
        }

        if (declarations.Count == 0)
        {
            var selector = command.Optional("symbol") ?? sourceSymbol.ToDisplayString(SymbolDisplayFormats.Qualified);
            throw new CliUsageException(command.Name, $"Symbol '{selector}' has no C# source declaration in the loaded target.");
        }

        return declarations
            .OrderBy(declaration => declaration.Context.Descriptor.Path is null)
            .ThenBy(declaration => declaration.Context.Descriptor.Path, StringComparer.Ordinal)
            .ThenBy(declaration => declaration.Node.SpanStart)
            .ThenBy(declaration => declaration.Node.Span.Length)
            .ThenBy(declaration => declaration.Context.Descriptor.DocumentKey, StringComparer.Ordinal)
            .First();
    }

    private static async Task<SymbolContextSelectedNode> ResolveSelectedNodeAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        DocumentDescriptor document,
        CancellationToken cancellationToken)
    {
        var context = await ResolveSemanticDocumentAsync(command, loaded, cancellationToken).ConfigureAwait(false);
        var sourceDocument = context.Document
            ?? throw new CliUsageException(command.Name, $"Could not resolve '{document.Name}'.");
        var root = await sourceDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CliUsageException(command.Name, $"Could not parse '{document.Name}'.");
        var position = await PositionResolver.GetPositionAsync(
            sourceDocument,
            command.OptionalInt("line", 1, 1),
            command.OptionalInt("column", 1, 1),
            command.Name,
            cancellationToken).ConfigureAwait(false);
        var model = await sourceDocument.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CliUsageException(command.Name, $"Could not create semantic model for '{document.Name}'.");
        return new SymbolContextSelectedNode(root.FindToken(position, findInsideTrivia: true).Parent ?? root, model);
    }

    private static async Task<IReadOnlyList<SourceRange>> GetAlternateDeclarationsAsync(
        ISymbol sourceSymbol,
        SyntaxNode primaryNode,
        CancellationToken cancellationToken)
    {
        var declarations = new List<SourceRange>();
        foreach (var syntaxReference in GetDeclarationSyntaxReferences(sourceSymbol))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = await syntaxReference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
            if (MatchesSyntaxNode(node, primaryNode))
            {
                continue;
            }

            declarations.Add(SourceRange.FromLocation(node.GetLocation()));
        }

        return declarations
            .DistinctBy(LocationKey)
            .OrderBy(location => location.Path, StringComparer.Ordinal)
            .ThenBy(location => location.Line)
            .ThenBy(location => location.Column)
            .ThenBy(location => location.EndLine)
            .ThenBy(location => location.EndColumn)
            .ToArray();
    }

    private static SyntaxContextNode CreateSyntaxContextNode(
        SyntaxNode node,
        SemanticModel model,
        CancellationToken cancellationToken,
        ISymbol? fallbackSymbol = null)
    {
        var symbol = model.GetDeclaredSymbol(node, cancellationToken)
            ?? GetReferencedSymbol(model, node, cancellationToken)
            ?? fallbackSymbol;
        return new SyntaxContextNode(
            SyntaxKindText(node),
            SourceRange.FromLocation(node.GetLocation()),
            symbol?.ToDisplayString(SymbolDisplayFormats.Qualified),
            CreateSymbolId(symbol));
    }

    private static IReadOnlyList<SyntaxReference> GetDeclarationSyntaxReferences(ISymbol sourceSymbol)
    {
        var references = new List<SyntaxReference>(sourceSymbol.DeclaringSyntaxReferences);
        if (sourceSymbol is IMethodSymbol { PartialDefinitionPart: { } definitionPart })
        {
            references.AddRange(definitionPart.DeclaringSyntaxReferences);
        }

        if (sourceSymbol is IMethodSymbol { PartialImplementationPart: { } implementationPart })
        {
            references.AddRange(implementationPart.DeclaringSyntaxReferences);
        }

        return references
            .DistinctBy(reference => string.Concat(reference.SyntaxTree.FilePath, "|", reference.Span.Start, "|", reference.Span.Length))
            .ToArray();
    }

    private static IEnumerable<SymbolContextDescendant> CreateDescendants(
        SyntaxNode declaration,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in declaration.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descendant = CreateDescendant(node, declaration, model, cancellationToken);
            if (descendant is null || !seen.Add(DescendantKey(descendant)))
            {
                continue;
            }

            yield return descendant;
        }
    }

    private static SymbolContextDescendant? CreateDescendant(
        SyntaxNode node,
        SyntaxNode declaration,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        string? relation = null;
        ISymbol? target = null;

        var declaredSymbol = model.GetDeclaredSymbol(node, cancellationToken);
        if (declaredSymbol is not null && !declaredSymbol.IsImplicitlyDeclared)
        {
            relation = "declaration";
            target = declaredSymbol;
        }
        else
        {
            switch (node)
            {
                case InvocationExpressionSyntax:
                    relation = "invocation";
                    target = GetReferencedSymbol(model, node, cancellationToken);
                    break;

                case ObjectCreationExpressionSyntax:
                case ImplicitObjectCreationExpressionSyntax:
                    relation = "construction";
                    target = GetReferencedSymbol(model, node, cancellationToken);
                    break;

                case MemberAccessExpressionSyntax:
                case MemberBindingExpressionSyntax:
                    relation = "member-reference";
                    target = GetReferencedSymbol(model, node, cancellationToken);
                    break;

                case IdentifierNameSyntax identifier when IsStandaloneMemberReference(identifier):
                    target = GetReferencedSymbol(model, node, cancellationToken);
                    if (target is { Kind: SymbolKind.Method or SymbolKind.Property or SymbolKind.Field or SymbolKind.Event })
                    {
                        relation = "member-reference";
                    }

                    break;
            }
        }

        if (relation is null)
        {
            return null;
        }

        return new SymbolContextDescendant(
            relation,
            SyntaxDepth(node, declaration),
            SyntaxKindText(node),
            SourceRange.FromLocation(node.GetLocation()),
            target?.ToDisplayString(SymbolDisplayFormats.Qualified),
            CreateSymbolId(target));
    }

    private static ISymbol? GetReferencedSymbol(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        var symbolInfo = model.GetSymbolInfo(node, cancellationToken);
        return symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
    }

    private static bool IsStandaloneMemberReference(IdentifierNameSyntax identifier)
    {
        return identifier.Parent is not MemberAccessExpressionSyntax
            && identifier.Parent is not MemberBindingExpressionSyntax
            && identifier.Parent is not InvocationExpressionSyntax;
    }

    private static int SyntaxDepth(SyntaxNode node, SyntaxNode declaration)
    {
        var depth = 1;
        for (var ancestor = node.Parent; ancestor is not null && !MatchesSyntaxNode(ancestor, declaration); ancestor = ancestor.Parent)
        {
            depth++;
        }

        return depth;
    }

    private static IEnumerable<SymbolContextComment> CreateDeclarationComments(
        SyntaxNode declaration,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var trivia = declaration.GetLeadingTrivia()
            .Concat(declaration.DescendantTrivia(descendIntoTrivia: true))
            .Concat(declaration.GetTrailingTrivia());
        foreach (var item in trivia)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.IsKind(SyntaxKind.SingleLineCommentTrivia) && !item.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                continue;
            }

            var owner = FindNearestDeclarationOwner(item, model, cancellationToken);
            if (owner is null || !MatchesSyntaxNode(owner, declaration))
            {
                continue;
            }

            var text = NormalizeCommentText(item.ToFullString());
            if (text is null)
            {
                continue;
            }

            var location = SourceRange.FromLocation(item.GetLocation());
            var key = LocationKey(location);
            if (!seen.Add(key))
            {
                continue;
            }

            yield return new SymbolContextComment(
                CommentPlacement(declaration, item),
                item.IsKind(SyntaxKind.SingleLineCommentTrivia) ? "line" : "block",
                location,
                text);
        }
    }

    private static SyntaxNode? FindNearestDeclarationOwner(
        SyntaxTrivia trivia,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        for (var node = trivia.Token.Parent; node is not null; node = node.Parent)
        {
            if (model.GetDeclaredSymbol(node, cancellationToken) is not null)
            {
                return node;
            }
        }

        return null;
    }

    private static string CommentPlacement(SyntaxNode declaration, SyntaxTrivia trivia)
    {
        if (declaration.GetLeadingTrivia().Any(candidate => candidate.FullSpan == trivia.FullSpan))
        {
            return "leading";
        }

        return declaration.GetTrailingTrivia().Any(candidate => candidate.FullSpan == trivia.FullSpan)
            ? "trailing"
            : "body";
    }

    private static string? NormalizeCommentText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var withoutDelimiters = value
            .Replace("//", " ", StringComparison.Ordinal)
            .Replace("/*", " ", StringComparison.Ordinal)
            .Replace("*/", " ", StringComparison.Ordinal);
        var builder = new System.Text.StringBuilder(withoutDelimiters.Length);
        var pendingSpace = false;
        foreach (var character in withoutDelimiters)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        if (builder.Length == 0)
        {
            return null;
        }

        return builder.Length <= MaximumSymbolContextCommentCharacters
            ? builder.ToString()
            : builder.ToString(0, MaximumSymbolContextCommentCharacters);
    }

    private static string? CreateSymbolId(ISymbol? symbol)
    {
        return symbol is not null && RoslynSymbolSearch.IsCodeSymbol(symbol)
            ? DocumentationCommentId.CreateDeclarationId(symbol)
            : null;
    }

    private static bool MatchesSyntaxNode(SyntaxNode left, SyntaxNode right)
    {
        return left.SyntaxTree == right.SyntaxTree && left.Span == right.Span;
    }

    private static string SyntaxKindText(SyntaxNode node)
    {
        return node.Language == LanguageNames.CSharp
            ? ((Microsoft.CodeAnalysis.CSharp.SyntaxKind)node.RawKind).ToString()
            : node.RawKind.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string DescendantKey(SymbolContextDescendant descendant)
    {
        return string.Concat(
            descendant.Relation, "|",
            LocationKey(descendant.Location), "|",
            descendant.TargetSymbolId, "|",
            descendant.TargetDisplayName);
    }

    private static string LocationKey(SourceRange location)
    {
        return string.Concat(
            location.Path, "|",
            location.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), "|",
            location.Column.ToString(System.Globalization.CultureInfo.InvariantCulture), "|",
            location.EndLine.ToString(System.Globalization.CultureInfo.InvariantCulture), "|",
            location.EndColumn.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed record SymbolContextPrimaryDeclaration(
        SyntaxNode Node,
        WorkspaceDocumentContext Context,
        SemanticModel Model);

    private sealed record SymbolContextSelectedNode(SyntaxNode Node, SemanticModel Model);
}
