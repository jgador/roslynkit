namespace RoslynKit;

/// <summary>
/// Hosts the RoslynKit console entry point.
/// </summary>
internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        return new CliApplication(Console.Out).RunAsync(args);
    }
}
