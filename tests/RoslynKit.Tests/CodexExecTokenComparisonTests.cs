using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit.Sdk;

namespace RoslynKit.Tests;

/// <summary>
/// Benchmarks live Codex exec token usage for the read-only definition trace with shell-only versus RoslynKit-first guidance.
/// </summary>
public sealed class CodexExecTokenComparisonTests
{
    private readonly ITestOutputHelper _output;

    private const string BenchmarkModelProvider = "openai";
    private const string BenchmarkModel = "gpt-5.4";
    private const string BenchmarkApprovalPolicyToml = "approval_policy=\"never\"";
    private const string BenchmarkModelProviderToml = "model_provider=\"openai\"";
    private const string BenchmarkSandboxMode = "danger-full-access";
    private const string LiveOutputRunnerCommand =
        """dotnet run --project .\tests\RoslynKit.Tests\RoslynKit.Tests.csproj -- -noLogo -reporter verbose -explicit only -method "RoslynKit.Tests.CodexExecTokenComparisonTests.RoslynkitDevPrompt_UsesFewerInputTokens_ThanShellOnlyCodexExec" -showLiveOutput -diagnostics""";

    private static readonly BenchmarkArm[] RunOrder =
    [
        BenchmarkArm.Control,
        BenchmarkArm.Treatment,
        BenchmarkArm.Treatment,
        BenchmarkArm.Control,
        BenchmarkArm.Control,
        BenchmarkArm.Treatment,
    ];

    public CodexExecTokenComparisonTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Explicit = true)]
    public async Task RoslynkitDevPrompt_UsesFewerInputTokens_ThanShellOnlyCodexExec()
    {
        WriteVisibleLine($"Run this benchmark with live console output: {LiveOutputRunnerCommand}");
        WriteVisibleLine(string.Empty);
        var benchmarkExecution = await ExecuteBenchmarkAsync(TestContext.Current.CancellationToken);
        PrintBenchmarkPrompts(benchmarkExecution.ControlPrompt, benchmarkExecution.TreatmentPrompt);
        PrintInputTokenSummary(benchmarkExecution.Results);
    }

    [Fact(Explicit = true)]
    public async Task RoslynkitDevPrompt_DumpsComparisonInFailureMessage()
    {
        var benchmarkExecution = await ExecuteBenchmarkAsync(TestContext.Current.CancellationToken);
        throw new XunitException(
            BuildBenchmarkReport(
                benchmarkExecution.ControlPrompt,
                benchmarkExecution.TreatmentPrompt,
                benchmarkExecution.Results));
    }

    private static string ResolveRoslynkitDevPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var executableName = OperatingSystem.IsWindows() ? "roslynkit.exe" : "roslynkit";
        return Path.Combine(userProfile, ".roslynkit", "tools", "roslynkit-dev", executableName);
    }

    private static string ResolveCodexExecutablePath()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            throw new XunitException("PATH is empty, so `codex` could not be resolved.");
        }

        IEnumerable<string> candidateNames = OperatingSystem.IsWindows()
            ? ["codex.exe", "codex.cmd", "codex.bat", "codex.com"]
            : ["codex"];

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidateName in candidateNames)
            {
                var candidatePath = Path.Combine(directory, candidateName);
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }
        }

        throw new XunitException("`codex` was not found on PATH. Make sure the Codex CLI shim is installed and visible to the test process.");
    }

    private static string BuildBenchmarkTaskBody(string solutionPath)
    {
        return $$"""
Repository context:
- The current working directory is the RoslynKit repository root.
- The solution path is `{{solutionPath}}`.
- If you use RoslynKit, always pass `--target` to the solution path above.

Task:
Trace the `definition` command from CLI entrypoint through parsing/binding, pre-Roslyn validation, Roslyn symbol lookup, and the nearest direct tests.

Requirements:
- Return only a flat bullet list with at most 7 bullets.
- Each bullet must be one sentence and include exact `file:line` references.
- Cover the stages in order: entrypoint, app dispatch, parser/binding, pre-Roslyn validation, Roslyn lookup/projection, and nearest direct tests.
- Keep the answer under 180 words.
- Ignore benchmark-external workflow helpers. Do not read or use `AGENTS.md`, `.agents/`, `.codex/`, `.synapse/`, memory files, skills, Atlas files, or sub-agents.
- Work only from repo source and test files needed for this trace.
- Prefer the smallest possible searches and line-range reads after you identify candidate files.
- Do not edit files.
- Do not run builds or tests.
- Do not make network calls.
""";
    }

    private static string BuildControlPrompt(string roslynkitDevPath, string benchmarkTaskBody)
    {
        return $$"""
For this run, do not use RoslynKit for C# semantic inspection.
Do not use `roslynkit`, `roslynkit-dev`, or the executable at `{{roslynkitDevPath}}`.
Use only shell and file-reading tools for every step, including C# files.

{{benchmarkTaskBody}}
""";
    }

    private static string BuildTreatmentPrompt(string roslynkitDevPath, string benchmarkTaskBody)
    {
        return $$"""
For this run, use RoslynKit first for C# semantic inspection.
Use the side-by-side dev tool at `{{roslynkitDevPath}}`, and do not use the stable global `roslynkit` install.
Invoke the executable directly as a shell command, not through a skill wrapper or helper workflow.
Start with the cheapest RoslynKit semantic workflow and stop as soon as you have enough evidence.
Resolve exact declarations and positions first with `symbols`, `definition`, `references`, or `implementations`, not broad file reads.
Use `quick-info` at the resolved symbol or position before any body read when you need type, signature, or documentation context.
If source text is still necessary, use `document-text` only when a full resolved document read is justified.
Prefer `quick-info`, `document-symbols`, declaration locations, and targeted cross-references before any whole-document read.
If only a small literal snippet or comment block is needed after semantic resolution, use shell and file-reading tools for that narrow read instead of pulling the whole document through RoslynKit.
Do not read an entire `.cs` file or a broad class body through RoslynKit unless prior semantic results prove it is necessary.
Avoid broad `document-symbols` dumps unless the file is already known and you need local structure to choose a member or range.
Use shell and file-reading tools only for literal text, prose, non-C# files, or if RoslynKit fails to load the target.

{{benchmarkTaskBody}}
""";
    }

    private void PrintBenchmarkPrompts(string controlPrompt, string treatmentPrompt)
    {
        WriteOutputBlock("WITHOUT ROSLYNKIT PROMPT", controlPrompt);
        WriteOutputBlock("WITH ROSLYNKIT PROMPT", treatmentPrompt);
    }

    private async Task<BenchmarkExecution> ExecuteBenchmarkAsync(CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Assert.Skip("OPENAI_API_KEY is not set. This explicit benchmark requires a live Codex exec run.");
            throw new UnreachableException();
        }

        var repositoryRoot = TestPaths.RepositoryRoot();
        var solutionPath = TestPaths.SolutionPath();
        var codexExecutablePath = ResolveCodexExecutablePath();
        var roslynkitDevPath = ResolveRoslynkitDevPath();
        if (!File.Exists(roslynkitDevPath))
        {
            throw new XunitException(
                $"roslynkit-dev executable was not found at '{roslynkitDevPath}'. Install the side-by-side dev tool before running this explicit benchmark.");
        }

        var benchmarkTaskBody = BuildBenchmarkTaskBody(solutionPath);
        var controlPrompt = BuildControlPrompt(roslynkitDevPath, benchmarkTaskBody);
        var treatmentPrompt = BuildTreatmentPrompt(roslynkitDevPath, benchmarkTaskBody);
        var results = new List<BenchmarkRunResult>(RunOrder.Length);

        for (var sequenceIndex = 0; sequenceIndex < RunOrder.Length; sequenceIndex++)
        {
            var arm = RunOrder[sequenceIndex];
            var pairIndex = (sequenceIndex / 2) + 1;
            var prompt = arm switch
            {
                BenchmarkArm.Control => controlPrompt,
                BenchmarkArm.Treatment => treatmentPrompt,
                _ => throw new UnreachableException()
            };

            var result = await RunCodexExecAsync(
                arm,
                pairIndex,
                sequenceIndex + 1,
                codexExecutablePath,
                repositoryRoot,
                apiKey,
                prompt,
                cancellationToken);
            results.Add(result);
        }

        return new BenchmarkExecution(controlPrompt, treatmentPrompt, results);
    }

    private void PrintInputTokenSummary(IReadOnlyList<BenchmarkRunResult> results)
    {
        WriteVisibleLine("INPUT TOKEN COMPARISON");

        foreach (var result in results)
        {
            WriteVisibleLine(
                $"{DescribeRun(result.Arm, result.PairIndex, result.SequenceIndex)}: exit_code={result.ExitCode}, input_tokens={result.InputTokens}, cached_input_tokens={result.CachedInputTokens}, output_tokens={result.OutputTokens}");
        }

        var controlTotal = results.Where(result => result.Arm == BenchmarkArm.Control).Sum(result => result.InputTokens);
        var treatmentTotal = results.Where(result => result.Arm == BenchmarkArm.Treatment).Sum(result => result.InputTokens);

        WriteVisibleLine($"without RoslynKit total input_tokens={controlTotal}");
        WriteVisibleLine($"with RoslynKit total input_tokens={treatmentTotal}");
    }

    private static string BuildBenchmarkReport(
        string controlPrompt,
        string treatmentPrompt,
        IReadOnlyList<BenchmarkRunResult> results)
    {
        var builder = new StringBuilder();
        AppendReportBlock(builder, "WITHOUT ROSLYNKIT PROMPT", controlPrompt);
        AppendReportBlock(builder, "WITH ROSLYNKIT PROMPT", treatmentPrompt);
        builder.AppendLine("INPUT TOKEN COMPARISON");

        foreach (var result in results)
        {
            builder.AppendLine(
                $"{DescribeRun(result.Arm, result.PairIndex, result.SequenceIndex)}: exit_code={result.ExitCode}, input_tokens={result.InputTokens}, cached_input_tokens={result.CachedInputTokens}, output_tokens={result.OutputTokens}");
        }

        var controlTotal = results.Where(result => result.Arm == BenchmarkArm.Control).Sum(result => result.InputTokens);
        var treatmentTotal = results.Where(result => result.Arm == BenchmarkArm.Treatment).Sum(result => result.InputTokens);

        builder.AppendLine($"without RoslynKit total input_tokens={controlTotal}");
        builder.AppendLine($"with RoslynKit total input_tokens={treatmentTotal}");
        return builder.ToString().TrimEnd();
    }

    private static void AppendReportBlock(StringBuilder builder, string heading, string content)
    {
        builder.AppendLine(heading);
        builder.AppendLine(content.TrimEnd());
        builder.AppendLine();
    }

    private void WriteOutputBlock(string heading, string content)
    {
        WriteVisibleLine(heading);

        foreach (var line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            WriteVisibleLine(line);
        }

        WriteVisibleLine(string.Empty);
    }

    private void WriteVisibleLine(string line)
    {
        _output.WriteLine(line);

        if (TestContext.Current is { } context)
        {
            context.SendDiagnosticMessage(line);
        }
    }

    private static async Task<BenchmarkRunResult> RunCodexExecAsync(
        BenchmarkArm arm,
        int pairIndex,
        int sequenceIndex,
        string codexExecutablePath,
        string repositoryRoot,
        string apiKey,
        string prompt,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = codexExecutablePath,
            WorkingDirectory = repositoryRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--ephemeral");
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("--ignore-user-config");
        startInfo.ArgumentList.Add("--ignore-rules");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(BenchmarkApprovalPolicyToml);
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(BenchmarkModelProviderToml);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(BenchmarkModel);
        startInfo.ArgumentList.Add("--cd");
        startInfo.ArgumentList.Add(repositoryRoot);
        startInfo.ArgumentList.Add("--sandbox");
        startInfo.ArgumentList.Add(BenchmarkSandboxMode);
        startInfo.ArgumentList.Add("-");
        startInfo.Environment["OPENAI_API_KEY"] = apiKey;
        startInfo.Environment["CODEX_API_KEY"] = apiKey;

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            throw new XunitException(
                $"Failed to start `codex exec` for {DescribeRun(arm, pairIndex, sequenceIndex)} using '{codexExecutablePath}'. Make sure the resolved Codex CLI shim is runnable. {ex.Message}");
        }

        if (process is null)
        {
            throw new XunitException($"Failed to start `codex exec` for {DescribeRun(arm, pairIndex, sequenceIndex)} using '{codexExecutablePath}'.");
        }

        using (process)
        {
            using var cancellationRegistration = cancellationToken.Register(() => TryKillProcess(process));

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.StandardInput.WriteAsync(prompt).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            process.StandardInput.Close();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new XunitException(BuildProcessFailureMessage(arm, pairIndex, sequenceIndex, process.ExitCode, stdout, stderr));
            }

            if (arm == BenchmarkArm.Treatment && TryFindRoslynKitPolicyBlock(stdout, out var blockedCommand))
            {
                throw new XunitException(
                    $"`codex exec` could not invoke RoslynKit for {DescribeRun(arm, pairIndex, sequenceIndex)} because the command was blocked by policy: {blockedCommand}{Environment.NewLine}" +
                    $"This benchmark is not valid with model provider `{BenchmarkModelProvider}`, model `{BenchmarkModel}`, approval policy `never`, and sandbox `{BenchmarkSandboxMode}` because the treatment arm still cannot actually use the RoslynKit dev tool.");
            }

            var tokenUsage = ExtractFinalTokenUsage(stdout, arm, pairIndex, sequenceIndex);
            return new BenchmarkRunResult(
                arm,
                pairIndex,
                sequenceIndex,
                process.ExitCode,
                tokenUsage.InputTokens,
                tokenUsage.CachedInputTokens,
                tokenUsage.OutputTokens,
                stdout,
                stderr);
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static TokenUsage ExtractFinalTokenUsage(string stdout, BenchmarkArm arm, int pairIndex, int sequenceIndex)
    {
        TokenUsage? finalTokenUsage = null;
        using var reader = new StringReader(stdout);

        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                if (!TryReadTokenUsage(document.RootElement, out var tokenUsage))
                {
                    continue;
                }

                finalTokenUsage = tokenUsage;
            }
            catch (JsonException ex)
            {
                throw new XunitException(
                    $"`codex exec` stdout line {lineNumber} was not valid JSON for {DescribeRun(arm, pairIndex, sequenceIndex)}. {ex.Message}{Environment.NewLine}{TruncateForMessage(line)}");
            }
        }

        return finalTokenUsage
            ?? throw new XunitException(
                $"`codex exec` did not emit a final token usage event for {DescribeRun(arm, pairIndex, sequenceIndex)}.{Environment.NewLine}stdout:{Environment.NewLine}{TruncateForMessage(stdout)}");
    }

    private static bool TryReadTokenUsage(JsonElement root, out TokenUsage tokenUsage)
    {
        tokenUsage = default;

        if (!root.TryGetProperty("type", out var eventType) || eventType.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var eventTypeValue = eventType.GetString();
        if (string.Equals(eventTypeValue, "turn.completed", StringComparison.Ordinal))
        {
            var usage = GetRequiredObject(root, "usage");
            tokenUsage = new TokenUsage(
                GetRequiredInt64(usage, "input_tokens"),
                GetRequiredInt64OrDefault(usage, "cached_input_tokens"),
                GetRequiredInt64(usage, "output_tokens"));
            return true;
        }

        if (!string.Equals(eventTypeValue, "event_msg", StringComparison.Ordinal))
        {
            return false;
        }

        if (!root.TryGetProperty("payload", out var payload)
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("type", out var payloadType)
            || payloadType.ValueKind != JsonValueKind.String
            || !string.Equals(payloadType.GetString(), "token_count", StringComparison.Ordinal))
        {
            return false;
        }

        var info = GetRequiredObject(payload, "info");
        var totalTokenUsage = GetRequiredObject(info, "total_token_usage");
        tokenUsage = new TokenUsage(
            GetRequiredInt64(totalTokenUsage, "input_tokens"),
            GetRequiredInt64OrDefault(totalTokenUsage, "cached_input_tokens"),
            GetRequiredInt64(totalTokenUsage, "output_tokens"));
        return true;
    }

    private static bool TryFindRoslynKitPolicyBlock(string stdout, out string blockedCommand)
    {
        blockedCommand = string.Empty;
        using var reader = new StringReader(stdout);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty("type", out var eventType)
                    || eventType.ValueKind != JsonValueKind.String
                    || !string.Equals(eventType.GetString(), "item.completed", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!document.RootElement.TryGetProperty("item", out var item)
                    || item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("type", out var itemType)
                    || itemType.ValueKind != JsonValueKind.String
                    || !string.Equals(itemType.GetString(), "command_execution", StringComparison.Ordinal))
                {
                    continue;
                }

                var command = item.TryGetProperty("command", out var commandProperty) && commandProperty.ValueKind == JsonValueKind.String
                    ? commandProperty.GetString() ?? string.Empty
                    : string.Empty;
                var aggregatedOutput = item.TryGetProperty("aggregated_output", out var outputProperty) && outputProperty.ValueKind == JsonValueKind.String
                    ? outputProperty.GetString() ?? string.Empty
                    : string.Empty;
                var status = item.TryGetProperty("status", out var statusProperty) && statusProperty.ValueKind == JsonValueKind.String
                    ? statusProperty.GetString() ?? string.Empty
                    : string.Empty;

                if (!string.Equals(status, "declined", StringComparison.Ordinal)
                    && !aggregatedOutput.Contains("blocked by policy", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!ContainsRoslynKitInvocation(command) && !ContainsRoslynKitInvocation(aggregatedOutput))
                {
                    continue;
                }

                blockedCommand = string.IsNullOrWhiteSpace(command) ? aggregatedOutput : command;
                return true;
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return false;
    }

    private static JsonElement GetRequiredObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            throw new XunitException($"Expected JSON object property `{propertyName}` in `codex exec` output.");
        }

        return property;
    }

    private static long GetRequiredInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            throw new XunitException($"Expected numeric JSON property `{propertyName}` in `codex exec` output.");
        }

        return property.GetInt64();
    }

    private static long GetRequiredInt64OrDefault(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        if (property.ValueKind != JsonValueKind.Number)
        {
            throw new XunitException($"Expected numeric JSON property `{propertyName}` in `codex exec` output.");
        }

        return property.GetInt64();
    }

    private static string BuildProcessFailureMessage(
        BenchmarkArm arm,
        int pairIndex,
        int sequenceIndex,
        int exitCode,
        string stdout,
        string stderr)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"`codex exec` exited with code {exitCode} for {DescribeRun(arm, pairIndex, sequenceIndex)}.");
        builder.AppendLine("stdout:");
        builder.AppendLine(TruncateForMessage(stdout));
        builder.AppendLine("stderr:");
        builder.AppendLine(TruncateForMessage(stderr));
        return builder.ToString().TrimEnd();
    }

    private static string DescribeRun(BenchmarkArm arm, int pairIndex, int sequenceIndex)
    {
        return $"pair {pairIndex}, sequence {sequenceIndex}, arm {arm.ToString().ToLowerInvariant()}";
    }

    private static string TruncateForMessage(string text)
    {
        const int maxLength = 4000;

        if (string.IsNullOrWhiteSpace(text))
        {
            return "<empty>";
        }

        var normalizedText = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalizedText.Length <= maxLength)
        {
            return normalizedText;
        }

        var edgeLength = maxLength / 2;
        return $"{normalizedText[..edgeLength]}{Environment.NewLine}...{Environment.NewLine}{normalizedText[^edgeLength..]}";
    }

    private static bool ContainsRoslynKitInvocation(string text)
    {
        return text.Contains("roslynkit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("RoslynKit.dll", StringComparison.Ordinal);
    }

    private enum BenchmarkArm
    {
        Control,
        Treatment
    }

    private sealed record BenchmarkExecution(
        string ControlPrompt,
        string TreatmentPrompt,
        IReadOnlyList<BenchmarkRunResult> Results);

    private readonly record struct TokenUsage(long InputTokens, long CachedInputTokens, long OutputTokens);

    private sealed record BenchmarkRunResult(
        BenchmarkArm Arm,
        int PairIndex,
        int SequenceIndex,
        int ExitCode,
        long InputTokens,
        long CachedInputTokens,
        long OutputTokens,
        string Stdout,
        string Stderr);
}
