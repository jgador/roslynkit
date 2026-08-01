namespace RoslynKit;

/// <summary>
/// Routes workspace commands through the daemon and preserves correctness with typed standalone fallback.
/// </summary>
internal static class DaemonFallbackWorkspaceCommandRouter
{
    private const string DaemonUnavailableWarning = "warning: daemon unavailable; executing standalone";

    /// <summary>
    /// Executes through the shared daemon client and falls back only when daemon infrastructure is unavailable.
    /// </summary>
    public static Task<CliProcessResult> ExecuteAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            command,
            DaemonClient.Shared.ExecuteAsync,
            WorkspaceCommandRouter.ExecuteAsync,
            cancellationToken);
    }

    internal static async Task<CliProcessResult> ExecuteAsync(
        ParsedCommand command,
        Func<ParsedCommand, CancellationToken, Task<CliProcessResult>> executeDaemonCommand,
        Func<ParsedCommand, CancellationToken, Task<CliProcessResult>> executeStandaloneCommand,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(executeDaemonCommand);
        ArgumentNullException.ThrowIfNull(executeStandaloneCommand);

        try
        {
            return await executeDaemonCommand(command, cancellationToken).ConfigureAwait(false);
        }
        catch (DaemonClientInfrastructureException)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        CliProcessResult standaloneResult;
        try
        {
            standaloneResult = await executeStandaloneCommand(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            standaloneResult = CliProcessResult.FromException(exception);
        }

        return standaloneResult with
        {
            Stderr = DaemonUnavailableWarning + Environment.NewLine + standaloneResult.Stderr,
        };
    }
}
