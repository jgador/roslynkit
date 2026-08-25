using System.Diagnostics;
using System.Globalization;

namespace RoslynKit.Benchmarking;

/// <summary>
/// Implements the file-backed benchmark helper commands used by the Bash controller.
/// </summary>
internal sealed class BenchmarkApplication(
    string repositoryRoot,
    IProcessRunner processRunner,
    TextWriter output,
    TextWriter error,
    TimeProvider timeProvider)
{
    public static BenchmarkApplication CreateDefault()
    {
        var repositoryRoot = BenchmarkPaths.FindRepositoryRoot(Environment.CurrentDirectory);
        return new BenchmarkApplication(repositoryRoot, new ProcessRunner(), Console.Out, Console.Error, TimeProvider.System);
    }

    /// <summary>
    /// Routes one helper command to preparation, session artifact creation, evaluation, or reporting.
    /// </summary>
    public async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (arguments.Count == 0 || IsHelp(arguments[0]))
        {
            await output.WriteLineAsync(BenchmarkOptionsParser.Usage).ConfigureAwait(false);
            return 0;
        }

        var command = arguments[0];
        var commandArguments = arguments.Skip(1).ToArray();
        return command switch
        {
            "prepare" => await PrepareCommandAsync(commandArguments, cancellationToken).ConfigureAwait(false),
            "prepare-session" => await PrepareSessionCommandAsync(commandArguments, cancellationToken).ConfigureAwait(false),
            "evaluate-session" => await EvaluateSessionCommandAsync(commandArguments, cancellationToken).ConfigureAwait(false),
            "report" => await ReportCommandAsync(commandArguments, cancellationToken).ConfigureAwait(false),
            _ => throw new BenchmarkException($"Unknown benchmark command: '{command}'."),
        };
    }

    private async Task<int> PrepareCommandAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var options = BenchmarkOptionsParser.Parse(arguments);
        if (options.Help)
        {
            await output.WriteLineAsync(BenchmarkOptionsParser.Usage).ConfigureAwait(false);
            return 0;
        }

        if (options.DryRun)
        {
            var dryRunDocument = CreateDocument(options, BenchmarkCatalog.Select(
                BenchmarkCatalog.Load(repositoryRoot),
                options.Case));
            await WriteDryRunAsync(dryRunDocument).ConfigureAwait(false);
            await WriteControlAsync("dry-run").ConfigureAwait(false);
            return 0;
        }

        if (options.ReportRunRoot is not null)
        {
            var reportRoot = BenchmarkPaths.ResolveExistingRunRoot(repositoryRoot, options.ReportRunRoot);
            var reportDocument = BenchmarkRunStore.Load(repositoryRoot, reportRoot);
            await BenchmarkReports.WriteAsync(reportRoot, reportDocument, cancellationToken).ConfigureAwait(false);
            await error.WriteLineAsync($"Benchmark reports refreshed: {reportRoot}").ConfigureAwait(false);
            await WriteControlAsync("report", reportRoot).ConfigureAwait(false);
            return 0;
        }

        string runRoot;
        BenchmarkRunDocument document;
        if (options.ResumeRunRoot is not null)
        {
            runRoot = BenchmarkPaths.ResolveExistingRunRoot(repositoryRoot, options.ResumeRunRoot);
            document = BenchmarkRunStore.Load(repositoryRoot, runRoot);
        }
        else
        {
            var cases = BenchmarkCatalog.Select(BenchmarkCatalog.Load(repositoryRoot), options.Case);
            document = CreateDocument(options, cases);
            runRoot = BenchmarkPaths.CreateRunRoot(repositoryRoot, timeProvider.GetUtcNow());
            CreateArtifactDirectories(runRoot);
            await BenchmarkRunStore.SaveAsync(runRoot, document, cancellationToken).ConfigureAwait(false);
        }

        CreateArtifactDirectories(runRoot);
        var pending = BenchmarkSchedule.Pending(document);
        if (pending.Count > 0)
        {
            await PrepareAsync(document.Configuration, cancellationToken).ConfigureAwait(false);
        }

        await BenchmarkReports.WriteAsync(runRoot, document, cancellationToken).ConfigureAwait(false);
        await WriteControlAsync(
            "run",
            runRoot,
            document.Configuration.Model,
            document.Configuration.ReasoningEffort,
            pending.Select(key => key.RunId).ToArray()).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> PrepareSessionCommandAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var command = ParseRunCommandOptions(arguments, requireExitCode: false, requireRunId: true);
        if (command.Help)
        {
            await output.WriteLineAsync(BenchmarkOptionsParser.Usage).ConfigureAwait(false);
            return 0;
        }

        var runRoot = BenchmarkPaths.ResolveExistingRunRoot(repositoryRoot, command.RunRoot!);
        var document = BenchmarkRunStore.Load(repositoryRoot, runRoot);
        var key = GetPendingKey(document, command.RunId!);
        var benchmarkCase = document.Cases.Single(candidate => candidate.Id == key.CaseId);
        var artifacts = CreateArtifacts(runRoot, key.RunId);

        CreateArtifactDirectories(runRoot);
        var retrieval = await new BenchmarkRetrievalService(processRunner).RetrieveAsync(
            key.Condition,
            repositoryRoot,
            document.Configuration,
            benchmarkCase,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            artifacts.EvidencePath,
            retrieval.Evidence + "\n",
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            artifacts.PromptPath,
            BenchmarkPrompt.Render(key.Condition, benchmarkCase, retrieval.Evidence),
            cancellationToken).ConfigureAwait(false);

        File.Delete(artifacts.AnswerPath);
        File.Delete(artifacts.EventPath);
        File.Delete(artifacts.StderrPath);
        await File.WriteAllTextAsync(
            artifacts.TimingPath,
            Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);

        await output.WriteLineAsync(artifacts.PromptPath).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> EvaluateSessionCommandAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var command = ParseRunCommandOptions(arguments, requireExitCode: true, requireRunId: true);
        if (command.Help)
        {
            await output.WriteLineAsync(BenchmarkOptionsParser.Usage).ConfigureAwait(false);
            return 0;
        }

        var runRoot = BenchmarkPaths.ResolveExistingRunRoot(repositoryRoot, command.RunRoot!);
        var document = BenchmarkRunStore.Load(repositoryRoot, runRoot);
        var key = GetPendingKey(document, command.RunId!);
        var benchmarkCase = document.Cases.Single(candidate => candidate.Id == key.CaseId);
        var artifacts = CreateArtifacts(runRoot, key.RunId);
        var artifactIssues = new List<string>();
        var answer = await ReadArtifactAsync(artifacts.AnswerPath, "answer", artifactIssues, cancellationToken)
            .ConfigureAwait(false);
        var events = await ReadArtifactAsync(artifacts.EventPath, "event log", artifactIssues, cancellationToken)
            .ConfigureAwait(false);
        var standardError = await ReadArtifactAsync(artifacts.StderrPath, "standard error", artifactIssues, cancellationToken)
            .ConfigureAwait(false);
        var retrievalBytes = await ReadEvidenceBytesAsync(artifacts.EvidencePath, artifactIssues, cancellationToken)
            .ConfigureAwait(false);
        var durationSeconds = await ReadDurationSecondsAsync(artifacts.TimingPath, artifactIssues, cancellationToken)
            .ConfigureAwait(false);
        var result = BenchmarkSessionEvaluator.Evaluate(
            benchmarkCase,
            key,
            document.Configuration,
            new ProcessResult(command.ExitCode!.Value, events, standardError),
            answer,
            retrievalBytes,
            durationSeconds,
            GetRetrievalCommand(key, document.Configuration, benchmarkCase),
            ToArtifactPath(runRoot, artifacts.AnswerPath),
            ToArtifactPath(runRoot, artifacts.EvidencePath),
            ToArtifactPath(runRoot, artifacts.EventPath),
            ToArtifactPath(runRoot, artifacts.StderrPath),
            artifactIssues);
        document.Sessions.Add(result);
        document.UpdatedAtUtc = timeProvider.GetUtcNow();
        await BenchmarkRunStore.SaveAsync(runRoot, document, cancellationToken).ConfigureAwait(false);
        await BenchmarkReports.WriteAsync(runRoot, document, cancellationToken).ConfigureAwait(false);

        await output.WriteLineAsync(
            $"Recorded {(result.Valid ? "valid" : "invalid")} session: {key.RunId}").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> ReportCommandAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var command = ParseRunCommandOptions(arguments, requireExitCode: false, requireRunId: false);
        if (command.Help)
        {
            await output.WriteLineAsync(BenchmarkOptionsParser.Usage).ConfigureAwait(false);
            return 0;
        }

        var runRoot = BenchmarkPaths.ResolveExistingRunRoot(repositoryRoot, command.RunRoot!);
        var document = BenchmarkRunStore.Load(repositoryRoot, runRoot);
        await BenchmarkReports.WriteAsync(runRoot, document, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Benchmark reports refreshed: {runRoot}").ConfigureAwait(false);
        return 0;
    }

    private BenchmarkRunDocument CreateDocument(BenchmarkOptions options, BenchmarkCase[] cases)
    {
        var now = timeProvider.GetUtcNow();
        return new BenchmarkRunDocument
        {
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Configuration = new BenchmarkRunConfiguration
            {
                Model = options.Model,
                ReasoningEffort = options.ReasoningEffort,
                Trials = options.Trials,
                Case = options.Case,
                MaximumResults = options.MaximumResults,
                IndexPath = BenchmarkOptionsParser.NormalizeIndexPath(options.IndexPath),
                RoslynKitPath = BenchmarkPaths.ResolveAppHost(repositoryRoot, options.RoslynKitPath),
                BuildRoslynKit = options.RoslynKitPath is null,
            },
            Cases = cases,
        };
    }

    private async Task PrepareAsync(
        BenchmarkRunConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (configuration.BuildRoslynKit)
        {
            await RunCheckedAsync(
                BenchmarkCommands.BuildRoslynKit(repositoryRoot),
                "Release RoslynKit build",
                cancellationToken).ConfigureAwait(false);
        }

        BenchmarkPaths.ValidateAppHost(configuration.RoslynKitPath);
        await RunCheckedAsync(
            BenchmarkCommands.Index(repositoryRoot, configuration.RoslynKitPath, configuration.IndexPath),
            "text-only index preparation",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RunCheckedAsync(
        ProcessInvocation invocation,
        string operation,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new BenchmarkException(
                $"{operation} failed ({result.ExitCode}): {result.StandardError.Trim()}");
        }
    }

    private async Task WriteDryRunAsync(BenchmarkRunDocument document)
    {
        if (document.Configuration.BuildRoslynKit)
        {
            await error.WriteLineAsync(
                $"Preparation: {BenchmarkCommands.Display(BenchmarkCommands.BuildRoslynKit(repositoryRoot))}").ConfigureAwait(false);
        }

        await error.WriteLineAsync(
            $"Index: {BenchmarkCommands.Display(BenchmarkCommands.Index(repositoryRoot, document.Configuration.RoslynKitPath, document.Configuration.IndexPath))}").ConfigureAwait(false);
        await error.WriteLineAsync("Sessions:").ConfigureAwait(false);
        foreach (var key in BenchmarkSchedule.Create(document))
        {
            var benchmarkCase = document.Cases.Single(candidate => candidate.Id == key.CaseId);
            var retrieval = key.Condition == BenchmarkConditions.RoslynKitSearch
                ? BenchmarkCommands.Display(BenchmarkCommands.Search(
                    repositoryRoot,
                    document.Configuration.RoslynKitPath,
                    document.Configuration.IndexPath,
                    benchmarkCase,
                    document.Configuration.MaximumResults))
                : "controller plain-text ranked excerpt search";
            await error.WriteLineAsync($"- {key.RunId}").ConfigureAwait(false);
            await error.WriteLineAsync($"  Retrieval: {retrieval}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Emits the control directive that the Bash controller consumes on standard output.
    /// </summary>
    private async Task WriteControlAsync(
        string action,
        string? runRoot = null,
        string? model = null,
        string? reasoningEffort = null,
        IReadOnlyList<string>? sessions = null)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("action=").Append(action).Append('\n');
        if (runRoot is not null)
        {
            builder.Append("run-root=").Append(EnsureControlValue(runRoot, "run root")).Append('\n');
        }

        if (model is not null)
        {
            builder.Append("model=").Append(EnsureControlValue(model, "model")).Append('\n');
        }

        if (reasoningEffort is not null)
        {
            builder.Append("reasoning-effort=").Append(EnsureControlValue(reasoningEffort, "reasoning effort")).Append('\n');
        }

        foreach (var session in sessions ?? [])
        {
            builder.Append("session=").Append(EnsureControlValue(session, "session id")).Append('\n');
        }

        await output.WriteAsync(builder.ToString()).ConfigureAwait(false);
    }

    private static BenchmarkSessionKey GetPendingKey(BenchmarkRunDocument document, string runId)
    {
        return BenchmarkSchedule.Pending(document)
            .SingleOrDefault(key => string.Equals(key.RunId, runId, StringComparison.Ordinal))
            ?? throw new BenchmarkException($"Run ID '{runId}' is not pending for this benchmark run.");
    }

    private string GetRetrievalCommand(
        BenchmarkSessionKey key,
        BenchmarkRunConfiguration configuration,
        BenchmarkCase benchmarkCase)
    {
        return key.Condition == BenchmarkConditions.RawText
            ? "controller plain-text ranked excerpt search"
            : BenchmarkCommands.Display(BenchmarkCommands.Search(
                repositoryRoot,
                configuration.RoslynKitPath,
                configuration.IndexPath,
                benchmarkCase,
                configuration.MaximumResults));
    }

    private static async Task<string> ReadArtifactAsync(
        string path,
        string name,
        ICollection<string> issues,
        CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            issues.Add($"{name} artifact was unavailable");
            return string.Empty;
        }
    }

    private static async Task<long> ReadEvidenceBytesAsync(
        string path,
        ICollection<string> issues,
        CancellationToken cancellationToken)
    {
        _ = await ReadArtifactAsync(path, "evidence", issues, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            issues.Add("evidence artifact length was unavailable");
            return 0;
        }
    }

    private static async Task<double> ReadDurationSecondsAsync(
        string timingPath,
        ICollection<string> issues,
        CancellationToken cancellationToken)
    {
        var timing = await ReadArtifactAsync(timingPath, "timing", issues, cancellationToken).ConfigureAwait(false);
        if (!long.TryParse(timing, NumberStyles.Integer, CultureInfo.InvariantCulture, out var started))
        {
            issues.Add("timing artifact did not contain a monotonic timestamp");
            return 0;
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        if (elapsed < 0)
        {
            issues.Add("timing artifact was later than the current monotonic timestamp");
            return 0;
        }

        return elapsed;
    }

    private static RunCommandOptions ParseRunCommandOptions(
        IReadOnlyList<string> arguments,
        bool requireExitCode,
        bool requireRunId)
    {
        string? runRoot = null;
        string? runId = null;
        int? exitCode = null;
        var help = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index++)
        {
            var option = arguments[index];
            switch (option)
            {
                case "--help":
                case "-h":
                    AddOnce(seen, "help", option);
                    help = true;
                    break;
                case "--run-root":
                    AddOnce(seen, "run-root", option);
                    runRoot = ReadValue(arguments, ref index, option);
                    break;
                case "--run-id" when requireRunId:
                    AddOnce(seen, "run-id", option);
                    runId = ReadValue(arguments, ref index, option);
                    break;
                case "--exit-code" when requireExitCode:
                    AddOnce(seen, "exit-code", option);
                    exitCode = ParseInteger(ReadValue(arguments, ref index, option), option);
                    break;
                default:
                    throw new BenchmarkException($"Unknown option for benchmark command: '{option}'.");
            }
        }

        if (help)
        {
            return new RunCommandOptions(runRoot, runId, exitCode, Help: true);
        }

        if (string.IsNullOrWhiteSpace(runRoot))
        {
            throw new BenchmarkException("--run-root is required.");
        }

        if (requireRunId && string.IsNullOrWhiteSpace(runId))
        {
            throw new BenchmarkException("--run-id is required.");
        }

        if (requireExitCode && exitCode is null)
        {
            throw new BenchmarkException("--exit-code is required.");
        }

        return new RunCommandOptions(runRoot, runId, exitCode, Help: false);
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
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
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

    private static bool IsHelp(string value) => value is "--help" or "-h" or "help";

    private static string EnsureControlValue(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains('\r', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal))
        {
            throw new BenchmarkException($"Benchmark {name} must be one nonempty line.");
        }

        return value;
    }

    private static void CreateArtifactDirectories(string runRoot)
    {
        foreach (var child in new[] { "answers", "events", "evidence", "prompts", "stderr", "timing" })
        {
            BenchmarkPaths.EnsureArtifactDirectory(runRoot, child);
        }
    }

    private static SessionArtifacts CreateArtifacts(string runRoot, string runId)
    {
        return new SessionArtifacts(
            Path.Combine(runRoot, "answers", $"{runId}.md"),
            Path.Combine(runRoot, "events", $"{runId}.jsonl"),
            Path.Combine(runRoot, "evidence", $"{runId}.txt"),
            Path.Combine(runRoot, "prompts", $"{runId}.txt"),
            Path.Combine(runRoot, "stderr", $"{runId}.txt"),
            Path.Combine(runRoot, "timing", $"{runId}.txt"));
    }

    private static string ToArtifactPath(string runRoot, string path) =>
        Path.GetRelativePath(runRoot, path).Replace('\\', '/');

    private sealed record RunCommandOptions(string? RunRoot, string? RunId, int? ExitCode, bool Help);

    private sealed record SessionArtifacts(
        string AnswerPath,
        string EventPath,
        string EvidencePath,
        string PromptPath,
        string StderrPath,
        string TimingPath);
}
