using Microsoft.CodeAnalysis;

namespace RoslynKit;

/// <summary>
/// Represents a one-based source span for a declaration, definition, or reference location.
/// </summary>
public sealed class SourceRange
{
    public SourceRange(string? path, int line, int column, int endLine, int endColumn)
    {
        Path = path;
        Line = line;
        Column = column;
        EndLine = endLine;
        EndColumn = endColumn;
    }

    /// <summary>
    /// Absolute source path for the location, when available.
    /// </summary>
    public string? Path { get; }

    /// <summary>
    /// One-based starting line of the source span.
    /// </summary>
    public int Line { get; }

    /// <summary>
    /// One-based starting column of the source span.
    /// </summary>
    public int Column { get; }

    /// <summary>
    /// One-based ending line of the source span.
    /// </summary>
    public int EndLine { get; }

    /// <summary>
    /// One-based ending column of the source span.
    /// </summary>
    public int EndColumn { get; }

    /// <summary>
    /// Converts a Roslyn source location into normalized output coordinates.
    /// </summary>
    public static SourceRange FromLocation(Location location)
    {
        var span = location.GetLineSpan();
        return new SourceRange(
            NormalizePath(span.Path, location.SourceTree?.FilePath),
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1);
    }

    private static string? NormalizePath(string? path, string? fallbackPath)
    {
        var resolvedPath = !string.IsNullOrWhiteSpace(path)
            ? path
            : fallbackPath;

        return string.IsNullOrWhiteSpace(resolvedPath)
            ? null
            : global::System.IO.Path.GetFullPath(resolvedPath);
    }
}
