namespace RoslynKit;

/// <summary>
/// Represents the <c>symbol-context</c> command payload that joins a local syntax node to its resolved symbol and bounded semantic context.
/// </summary>
public sealed class SymbolContextResult
{
    public SymbolContextResult(
        DocumentDescriptor? document,
        int? line,
        int? column,
        string? selector,
        SyntaxContextNode selectedNode,
        SymbolItem symbol,
        string? documentation,
        IReadOnlyList<SourceRange> alternateDeclarations,
        IReadOnlyList<SyntaxContextNode> ancestors,
        int totalDescendantCount,
        int returnedDescendantCount,
        bool descendantsTruncated,
        IReadOnlyList<SymbolContextDescendant> descendants,
        int totalCommentCount,
        int returnedCommentCount,
        bool commentsTruncated,
        IReadOnlyList<SymbolContextComment> comments,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        Document = document;
        Line = line;
        Column = column;
        Selector = selector;
        SelectedNode = selectedNode;
        Symbol = symbol;
        Documentation = documentation;
        AlternateDeclarations = alternateDeclarations;
        Ancestors = ancestors;
        TotalDescendantCount = totalDescendantCount;
        ReturnedDescendantCount = returnedDescendantCount;
        DescendantsTruncated = descendantsTruncated;
        Descendants = descendants;
        TotalCommentCount = totalCommentCount;
        ReturnedCommentCount = returnedCommentCount;
        CommentsTruncated = commentsTruncated;
        Comments = comments;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    /// <summary>
    /// Document descriptor for position-mode lookups, or <c>null</c> when a symbol selector was used.
    /// </summary>
    public DocumentDescriptor? Document { get; }

    /// <summary>
    /// One-based source line for position-mode lookups, or <c>null</c> for selector-mode lookups.
    /// </summary>
    public int? Line { get; }

    /// <summary>
    /// One-based source column for position-mode lookups, or <c>null</c> for selector-mode lookups.
    /// </summary>
    public int? Column { get; }

    /// <summary>
    /// Symbol selector used for selector-mode lookups, or <c>null</c> for position-mode lookups.
    /// </summary>
    public string? Selector { get; }

    /// <summary>
    /// Local syntax node selected by the command position, or the primary declaration for selector mode.
    /// </summary>
    public SyntaxContextNode SelectedNode { get; }

    /// <summary>
    /// Source symbol resolved from the selected node or selector.
    /// </summary>
    public SymbolItem Symbol { get; }

    /// <summary>
    /// Plain-text XML summary documentation for the resolved symbol, kept separate from ordinary comments.
    /// </summary>
    public string? Documentation { get; }

    /// <summary>
    /// Additional source declaration locations for partial or multi-declaration symbols, excluding the primary declaration.
    /// </summary>
    public IReadOnlyList<SourceRange> AlternateDeclarations { get; }

    /// <summary>
    /// Syntax ancestors of <see cref="SelectedNode"/>, nearest first.
    /// </summary>
    public IReadOnlyList<SyntaxContextNode> Ancestors { get; }

    /// <summary>
    /// Number of distinct semantic descendants before the result limit is applied.
    /// </summary>
    public int TotalDescendantCount { get; }

    /// <summary>
    /// Number of semantic descendants returned in <see cref="Descendants"/>.
    /// </summary>
    public int ReturnedDescendantCount { get; }

    /// <summary>
    /// Indicates whether semantic descendants were omitted because of <c>--max-results</c>.
    /// </summary>
    public bool DescendantsTruncated { get; }

    /// <summary>
    /// Bounded semantic descendants from the primary declaration.
    /// </summary>
    public IReadOnlyList<SymbolContextDescendant> Descendants { get; }

    /// <summary>
    /// Number of declaration-owned ordinary comments before the comment limit is applied.
    /// </summary>
    public int TotalCommentCount { get; }

    /// <summary>
    /// Number of declaration-owned ordinary comments returned in <see cref="Comments"/>.
    /// </summary>
    public int ReturnedCommentCount { get; }

    /// <summary>
    /// Indicates whether ordinary comments were omitted because of <c>--max-comments</c>.
    /// </summary>
    public bool CommentsTruncated { get; }

    /// <summary>
    /// Bounded ordinary comments owned by the primary declaration.
    /// </summary>
    public IReadOnlyList<SymbolContextComment> Comments { get; }

    /// <summary>
    /// Non-fatal workspace load diagnostics emitted while opening the target.
    /// </summary>
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }
}

/// <summary>
/// Represents one source syntax node in a symbol-context result.
/// </summary>
public sealed class SyntaxContextNode
{
    public SyntaxContextNode(string kind, SourceRange location, string? symbolDisplayName, string? symbolId)
    {
        Kind = kind;
        Location = location;
        SymbolDisplayName = symbolDisplayName;
        SymbolId = symbolId;
    }

    /// <summary>
    /// C# syntax kind reported by Roslyn.
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// Source range occupied by the syntax node.
    /// </summary>
    public SourceRange Location { get; }

    /// <summary>
    /// Roslyn display name of the declaration or reference symbol associated with this node, when one is available.
    /// </summary>
    public string? SymbolDisplayName { get; }

    /// <summary>
    /// Documentation-comment ID for the declaration or reference symbol associated with this node, when Roslyn can create one.
    /// </summary>
    public string? SymbolId { get; }
}

/// <summary>
/// Represents one semantic descendant discovered from the primary declaration syntax tree.
/// </summary>
public sealed class SymbolContextDescendant
{
    public SymbolContextDescendant(
        string relation,
        int depth,
        string syntaxKind,
        SourceRange location,
        string? targetDisplayName,
        string? targetSymbolId)
    {
        Relation = relation;
        Depth = depth;
        SyntaxKind = syntaxKind;
        Location = location;
        TargetDisplayName = targetDisplayName;
        TargetSymbolId = targetSymbolId;
    }

    /// <summary>
    /// Semantic relationship represented by the syntax node, such as declaration or invocation.
    /// </summary>
    public string Relation { get; }

    /// <summary>
    /// Nesting depth relative to the primary declaration.
    /// </summary>
    public int Depth { get; }

    /// <summary>
    /// C# syntax kind reported by Roslyn.
    /// </summary>
    public string SyntaxKind { get; }

    /// <summary>
    /// Source range occupied by the related syntax node.
    /// </summary>
    public SourceRange Location { get; }

    /// <summary>
    /// Roslyn display name of the resolved target symbol, when one is available.
    /// </summary>
    public string? TargetDisplayName { get; }

    /// <summary>
    /// Documentation-comment ID for the resolved target symbol, when Roslyn can create one.
    /// </summary>
    public string? TargetSymbolId { get; }
}

/// <summary>
/// Represents one normalized ordinary comment owned by a declaration.
/// </summary>
public sealed class SymbolContextComment
{
    public SymbolContextComment(string placement, string style, SourceRange location, string text)
    {
        Placement = placement;
        Style = style;
        Location = location;
        Text = text;
    }

    /// <summary>
    /// Placement relative to the owning declaration: leading, body, or trailing.
    /// </summary>
    public string Placement { get; }

    /// <summary>
    /// Comment syntax style: line or block.
    /// </summary>
    public string Style { get; }

    /// <summary>
    /// Source range occupied by the comment trivia.
    /// </summary>
    public SourceRange Location { get; }

    /// <summary>
    /// Normalized and bounded comment text without C# comment delimiters.
    /// </summary>
    public string Text { get; }
}
