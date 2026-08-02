namespace RoslynKit;

/// <summary>
/// Executes workspace-backed commands in the current process with standalone workspace ownership.
/// </summary>
internal static class WorkspaceCommandRouter
{
    public static async Task<CliProcessResult> ExecuteAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        return await WorkspaceCommandBackend.ExecuteStandaloneAsync(command, cancellationToken).ConfigureAwait(false);
    }
}
