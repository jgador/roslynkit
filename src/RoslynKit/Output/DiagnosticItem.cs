using Microsoft.CodeAnalysis;

namespace RoslynKit;

/// <summary>
/// Represents one compiler diagnostic surfaced by the <c>diagnostics</c> command.
/// </summary>
public sealed class DiagnosticItem
{
    public DiagnosticItem(
        string projectName,
        string id,
        string severity,
        string message,
        string? path,
        int? line,
        int? column,
        int? endLine,
        int? endColumn)
    {
        ProjectName = projectName;
        Id = id;
        Severity = severity;
        Message = message;
        Path = path;
        Line = line;
        Column = column;
        EndLine = endLine;
        EndColumn = endColumn;
    }

    /// <summary>
    /// Project name associated with the diagnostic.
    /// </summary>
    public string ProjectName { get; }

    /// <summary>
    /// Compiler diagnostic identifier, such as <c>CS1002</c>.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Roslyn diagnostic severity projected as text.
    /// </summary>
    public string Severity { get; }

    /// <summary>
    /// Diagnostic message text from Roslyn.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Absolute source path for source diagnostics, or <c>null</c> for non-source diagnostics.
    /// </summary>
    public string? Path { get; }

    /// <summary>
    /// One-based starting line for source diagnostics.
    /// </summary>
    public int? Line { get; }

    /// <summary>
    /// One-based starting column for source diagnostics.
    /// </summary>
    public int? Column { get; }

    /// <summary>
    /// One-based ending line for source diagnostics.
    /// </summary>
    public int? EndLine { get; }

    /// <summary>
    /// One-based ending column for source diagnostics.
    /// </summary>
    public int? EndColumn { get; }

    /// <summary>
    /// Converts a Roslyn diagnostic into the command-output shape with normalized source coordinates.
    /// </summary>
    public static DiagnosticItem FromDiagnostic(string projectName, Diagnostic diagnostic)
    {
        var span = diagnostic.Location.IsInSource ? diagnostic.Location.GetLineSpan() : default;
        return new DiagnosticItem(
            projectName,
            diagnostic.Id,
            diagnostic.Severity.ToString(),
            diagnostic.GetMessage(),
            diagnostic.Location.IsInSource ? NormalizePath(span.Path) : null,
            diagnostic.Location.IsInSource ? span.StartLinePosition.Line + 1 : null,
            diagnostic.Location.IsInSource ? span.StartLinePosition.Character + 1 : null,
            diagnostic.Location.IsInSource ? span.EndLinePosition.Line + 1 : null,
            diagnostic.Location.IsInSource ? span.EndLinePosition.Character + 1 : null);
    }

    private static string? NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : global::System.IO.Path.GetFullPath(path);
    }
}
