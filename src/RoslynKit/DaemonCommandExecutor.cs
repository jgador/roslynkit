namespace RoslynKit;

/// <summary>
/// Executes local daemon lifecycle commands without loading a Roslyn workspace or starting a daemon.
/// </summary>
internal static class DaemonCommandExecutor
{
    public static CliProcessResult Execute(ParsedCommand command)
    {
        if (command.Name is not ("daemon status" or "daemon stop"))
        {
            throw new InvalidOperationException($"Unsupported daemon command '{command.Name}'.");
        }

        return CliProcessResult.Success($"command: {command.Name}\nstate: not-running");
    }
}
