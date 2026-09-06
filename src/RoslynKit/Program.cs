namespace RoslynKit;

/// <summary>
/// Routes standalone commands and client-owned standard-stream sessions with process-lifetime cancellation.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Runs one RoslynKit command in the current process.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        using var lifetimeCancellation = new ProcessLifetimeCancellation();
        if (args.FirstOrDefault() == "--repository-worker")
        {
            Console.SetOut(Console.Error);
            return await RepositoryWorkerHost.RunAsync(args[1..], Console.OpenStandardInput(), Console.OpenStandardOutput(), lifetimeCancellation.Token).ConfigureAwait(false);
        }

        if (args.FirstOrDefault() == "serve")
        {
            try
            {
                var command = CliParser.Parse(args);
                if (!command.IsHelp)
                {
                    var options = McpServeOptions.FromCommand(command);
                    Console.SetOut(Console.Error);
                    return await McpServerHost.RunAsync(options, lifetimeCancellation.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                var failure = CliProcessResult.FromException(exception);
                await Console.Error.WriteAsync(failure.Stdout).ConfigureAwait(false);
                return failure.ExitCode;
            }
        }

        return await RunCliAsync(args, lifetimeCancellation.Token).ConfigureAwait(false);
    }

    private static Task<int> RunCliAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        return CreateCliApplication(Console.Out, Console.Error).RunAsync(args, cancellationToken);
    }

    internal static CliApplication CreateCliApplication(TextWriter stdout, TextWriter stderr)
    {
        return new CliApplication(stdout, stderr, WorkspaceCommandRouter.ExecuteAsync);
    }
}
