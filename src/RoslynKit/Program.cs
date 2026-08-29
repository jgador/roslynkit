namespace RoslynKit;

/// <summary>
/// Runs the ordinary RoslynKit command-line application with process-lifetime cancellation.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Runs one RoslynKit command in the current process.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        using var lifetimeCancellation = new ProcessLifetimeCancellation();
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
