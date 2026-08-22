using System.Globalization;
using System.Text.RegularExpressions;

namespace RoslynKit.Benchmarking;

/// <summary>
/// Holds validated command-line settings for the benchmark controller.
/// </summary>
internal sealed record BenchmarkOptions
{
    public string Model { get; init; } = "gpt-5.6-sol";

    public string ReasoningEffort { get; init; } = "high";

    public int Trials { get; init; } = 1;

    public string Case { get; init; } = "all";

    public int MaximumResults { get; init; } = 10;

    public string IndexPath { get; init; } = "./artifacts/roslynkit-text.db";

    public string? RoslynKitPath { get; init; }

    public string? ResumeRunRoot { get; init; }

    public string? ReportRunRoot { get; init; }

    public bool DryRun { get; init; }

    public bool Help { get; init; }
}

/// <summary>
/// Parses the dependency-free benchmark command line.
/// </summary>
internal static partial class BenchmarkOptionsParser
{
    public static string Usage =>
        """
        Usage:
          dotnet run --project ./tests/Integration/Benchmarking/RoslynKit.Benchmarking.csproj -- [options]

        Options:
          --model <id>                 Codex model (default: gpt-5.6-sol)
          --reasoning-effort <level>   Codex reasoning effort (default: high)
          --trials <1-100>             Trials per selected case (default: 1)
          --case <id|all>              Select one case or all cases (default: all)
          --case-id <id|all>           Compatibility alias for --case
          --max-results <2-50>         Maximum RoslynKit results (default: 10)
          --index-path <path>          Database directly below ./artifacts/
          --roslynkit-path <path>      Use an existing RoslynKit apphost
          --dry-run                    Print the schedule without building or starting Codex
          --resume-run-root <path>     Resume missing sessions from one run document
          --report-run-root <path>     Regenerate CSV and Markdown from one run document
          --help                       Show this help
        """;

    public static BenchmarkOptions Parse(IReadOnlyList<string> arguments)
    {
        var options = new BenchmarkOptions();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index++)
        {
            var option = arguments[index];
            switch (option)
            {
                case "--help":
                case "-h":
                    AddOnce(seen, "help", option);
                    options = options with { Help = true };
                    break;
                case "--dry-run":
                    AddOnce(seen, "dry-run", option);
                    options = options with { DryRun = true };
                    break;
                case "--model":
                    AddOnce(seen, "model", option);
                    options = options with { Model = ReadValue(arguments, ref index, option) };
                    break;
                case "--reasoning-effort":
                    AddOnce(seen, "reasoning-effort", option);
                    options = options with { ReasoningEffort = ReadValue(arguments, ref index, option) };
                    break;
                case "--trials":
                    AddOnce(seen, "trials", option);
                    options = options with { Trials = ParseInteger(ReadValue(arguments, ref index, option), option) };
                    break;
                case "--case":
                case "--case-id":
                    AddOnce(seen, "case", option);
                    options = options with { Case = ReadValue(arguments, ref index, option) };
                    break;
                case "--max-results":
                    AddOnce(seen, "max-results", option);
                    options = options with { MaximumResults = ParseInteger(ReadValue(arguments, ref index, option), option) };
                    break;
                case "--index-path":
                    AddOnce(seen, "index-path", option);
                    options = options with { IndexPath = ReadValue(arguments, ref index, option) };
                    break;
                case "--roslynkit-path":
                    AddOnce(seen, "roslynkit-path", option);
                    options = options with { RoslynKitPath = ReadValue(arguments, ref index, option) };
                    break;
                case "--resume-run-root":
                    AddOnce(seen, "resume-run-root", option);
                    options = options with { ResumeRunRoot = ReadValue(arguments, ref index, option) };
                    break;
                case "--report-run-root":
                    AddOnce(seen, "report-run-root", option);
                    options = options with { ReportRunRoot = ReadValue(arguments, ref index, option) };
                    break;
                default:
                    throw new BenchmarkException($"Unknown benchmark option: '{option}'.");
            }
        }

        Validate(options);
        return options;
    }

    public static string NormalizeIndexPath(string value)
    {
        var normalized = value.Replace('\\', '/');
        if (!normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = $"./{normalized}";
        }

        if (!IndexPathPattern().IsMatch(normalized))
        {
            throw new BenchmarkException("--index-path must name one database file directly below ./artifacts/.");
        }

        return normalized;
    }

    private static void Validate(BenchmarkOptions options)
    {
        if (options.Trials is < 1 or > 100)
        {
            throw new BenchmarkException("--trials must be from 1 through 100.");
        }

        if (options.MaximumResults is < 2 or > 50)
        {
            throw new BenchmarkException("--max-results must be from 2 through 50.");
        }

        if (string.IsNullOrWhiteSpace(options.Model)
            || string.IsNullOrWhiteSpace(options.ReasoningEffort)
            || string.IsNullOrWhiteSpace(options.Case))
        {
            throw new BenchmarkException("Model, reasoning effort, and case must not be empty.");
        }

        _ = NormalizeIndexPath(options.IndexPath);
        if (options.ResumeRunRoot is not null && options.ReportRunRoot is not null)
        {
            throw new BenchmarkException("--resume-run-root and --report-run-root are mutually exclusive.");
        }

        if (options.DryRun && (options.ResumeRunRoot is not null || options.ReportRunRoot is not null))
        {
            throw new BenchmarkException("--dry-run cannot be combined with resume or report mode.");
        }
    }

    private static string ReadValue(IReadOnlyList<string> arguments, ref int index, string option)
    {
        if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new BenchmarkException($"{option} requires a value.");
        }

        index++;
        return arguments[index];
    }

    private static int ParseInteger(string value, string option)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
        {
            throw new BenchmarkException($"{option} requires an integer value.");
        }

        return result;
    }

    private static void AddOnce(ISet<string> seen, string name, string option)
    {
        if (!seen.Add(name))
        {
            throw new BenchmarkException($"Option '{option}' was specified more than once.");
        }
    }

    [GeneratedRegex("^\\./artifacts/[A-Za-z0-9._-]+\\.db$", RegexOptions.CultureInvariant)]
    private static partial Regex IndexPathPattern();
}
