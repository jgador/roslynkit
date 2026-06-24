using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynKit;

/// <summary>
/// Resolves one-based line and column inputs into Roslyn source positions.
/// </summary>
public static class PositionResolver
{
    /// <summary>
    /// Validates one-based source coordinates and converts them into a document position.
    /// </summary>
    public static async Task<int> GetPositionAsync(Document document, int oneBasedLine, int oneBasedColumn, string commandName, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
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
