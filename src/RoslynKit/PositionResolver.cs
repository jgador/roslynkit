using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynKit;

/// <summary>
/// Converts one-based CLI coordinates into Roslyn positions and projects Roslyn spans into JSON document ranges.
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
