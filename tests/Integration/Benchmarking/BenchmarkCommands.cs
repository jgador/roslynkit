namespace RoslynKit.Benchmarking;

/// <summary>
/// Constructs direct process invocations used by the benchmark.
/// </summary>
internal static class BenchmarkCommands
{
    private static readonly string[] DisabledCodexFeatures =
    [
        "apps",
        "browser_use",
        "computer_use",
        "goals",
        "image_generation",
        "memories",
        "multi_agent",
        "multi_agent_v2",
        "plugins",
        "shell_tool",
        "skill_search",
        "standalone_web_search",
        "unified_exec",
    ];

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

    public static ProcessInvocation Codex(
        string repositoryRoot,
        string model,
        string reasoningEffort,
        string answerPath,
        string prompt)
    {
        var arguments = new List<string>
        {
            "exec",
            "--json",
            "--ephemeral",
            "--ignore-rules",
            "--sandbox",
            "read-only",
            "--config",
            $"model_reasoning_effort=\"{reasoningEffort}\"",
            "--config",
            "project_doc_max_bytes=0",
            "--model",
            model,
            "--color",
            "never",
            "--cd",
            repositoryRoot,
            "--output-last-message",
            answerPath,
        };
        foreach (var feature in DisabledCodexFeatures)
        {
            arguments.Add("--disable");
            arguments.Add(feature);
        }

        arguments.Add("-");
        return new ProcessInvocation(
            "codex",
            repositoryRoot,
            arguments,
            prompt,
            ["CODEX_THREAD_ID"]);
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
