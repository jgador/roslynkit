namespace RoslynKit;

/// <summary>
/// Forwards the RoslynKit console entry point into <see cref="CliApplication.RunAsync(IReadOnlyList{string}, CancellationToken)"/>.
/// </summary>
internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        return new CliApplication(Console.Out).RunAsync(args);
    }
}
