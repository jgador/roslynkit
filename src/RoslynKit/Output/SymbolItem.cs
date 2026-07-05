using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace RoslynKit;

/// <summary>
/// Represents one source-declared symbol surfaced by symbol-based RoslynKit commands.
/// </summary>
public sealed class SymbolItem
{
    public SymbolItem(
        string projectName,
        string name,
        string metadataName,
        string displayName,
        string kind,
        string accessibility,
        bool isStatic,
        string? containingType,
        string? containingNamespace,
        SourceRange? primaryLocation,
        IReadOnlyList<SourceRange> declarations,
        string? symbolId,
        string? documentation = null)
    {
        ProjectName = projectName;
        Name = name;
        MetadataName = metadataName;
        DisplayName = displayName;
        Kind = kind;
        Accessibility = accessibility;
        IsStatic = isStatic;
        ContainingType = containingType;
        ContainingNamespace = containingNamespace;
        PrimaryLocation = primaryLocation;
        Declarations = declarations;
        SymbolId = symbolId;
        Documentation = string.IsNullOrWhiteSpace(documentation) ? null : documentation;
    }

    /// <summary>
    /// Project name used to scope the symbol in command output.
    /// </summary>
    public string ProjectName { get; }

    /// <summary>
    /// Simple symbol name reported by Roslyn.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Metadata name used by Roslyn for overloads, generics, and emitted identity.
    /// </summary>
    public string MetadataName { get; }

    /// <summary>
    /// Fully qualified display name rendered with RoslynKit's deterministic symbol format.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Roslyn symbol kind projected as text.
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// Declared accessibility projected as text.
    /// </summary>
    public string Accessibility { get; }

    /// <summary>
    /// Indicates whether the Roslyn symbol is static.
    /// </summary>
    public bool IsStatic { get; }

    /// <summary>
    /// Fully qualified containing type name for member symbols.
    /// </summary>
    public string? ContainingType { get; }

    /// <summary>
    /// Fully qualified containing namespace name, excluding the global namespace.
    /// </summary>
    public string? ContainingNamespace { get; }

    /// <summary>
    /// First declaration location after RoslynKit filtering and deterministic ordering.
    /// </summary>
    public SourceRange? PrimaryLocation { get; }

    /// <summary>
    /// Declaration locations that remain after command-specific source filtering.
    /// </summary>
    public IReadOnlyList<SourceRange> Declarations { get; }

    /// <summary>
    /// Documentation-comment ID that can be reused as a symbol selector when Roslyn can create one.
    /// </summary>
    public string? SymbolId { get; }

    /// <summary>
    /// Plain-text summary documentation extracted from the Roslyn symbol's XML documentation comment.
    /// </summary>
    public string? Documentation { get; }

    /// <summary>
    /// Converts a Roslyn symbol into command-output metadata with all source declarations included.
    /// </summary>
    public static SymbolItem FromSymbol(ISymbol symbol, string projectName)
    {
        return FromSymbol(symbol, projectName, includeDeclaration: static location => location.IsInSource);
    }

    /// <summary>
    /// Converts a Roslyn symbol while keeping declaration locations only from one normalized path.
    /// </summary>
    public static SymbolItem FromSymbol(ISymbol symbol, string projectName, string? restrictDeclarationsToPath)
    {
        return FromSymbol(
            symbol,
            projectName,
            location => restrictDeclarationsToPath is null || RoslynDocumentFilters.LocationMatchesPath(location, restrictDeclarationsToPath));
    }

    /// <summary>
    /// Converts a Roslyn symbol while keeping declaration locations only from a project or solution source path set.
    /// </summary>
    public static SymbolItem FromSymbol(ISymbol symbol, string projectName, ISet<string> restrictDeclarationsToPaths)
    {
        return FromSymbol(
            symbol,
            projectName,
            location => RoslynDocumentFilters.LocationMatchesAnyPath(location, restrictDeclarationsToPaths));
    }

    /// <summary>
    /// Converts a Roslyn symbol while keeping declaration locations only from one syntax tree.
    /// </summary>
    public static SymbolItem FromSymbol(ISymbol symbol, string projectName, SyntaxTree restrictDeclarationsToSyntaxTree)
    {
        return FromSymbol(
            symbol,
            projectName,
            location => location.IsInSource && location.SourceTree == restrictDeclarationsToSyntaxTree);
    }

    private static SymbolItem FromSymbol(ISymbol symbol, string projectName, Func<Location, bool> includeDeclaration)
    {
        var declarations = symbol.Locations
            .Where(location => location.IsInSource)
            .Where(includeDeclaration)
            .Select(SourceRange.FromLocation)
            .OrderBy(location => location.Path, StringComparer.Ordinal)
            .ThenBy(location => location.Line)
            .ThenBy(location => location.Column)
            .ToArray();

        return new SymbolItem(
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
            declarations,
            RoslynSymbolSearch.IsCodeSymbol(symbol) ? DocumentationCommentId.CreateDeclarationId(symbol) : null,
            GetSummaryDocumentation(symbol));
    }

    private static string? GetSummaryDocumentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var document = XDocument.Parse(xml);
            var summary = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "summary");
            return summary is null ? null : NormalizeDocumentationText(RenderDocumentationNodes(summary.Nodes()));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string RenderDocumentationNodes(IEnumerable<XNode> nodes)
    {
        var builder = new StringBuilder();
        foreach (var node in nodes)
        {
            AppendDocumentationNode(builder, node);
        }

        return builder.ToString();
    }

    private static void AppendDocumentationNode(StringBuilder builder, XNode node)
    {
        switch (node)
        {
            case XCData cdata:
                builder.Append(cdata.Value);
                break;

            case XText text:
                builder.Append(text.Value);
                break;

            case XElement element:
                AppendDocumentationElement(builder, element);
                break;
        }
    }

    private static void AppendDocumentationElement(StringBuilder builder, XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "see":
            case "seealso":
                builder.Append(SimplifyDocumentationReference(
                    (string?)element.Attribute("cref")
                    ?? (string?)element.Attribute("langword")
                    ?? (string?)element.Attribute("href")
                    ?? element.Value));
                break;

            case "paramref":
            case "typeparamref":
                builder.Append((string?)element.Attribute("name") ?? element.Value);
                break;

            default:
                foreach (var child in element.Nodes())
                {
                    AppendDocumentationNode(builder, child);
                }

                break;
        }
    }

    private static string SimplifyDocumentationReference(string value)
    {
        if (value.Length > 2 && value[1] == ':' && value[0] is 'T' or 'M' or 'P' or 'F' or 'E' or 'N')
        {
            return value[2..];
        }

        return value.StartsWith("!:", StringComparison.Ordinal) ? value[2..] : value;
    }

    private static string? NormalizeDocumentationText(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text)
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

        return builder.Length == 0 ? null : builder.ToString();
    }
}

/// <summary>
/// Provides shared symbol display formats for deterministic RoslynKit output.
/// </summary>
public static class SymbolDisplayFormats
{
    /// <summary>
    /// Fully qualified format used for stable symbol names without global namespace prefixes.
    /// </summary>
    public static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>
    /// Fully qualified member format that includes the containing type for member identities.
    /// </summary>
    public static readonly SymbolDisplayFormat QualifiedMember = Qualified
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType);
}
