using System.Text;

namespace RoslynKit.Benchmarking;

/// <summary>
/// Orchestrates preparation, paired retrieval, isolated Codex judging, persistence, and reporting.
/// </summary>
internal sealed class BenchmarkApplication(
    string repositoryRoot,
    IProcessRunner processRunner,
    TextWriter output,
    TimeProvider timeProvider)
{
    public static BenchmarkApplication CreateDefault()
    {
        var repositoryRoot = BenchmarkPaths.FindRepositoryRoot(Environment.CurrentDirectory);
        return new BenchmarkApplication(repositoryRoot, new ProcessRunner(), Console.Out, TimeProvider.System);
    }

    public async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var options = BenchmarkOptionsParser.Parse(arguments);
        if (options.Help)
        {
            await output.WriteLineAsync(BenchmarkOptionsParser.Usage).ConfigureAwait(false);
            return 0;
        }

        if (options.ReportRunRoot is not null)
        {
            var reportRoot = BenchmarkPaths.ResolveExistingRunRoot(repositoryRoot, options.ReportRunRoot);
            var reportDocument = BenchmarkRunStore.Load(repositoryRoot, reportRoot);
            await BenchmarkReports.WriteAsync(reportRoot, reportDocument, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"Benchmark reports refreshed: {reportRoot}").ConfigureAwait(false);
            return 0;
        }

        if (options.DryRun)
        {
            var dryRunDocument = CreateDocument(options, BenchmarkCatalog.Select(
                BenchmarkCatalog.Load(repositoryRoot),
                options.Case));
            await WriteDryRunAsync(dryRunDocument).ConfigureAwait(false);
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
            await BenchmarkReports.WriteAsync(runRoot, document, cancellationToken).ConfigureAwait(false);
        }

        var pending = BenchmarkSchedule.Pending(document);
        if (pending.Count == 0)
        {
            await BenchmarkReports.WriteAsync(runRoot, document, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"Benchmark already complete: {runRoot}").ConfigureAwait(false);
            return 0;
        }

        CreateArtifactDirectories(runRoot);
        await PrepareAsync(document.Configuration, cancellationToken).ConfigureAwait(false);
        var retrievalService = new BenchmarkRetrievalService(processRunner);
        foreach (var key in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var benchmarkCase = document.Cases.Single(candidate => candidate.Id == key.CaseId);
            var answerPath = Path.Combine(runRoot, "answers", $"{key.RunId}.md");
            var eventPath = Path.Combine(runRoot, "events", $"{key.RunId}.jsonl");
            var evidencePath = Path.Combine(runRoot, "evidence", $"{key.RunId}.txt");
            var stderrPath = Path.Combine(runRoot, "stderr", $"{key.RunId}.txt");
            var retrieval = await retrievalService.RetrieveAsync(
                key.Condition,
                repositoryRoot,
                document.Configuration,
                benchmarkCase,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                evidencePath,
                retrieval.Evidence + "\n",
                cancellationToken).ConfigureAwait(false);

            File.Delete(answerPath);
            var prompt = BenchmarkPrompt.Render(key.Condition, benchmarkCase, retrieval.Evidence);
            var invocation = BenchmarkCommands.Codex(
                repositoryRoot,
                document.Configuration.Model,
                document.Configuration.ReasoningEffort,
                answerPath,
                prompt);
            await output.WriteLineAsync($"[{key.CaseId}] {key.Condition} trial {key.Trial}").ConfigureAwait(false);
            var started = timeProvider.GetTimestamp();
            var processResult = await processRunner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
            var duration = timeProvider.GetElapsedTime(started).TotalSeconds;
            await File.WriteAllTextAsync(eventPath, processResult.StandardOutput, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(stderrPath, processResult.StandardError, cancellationToken).ConfigureAwait(false);
            var answer = File.Exists(answerPath)
                ? await File.ReadAllTextAsync(answerPath, cancellationToken).ConfigureAwait(false)
                : string.Empty;
            var result = BenchmarkSessionEvaluator.Evaluate(
                benchmarkCase,
                key,
                document.Configuration,
                processResult,
                answer,
                new FileInfo(evidencePath).Length,
                duration,
                retrieval.Command,
                ToArtifactPath(runRoot, answerPath),
                ToArtifactPath(runRoot, evidencePath),
                ToArtifactPath(runRoot, eventPath),
                ToArtifactPath(runRoot, stderrPath));
            document.Sessions.Add(result);
            document.UpdatedAtUtc = timeProvider.GetUtcNow();
            await BenchmarkRunStore.SaveAsync(runRoot, document, cancellationToken).ConfigureAwait(false);
            await BenchmarkReports.WriteAsync(runRoot, document, cancellationToken).ConfigureAwait(false);
        }

        var report = BenchmarkReports.Create(document);
        await output.WriteLineAsync($"Benchmark complete: {runRoot}").ConfigureAwait(false);
        await output.WriteLineAsync($"Strict 20% acceptance: {(report.Accepted ? "passed" : "failed")}").ConfigureAwait(false);
        return 0;
    }

    private BenchmarkRunDocument CreateDocument(BenchmarkOptions options, BenchmarkCase[] cases)
    {
        var now = timeProvider.GetUtcNow();
        return new BenchmarkRunDocument
        {
            SchemaVersion = BenchmarkRunStore.SchemaVersion,
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
            await output.WriteLineAsync(
                $"Preparation: {BenchmarkCommands.Display(BenchmarkCommands.BuildRoslynKit(repositoryRoot))}").ConfigureAwait(false);
        }

        await output.WriteLineAsync(
            $"Index: {BenchmarkCommands.Display(BenchmarkCommands.Index(repositoryRoot, document.Configuration.RoslynKitPath, document.Configuration.IndexPath))}").ConfigureAwait(false);
        await output.WriteLineAsync().ConfigureAwait(false);
        foreach (var key in BenchmarkSchedule.Create(document))
        {
            var benchmarkCase = document.Cases.Single(candidate => candidate.Id == key.CaseId);
            var evidence = key.Condition == BenchmarkConditions.RoslynKitSearch
                ? $"<output of {BenchmarkCommands.Display(BenchmarkCommands.Search(repositoryRoot, document.Configuration.RoslynKitPath, document.Configuration.IndexPath, benchmarkCase, document.Configuration.MaximumResults))}>"
                : "<controller-generated bounded plain-text search excerpts>";
            var prompt = BenchmarkPrompt.Render(key.Condition, benchmarkCase, evidence);
            var codex = BenchmarkCommands.Codex(
                repositoryRoot,
                document.Configuration.Model,
                document.Configuration.ReasoningEffort,
                "<answer-path>",
                prompt);
            await output.WriteLineAsync($"[{key.CaseId}] {key.Condition} trial {key.Trial}").ConfigureAwait(false);
            await output.WriteLineAsync($"Codex: {BenchmarkCommands.Display(codex)}").ConfigureAwait(false);
            await output.WriteLineAsync(prompt).ConfigureAwait(false);
            await output.WriteLineAsync().ConfigureAwait(false);
        }
    }

    private static void CreateArtifactDirectories(string runRoot)
    {
        foreach (var child in new[] { "answers", "events", "evidence", "stderr" })
        {
            Directory.CreateDirectory(Path.Combine(runRoot, child));
        }
    }

    private static string ToArtifactPath(string runRoot, string path) =>
        Path.GetRelativePath(runRoot, path).Replace('\\', '/');
}
