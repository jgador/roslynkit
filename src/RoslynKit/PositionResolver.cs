using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynKit;

/// <summary>
/// Converts one-based CLI coordinates and ranges into Roslyn positions and JSON document ranges.
/// </summary>
public static class PositionResolver
{
    /// <summary>
    /// Validates one-based CLI coordinates and converts them into a Roslyn document position.
    /// </summary>
    public static async Task<int> GetPositionAsync(TextDocument document, int oneBasedLine, int oneBasedColumn, string commandName, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return GetPosition(text, oneBasedLine, oneBasedColumn, commandName);
    }

    /// <summary>
    /// Resolves and validates the optional one-based range requested for a text document read.
    /// </summary>
    public static async Task<ResolvedTextSpan> ResolveRangeAsync(
        TextDocument document,
        int? startLine,
        int? startColumn,
        int? endLine,
        int? endColumn,
        string commandName,
        CancellationToken cancellationToken)
    {
        if (startColumn.HasValue && !startLine.HasValue)
        {
            throw new CliUsageException(commandName, "Option '--start-column' requires '--start-line'.");
        }

        if (endColumn.HasValue && !endLine.HasValue)
        {
            throw new CliUsageException(commandName, "Option '--end-column' requires '--end-line'.");
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var resolvedStartLine = startLine ?? 1;
        var resolvedStartColumn = startColumn ?? 1;
        var resolvedEndLine = endLine ?? text.Lines.Count;
        var resolvedEndColumn = endColumn ?? text.Lines[resolvedEndLine - 1].Span.Length + 1;

        var start = GetPosition(text, resolvedStartLine, resolvedStartColumn, commandName);
        var end = GetPosition(text, resolvedEndLine, resolvedEndColumn, commandName);
        if (end < start)
        {
            throw new CliUsageException(commandName, "The requested range end precedes the range start.");
        }

        var span = TextSpan.FromBounds(start, end);
        return new ResolvedTextSpan(text, span, ToDocumentRange(text, span));
    }

    public static DocumentRange ToDocumentRange(SourceText text, TextSpan span)
    {
        var lineSpan = text.Lines.GetLinePositionSpan(span);
        return new DocumentRange(
            lineSpan.Start.Line + 1,
            lineSpan.Start.Character + 1,
            lineSpan.End.Line + 1,
            lineSpan.End.Character + 1);
    }

    private static int GetPosition(SourceText text, int oneBasedLine, int oneBasedColumn, string commandName)
    {
        var zeroBasedLine = oneBasedLine - 1;
        var zeroBasedColumn = oneBasedColumn - 1;

        if (zeroBasedLine < 0 || zeroBasedLine >= text.Lines.Count)
        {
            throw new CliUsageException(commandName, $"Line {oneBasedLine} is outside the document range 1..{text.Lines.Count}.");
        }

        var line = text.Lines[zeroBasedLine];
        if (zeroBasedColumn < 0 || zeroBasedColumn > line.Span.Length)
        {
            throw new CliUsageException(commandName, $"Column {oneBasedColumn} is outside the line range 1..{line.Span.Length + 1}.");
        }

        return text.Lines.GetPosition(new LinePosition(zeroBasedLine, zeroBasedColumn));
    }
}

/// <summary>
/// Carries the loaded document text together with a validated span and JSON range.
/// </summary>
public readonly record struct ResolvedTextSpan(SourceText Text, TextSpan Span, DocumentRange Range);
