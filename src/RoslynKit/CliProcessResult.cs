namespace RoslynKit;

/// <summary>
/// Carries exact buffered process streams and an exit code across local or IPC execution boundaries.
/// </summary>
public sealed record CliProcessResult(int ExitCode, string Stdout, string Stderr)
{
    internal static CliProcessResult Success(string stdout)
    {
        return new CliProcessResult(0, stdout + Environment.NewLine, string.Empty);
    }

    internal static CliProcessResult Failure(int exitCode, string errorCode, string message, string? hint = null)
    {
        var stdout = $"error: {errorCode}\nmessage: {message}";
        if (!string.IsNullOrWhiteSpace(hint))
        {
            stdout += $"\nhint: {hint}";
        }

        return new CliProcessResult(exitCode, stdout + Environment.NewLine, string.Empty);
    }
}
