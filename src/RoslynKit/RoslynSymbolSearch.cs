using Microsoft.CodeAnalysis;

namespace RoslynKit;

/// <summary>
/// Provides deterministic source symbol enumeration and filtering for the <c>symbols</c> and <c>document-symbols</c> commands.
/// </summary>
public static class RoslynSymbolSearch
{
    /// <summary>
    /// Recursively walks namespace and type members to yield source-declared symbols in deterministic order.
    /// </summary>
    public static IEnumerable<ISymbol> EnumerateSourceSymbols(INamespaceOrTypeSymbol root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (root is INamespaceSymbol namespaceSymbol)
        {
            foreach (var namespaceMember in namespaceSymbol.GetNamespaceMembers().OrderBy(member => member.Name, StringComparer.Ordinal))
            {
                foreach (var symbol in EnumerateSourceSymbols(namespaceMember, cancellationToken))
                {
                    yield return symbol;
                }
            }
        }

        foreach (var typeMember in root.GetTypeMembers().OrderBy(member => member.MetadataName, StringComparer.Ordinal))
        {
            foreach (var symbol in EnumerateTypeAndMembers(typeMember, cancellationToken))
            {
                yield return symbol;
            }
        }
    }

    /// <summary>
    /// Classifies Roslyn symbols that RoslynKit can expose as source navigation results.
    /// </summary>
    public static bool IsCodeSymbol(ISymbol symbol)
    {
        return symbol.Kind is SymbolKind.NamedType
            or SymbolKind.Method
            or SymbolKind.Property
            or SymbolKind.Field
            or SymbolKind.Event
            or SymbolKind.Namespace;
    }

    /// <summary>
    /// Filters document outlines to source-authored declarations that are useful navigation targets.
    /// </summary>
    public static bool IsDocumentSymbol(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared || symbol.Kind == SymbolKind.Namespace || IsConstructor(symbol))
        {
            return false;
        }

        if (symbol is IMethodSymbol { AssociatedSymbol: not null })
        {
            return false;
        }

        return IsCodeSymbol(symbol);
    }

    /// <summary>
    /// Identifies constructors so document outlines and symbol lists can avoid noisy constructor-only hops.
    /// </summary>
    public static bool IsConstructor(ISymbol symbol)
    {
        return symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor };
    }

    private static IEnumerable<ISymbol> EnumerateTypeAndMembers(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (HasSourceDeclaration(type))
        {
            yield return type;
        }

        foreach (var member in type.GetMembers().OrderBy(member => member.MetadataName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (member is INamedTypeSymbol nestedType)
            {
                foreach (var nestedSymbol in EnumerateTypeAndMembers(nestedType, cancellationToken))
                {
                    yield return nestedSymbol;
                }

                continue;
            }

            if (IsCodeSymbol(member) && HasSourceDeclaration(member))
            {
                yield return member;
            }
        }
    }

    private static bool HasSourceDeclaration(ISymbol symbol)
    {
        return symbol.Locations.Any(location => location.IsInSource);
    }
}
