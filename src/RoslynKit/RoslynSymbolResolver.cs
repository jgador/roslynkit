using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynKit;

/// <summary>
/// Resolves a <c>--symbol</c> selector, either a documentation-comment ID or a qualified name, to one symbol in the loaded solution.
/// </summary>
public static class RoslynSymbolResolver
{
    private const int MaxCandidateIds = 20;

    /// <summary>
    /// Resolves the selector deterministically, throwing a usage error listing candidate documentation-comment IDs when the selector is ambiguous or unmatched.
    /// </summary>
    public static async Task<ISymbol> ResolveAsync(Solution solution, string selector, string commandName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new CliUsageException(commandName, "Option '--symbol' requires a non-empty selector.");
        }

        return IsDeclarationId(selector)
            ? await ResolveDeclarationIdAsync(solution, selector, commandName, cancellationToken).ConfigureAwait(false)
            : await ResolveQualifiedNameAsync(solution, selector, commandName, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsDeclarationId(string selector)
    {
        return selector.Length > 2 && selector[1] == ':' && selector[0] is 'T' or 'M' or 'P' or 'F' or 'E' or 'N';
    }

    private static async Task<ISymbol> ResolveDeclarationIdAsync(Solution solution, string selector, string commandName, CancellationToken cancellationToken)
    {
        foreach (var project in OrderedProjects(solution))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(selector, compilation);
            if (symbol is not null)
            {
                return symbol;
            }
        }

        throw new CliUsageException(commandName, $"No symbol found for '{selector}' in the loaded target.");
    }

    private static async Task<ISymbol> ResolveQualifiedNameAsync(Solution solution, string selector, string commandName, CancellationToken cancellationToken)
    {
        var name = GetLastNameSegment(selector);
        if (name.Length == 0)
        {
            throw new CliUsageException(commandName, $"No symbol found for '{selector}' in the loaded target.");
        }

        var declarations = await SymbolFinder.FindSourceDeclarationsAsync(solution, name, ignoreCase: false, SymbolFilter.All, cancellationToken).ConfigureAwait(false);
        var candidates = declarations
            .Where(RoslynSymbolSearch.IsCodeSymbol)
            .OrderBy(FirstSourcePath, StringComparer.Ordinal)
            .ThenBy(FirstSourceLine)
            .ToArray();

        var groups = candidates
            .Where(symbol => string.Equals(symbol.ToDisplayString(SymbolDisplayFormats.QualifiedMember), selector, StringComparison.Ordinal))
            .GroupBy(CandidateKey, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();

        if (groups.Length == 1)
        {
            return groups[0].First();
        }

        if (groups.Length > 1)
        {
            var candidateIds = groups.Select(group => group.Key).Take(MaxCandidateIds);
            throw new CliUsageException(
                commandName,
                $"Symbol '{selector}' is ambiguous in the loaded target. Retry with --symbol and one of: {string.Join(", ", candidateIds)}.");
        }

        if (candidates.Length > 0)
        {
            var candidateIds = candidates
                .Select(CandidateKey)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(MaxCandidateIds);
            throw new CliUsageException(
                commandName,
                $"No symbol matches '{selector}' in the loaded target. Declarations named '{name}': {string.Join(", ", candidateIds)}.");
        }

        throw new CliUsageException(commandName, $"No symbol found for '{selector}' in the loaded target.");
    }

    private static IEnumerable<Project> OrderedProjects(Solution solution)
    {
        return solution.Projects
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .ThenBy(project => project.FilePath, StringComparer.Ordinal);
    }

    private static string GetLastNameSegment(string selector)
    {
        var lastDot = selector.LastIndexOf('.');
        var segment = lastDot < 0 ? selector : selector[(lastDot + 1)..];
        var genericStart = segment.IndexOf('<');
        return genericStart < 0 ? segment : segment[..genericStart];
    }

    private static string CandidateKey(ISymbol symbol)
    {
        return DocumentationCommentId.CreateDeclarationId(symbol)
            ?? string.Concat(symbol.ToDisplayString(SymbolDisplayFormats.QualifiedMember), "|", FirstSourcePath(symbol));
    }

    private static string FirstSourcePath(ISymbol symbol)
    {
        return symbol.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree?.FilePath ?? string.Empty;
    }

    private static int FirstSourceLine(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        return location is null ? 0 : location.GetLineSpan().StartLinePosition.Line;
    }
}
