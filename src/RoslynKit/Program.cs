namespace RoslynKit;

/// <summary>
/// Routes hidden daemon mode before forwarding ordinary arguments to <see cref="CliApplication.RunAsync(IReadOnlyList{string}, CancellationToken)"/>.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Runs either the hidden daemon host or the ordinary command-line application.
    /// </summary>
    public static Task<int> Main(string[] args)
    {
        return RunAsync(args, DaemonServerRunner.RunAsync, RunCliAsync, CancellationToken.None);
    }

    internal static Task<int> RunAsync(
        IReadOnlyList<string> args,
        Func<string, CancellationToken, Task<int>> runDaemonAsync,
        Func<IReadOnlyList<string>, CancellationToken, Task<int>> runCliAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(runDaemonAsync);
        ArgumentNullException.ThrowIfNull(runCliAsync);

        if (args.Count > 0 && args[0] == DaemonServerRunner.InternalModeToken)
        {
            return DaemonServerRunner.TryParseArguments(args, out var targetPath)
                ? runDaemonAsync(targetPath!, cancellationToken)
                : Task.FromResult(1);
        }

        return runCliAsync(args, cancellationToken);
    }

    private static Task<int> RunCliAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        return new CliApplication(
            Console.Out,
            Console.Error,
            DaemonClient.Shared.ExecuteAsync,
            DaemonCommandExecutor.ExecuteAsync).RunAsync(args, cancellationToken);
    }
}
