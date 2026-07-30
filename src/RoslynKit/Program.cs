namespace RoslynKit;

/// <summary>
/// Forwards the RoslynKit console entry point into <see cref="CliApplication.RunAsync(IReadOnlyList{string}, CancellationToken)"/>.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Runs the command-line application using standard output as the rendering target.
    /// </summary>
    public static Task<int> Main(string[] args)
    {
        return new CliApplication(Console.Out, Console.Error).RunAsync(args);
    }
}
