using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynKit;

/// <summary>
/// Represents one source reference location returned by the <c>references</c> command.
/// </summary>
public sealed class ReferenceItem
{
    public ReferenceItem(
        string? path,
        int line,
        int column,
        int endLine,
        int endColumn,
        bool isImplicit,
        string definition)
    {
        Path = path;
        Line = line;
        Column = column;
        EndLine = endLine;
        EndColumn = endColumn;
        IsImplicit = isImplicit;
        Definition = definition;
    }

    /// <summary>
    /// Absolute source path for the reference location, when available.
    /// </summary>
    public string? Path { get; }

    /// <summary>
    /// One-based starting line for the reference span.
    /// </summary>
    public int Line { get; }

    /// <summary>
    /// One-based starting column for the reference span.
    /// </summary>
    public int Column { get; }

    /// <summary>
    /// One-based ending line for the reference span.
    /// </summary>
    public int EndLine { get; }

    /// <summary>
    /// One-based ending column for the reference span.
    /// </summary>
    public int EndColumn { get; }

    /// <summary>
    /// Indicates whether Roslyn reported the reference as implicit rather than explicit source text.
    /// </summary>
    public bool IsImplicit { get; }

    /// <summary>
    /// Fully qualified display name of the referenced definition symbol.
    /// </summary>
    public string Definition { get; }

    /// <summary>
    /// Converts a Roslyn reference location into deterministic command-output coordinates.
    /// </summary>
    public static ReferenceItem FromReferenceLocation(ISymbol definition, ReferenceLocation referenceLocation)
    {
        var location = SourceRange.FromLocation(referenceLocation.Location);
        return new ReferenceItem(
            location.Path,
            location.Line,
            location.Column,
            location.EndLine,
            location.EndColumn,
            referenceLocation.IsImplicit,
            definition.ToDisplayString(SymbolDisplayFormats.Qualified));
    }
}
