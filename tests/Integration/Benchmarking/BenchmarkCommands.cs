namespace RoslynKit.Benchmarking;

/// <summary>
/// Constructs direct process invocations used by the benchmark.
/// </summary>
internal static class BenchmarkCommands
{
    public static ProcessInvocation BuildRoslynKit(string repositoryRoot)
    {
        return new ProcessInvocation(
            "dotnet",
            repositoryRoot,
            [
                "build",
                "./src/RoslynKit/RoslynKit.csproj",
                "--configuration",
                "Release",
                "--tl:off",
                "--nologo",
                "-clp:ErrorsOnly;NoSummary",
            ]);
    }

    public static ProcessInvocation Index(
        string repositoryRoot,
        string roslynKitPath,
        string indexPath)
    {
        return new ProcessInvocation(
            roslynKitPath,
            repositoryRoot,
            [
                "index",
                "--target",
                "./RoslynKit.slnx",
                "--index-path",
                indexPath,
                "--text-only",
            ]);
    }

    public static ProcessInvocation Search(
        string repositoryRoot,
        string roslynKitPath,
        string indexPath,
        BenchmarkCase benchmarkCase,
        int maximumResults)
    {
        return new ProcessInvocation(
            roslynKitPath,
            repositoryRoot,
            [
                "search",
                "--target",
                "./RoslynKit.slnx",
                "--index-path",
                indexPath,
                "--query",
                benchmarkCase.Query,
                "--max-results",
                maximumResults.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--text-only",
                "--compact",
                "--balanced",
            ]);
    }

    public static string Display(ProcessInvocation invocation)
    {
        return string.Join(' ', new[] { invocation.FileName }.Concat(invocation.Arguments).Select(Quote));
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.All(character => char.IsLetterOrDigit(character) || "./_:-".Contains(character, StringComparison.Ordinal)))
        {
            return value;
        }

        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
