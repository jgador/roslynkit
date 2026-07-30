namespace RoslynKit;

/// <summary>
/// Executes local daemon lifecycle commands without loading a Roslyn workspace or starting a daemon.
/// </summary>
internal static class DaemonCommandExecutor
{
    /// <summary>
    /// Routes a lifecycle control through the non-starting daemon client and renders its stable output.
    /// </summary>
    public static Task<CliProcessResult> ExecuteAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(command, DaemonClient.Shared, cancellationToken);
    }

    internal static async Task<CliProcessResult> ExecuteAsync(
        ParsedCommand command,
        DaemonClient client,
        CancellationToken cancellationToken)
    {
        if (command.Name is not ("daemon status" or "daemon stop"))
        {
            throw new InvalidOperationException($"Unsupported daemon command '{command.Name}'.");
        }

        if (command.Name == "daemon stop")
        {
            var stop = await client.StopAsync(command, cancellationToken).ConfigureAwait(false);
            return CliProcessResult.Success(
                stop is null
                    ? "command: daemon stop\nstate: not-running"
                    : "command: daemon stop\nstate: stopping");
        }

        var status = await client.GetStatusAsync(command, cancellationToken).ConfigureAwait(false);
        if (status is null || !status.Running)
        {
            return CliProcessResult.Success("command: daemon status\nstate: not-running");
        }

        var lines = new List<string>
        {
            "command: daemon status",
            "state: running",
            $"target: `{status.TargetPath}`",
            $"pid: {status.ProcessId}",
            $"workspace: {status.WorkspaceState}",
        };
        if (status.Generation is not null)
        {
            lines.Add($"generation: {status.Generation}");
        }

        lines.Add($"active-requests: {status.ActiveRequests}");
        lines.Add($"queued-requests: {status.QueuedRequests}");
        if (!string.IsNullOrWhiteSpace(status.Diagnostic))
        {
            lines.Add($"diagnostic: {SingleLine(status.Diagnostic)}");
        }

        return CliProcessResult.Success(string.Join('\n', lines));
    }

    private static string SingleLine(string value)
    {
        return value
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }
}
