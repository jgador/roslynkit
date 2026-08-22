namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Verifies the file-backed benchmark helper command surfaces.
/// </summary>
public sealed class BenchmarkApplicationTests
{
    [Fact]
    public async Task RunAsync_PrepareDryRunPrintsPlanWithoutCreatingRunOrStartingProcess()
    {
        using var repository = CreateRepository();
        var processRunner = new CountingProcessRunner();
        using var output = new StringWriter();
        var application = CreateApplication(repository, processRunner, output);

        var exitCode = await application.RunAsync(
            ["prepare", "--dry-run", "--trials", "1", "--case", "sample-case"],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, processRunner.InvocationCount);
        Assert.False(Directory.Exists(Path.Combine(repository.RootPath, "artifacts", "benchmark")));
        Assert.Contains("Preparation:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Sessions:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--text-only --compact --balanced", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Codex:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PrepareCreatesRunBuildsIndexAndPrintsOnlyRunRoot()
    {
        using var repository = CreateRepository();
        var apphostPath = CreateAppHost(repository);
        var processRunner = new SuccessfulProcessRunner();
        using var output = new StringWriter();
        var application = CreateApplication(repository, processRunner, output);

        var exitCode = await application.RunAsync(
            ["prepare", "--case", "sample-case", "--roslynkit-path", apphostPath],
            TestContext.Current.CancellationToken);

        var runRoot = output.ToString().Trim();
        var document = BenchmarkRunStore.Load(repository.RootPath, runRoot);
        Assert.Equal(0, exitCode);
        Assert.True(Path.IsPathFullyQualified(runRoot));
        Assert.Equal(runRoot + Environment.NewLine, output.ToString());
        Assert.Equal(1, processRunner.InvocationCount);
        Assert.False(document.Configuration.BuildRoslynKit);
        Assert.Equal(
            BenchmarkSchedule.Pending(document).Select(key => key.RunId),
            File.ReadAllLines(Path.Combine(runRoot, "schedule.txt")));
        Assert.Equal("gpt-5.6-terra\n", File.ReadAllText(Path.Combine(runRoot, "model.txt")));
        Assert.Equal("high\n", File.ReadAllText(Path.Combine(runRoot, "reasoning-effort.txt")));
        Assert.True(File.Exists(Path.Combine(runRoot, "runs.csv")));
        Assert.True(File.Exists(Path.Combine(runRoot, "summary.md")));
    }

    [Fact]
    public async Task RunAsync_PrepareResumeWritesOnlyPendingScheduleEntries()
    {
        using var repository = CreateRepository();
        var runRoot = CreateRunRoot(repository, "partial-run");
        var document = CreateRunDocument(
            repository,
            sessions: [BenchmarkTestData.Session(BenchmarkConditions.RawText, 1, 100)]);
        await BenchmarkRunStore.SaveAsync(runRoot, document, TestContext.Current.CancellationToken);
        var processRunner = new SuccessfulProcessRunner();
        using var output = new StringWriter();
        var application = CreateApplication(repository, processRunner, output);

        var exitCode = await application.RunAsync(
            ["prepare", "--resume-run-root", runRoot],
            TestContext.Current.CancellationToken);

        var pending = Assert.Single(BenchmarkSchedule.Pending(BenchmarkRunStore.Load(repository.RootPath, runRoot)));
        Assert.Equal(0, exitCode);
        Assert.Equal(runRoot + Environment.NewLine, output.ToString());
        Assert.Equal(1, processRunner.InvocationCount);
        Assert.Equal([pending.RunId], File.ReadAllLines(Path.Combine(runRoot, "schedule.txt")));
    }

    [Fact]
    public async Task RunAsync_PrepareSessionWritesDeterministicArtifactsAndRemovesStaleJudgeFiles()
    {
        using var repository = CreateRepository();
        var runRoot = CreateRunRoot(repository, "prepare-session-run");
        await BenchmarkRunStore.SaveAsync(
            runRoot,
            CreateRunDocument(repository),
            TestContext.Current.CancellationToken);
        var key = new BenchmarkSessionKey("sample-case", BenchmarkConditions.RawText, 1);
        var answerPath = Path.Combine(runRoot, "answers", $"{key.RunId}.md");
        var eventPath = Path.Combine(runRoot, "events", $"{key.RunId}.jsonl");
        var stderrPath = Path.Combine(runRoot, "stderr", $"{key.RunId}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(answerPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(eventPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(stderrPath)!);
        File.WriteAllText(answerPath, "stale answer");
        File.WriteAllText(eventPath, "stale event");
        File.WriteAllText(stderrPath, "stale error");
        var processRunner = new CountingProcessRunner();
        using var output = new StringWriter();
        var application = CreateApplication(repository, processRunner, output);

        var exitCode = await application.RunAsync(
            ["prepare-session", "--run-root", runRoot, "--run-id", key.RunId],
            TestContext.Current.CancellationToken);

        var promptPath = Path.Combine(runRoot, "prompts", $"{key.RunId}.txt");
        var evidencePath = Path.Combine(runRoot, "evidence", $"{key.RunId}.txt");
        var timingPath = Path.Combine(runRoot, "timing", $"{key.RunId}.txt");
        Assert.Equal(0, exitCode);
        Assert.Equal(0, processRunner.InvocationCount);
        Assert.Equal(promptPath + Environment.NewLine, output.ToString());
        Assert.True(File.Exists(promptPath));
        Assert.True(File.Exists(evidencePath));
        Assert.True(long.TryParse(File.ReadAllText(timingPath), out _));
        Assert.False(File.Exists(answerPath));
        Assert.False(File.Exists(eventPath));
        Assert.False(File.Exists(stderrPath));
    }

    [Fact]
    public async Task RunAsync_PrepareSessionRejectsSymbolicLinkedArtifactDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repository = CreateRepository();
        var runRoot = CreateRunRoot(repository, "symbolic-link-session-run");
        await BenchmarkRunStore.SaveAsync(
            runRoot,
            CreateRunDocument(repository),
            TestContext.Current.CancellationToken);
        var outsideDirectory = Path.Combine(repository.RootPath, "outside-answers");
        Directory.CreateDirectory(outsideDirectory);
        Directory.CreateSymbolicLink(Path.Combine(runRoot, "answers"), outsideDirectory);
        var key = new BenchmarkSessionKey("sample-case", BenchmarkConditions.RawText, 1);
        var processRunner = new CountingProcessRunner();
        using var output = new StringWriter();
        var application = CreateApplication(repository, processRunner, output);

        await Assert.ThrowsAsync<BenchmarkException>(() => application.RunAsync(
            ["prepare-session", "--run-root", runRoot, "--run-id", key.RunId],
            TestContext.Current.CancellationToken));

        Assert.Equal(0, processRunner.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_EvaluateSessionPersistsFileBackedResultAndRejectsDuplicateRunId()
    {
        using var repository = CreateRepository();
        var runRoot = CreateRunRoot(repository, "evaluate-session-run");
        await BenchmarkRunStore.SaveAsync(
            runRoot,
            CreateRunDocument(repository),
            TestContext.Current.CancellationToken);
        var key = new BenchmarkSessionKey("sample-case", BenchmarkConditions.RawText, 1);
        WriteJudgeArtifacts(
            runRoot,
            key,
            "src/RoslynKit/Alpha.cs:1 and tests/RoslynKit.Tests/AlphaTests.cs:1",
            "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":100,\"cached_input_tokens\":0,\"output_tokens\":10,\"reasoning_output_tokens\":4}}\n");
        var processRunner = new CountingProcessRunner();
        using var output = new StringWriter();
        var application = CreateApplication(repository, processRunner, output);
        var arguments = new[] { "evaluate-session", "--run-root", runRoot, "--run-id", key.RunId, "--exit-code", "0" };

        var exitCode = await application.RunAsync(arguments, TestContext.Current.CancellationToken);
        var document = BenchmarkRunStore.Load(repository.RootPath, runRoot);
        var result = Assert.Single(document.Sessions);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, processRunner.InvocationCount);
        Assert.True(result.Valid);
        Assert.True(result.Correct);
        Assert.Equal($"answers/{key.RunId}.md", result.AnswerPath);
        Assert.Equal($"evidence/{key.RunId}.txt", result.EvidencePath);
        Assert.Equal($"events/{key.RunId}.jsonl", result.EventPath);
        Assert.Equal($"stderr/{key.RunId}.txt", result.StderrPath);
        Assert.True(File.Exists(Path.Combine(runRoot, "runs.csv")));
        Assert.True(File.Exists(Path.Combine(runRoot, "summary.md")));
        Assert.Contains("Recorded valid session", output.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<BenchmarkException>(() => application.RunAsync(arguments, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_EvaluateSessionRecordsInvalidJudgeResultAndReturnsSuccess()
    {
        using var repository = CreateRepository();
        var runRoot = CreateRunRoot(repository, "invalid-session-run");
        await BenchmarkRunStore.SaveAsync(
            runRoot,
            CreateRunDocument(repository),
            TestContext.Current.CancellationToken);
        var key = new BenchmarkSessionKey("sample-case", BenchmarkConditions.RawText, 1);
        WriteJudgeArtifacts(runRoot, key, string.Empty, "not JSON\n");
        var processRunner = new CountingProcessRunner();
        using var output = new StringWriter();
        var application = CreateApplication(repository, processRunner, output);

        var exitCode = await application.RunAsync(
            ["evaluate-session", "--run-root", runRoot, "--run-id", key.RunId, "--exit-code", "1"],
            TestContext.Current.CancellationToken);

        var result = Assert.Single(BenchmarkRunStore.Load(repository.RootPath, runRoot).Sessions);
        Assert.Equal(0, exitCode);
        Assert.False(result.Valid);
        Assert.Contains("codex exited with 1", result.Issues);
        Assert.Contains("Recorded invalid session", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReportRefreshesReportsWithoutStartingProcess()
    {
        using var repository = CreateRepository();
        var runRoot = CreateRunRoot(repository, "report-run");
        var document = CreateRunDocument(
            repository,
            sessions:
            [
                BenchmarkTestData.Session(BenchmarkConditions.RawText, 1, 100),
                BenchmarkTestData.Session(BenchmarkConditions.RoslynKitSearch, 1, 75),
            ]);
        await BenchmarkRunStore.SaveAsync(runRoot, document, TestContext.Current.CancellationToken);
        var processRunner = new CountingProcessRunner();
        using var output = new StringWriter();
        var application = CreateApplication(repository, processRunner, output);

        var exitCode = await application.RunAsync(
            ["report", "--run-root", runRoot],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, processRunner.InvocationCount);
        Assert.True(File.Exists(Path.Combine(runRoot, "runs.csv")));
        Assert.True(File.Exists(Path.Combine(runRoot, "summary.md")));
        Assert.Contains("Benchmark reports refreshed", output.ToString(), StringComparison.Ordinal);
    }

    private static BenchmarkApplication CreateApplication(
        TemporaryBenchmarkRepository repository,
        IProcessRunner processRunner,
        TextWriter output)
    {
        return new BenchmarkApplication(repository.RootPath, processRunner, output, TimeProvider.System);
    }

    private static TemporaryBenchmarkRepository CreateRepository()
    {
        var repository = new TemporaryBenchmarkRepository();
        repository.Write("src/RoslynKit/Alpha.cs", "internal class Alpha { }\n");
        repository.Write("tests/RoslynKit.Tests/AlphaTests.cs", "internal class AlphaTests { }\n");
        repository.Write(
            "tests/Integration/Benchmarking/cases.json",
            BenchmarkTestData.CatalogJson(BenchmarkTestData.Case()));
        return repository;
    }

    private static BenchmarkRunDocument CreateRunDocument(
        TemporaryBenchmarkRepository repository,
        IEnumerable<BenchmarkSessionResult>? sessions = null)
    {
        var document = BenchmarkTestData.Document(sessions: sessions);
        return document with
        {
            Configuration = document.Configuration with
            {
                RoslynKitPath = CreateAppHost(repository),
                BuildRoslynKit = false,
            },
        };
    }

    private static string CreateAppHost(TemporaryBenchmarkRepository repository)
    {
        var fileName = OperatingSystem.IsWindows() ? "RoslynKit.exe" : "RoslynKit";
        var path = Path.Combine(repository.RootPath, fileName);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static string CreateRunRoot(TemporaryBenchmarkRepository repository, string name)
    {
        var runRoot = Path.Combine(repository.RootPath, "artifacts", "benchmark", name);
        Directory.CreateDirectory(runRoot);
        return runRoot;
    }

    private static void WriteJudgeArtifacts(
        string runRoot,
        BenchmarkSessionKey key,
        string answer,
        string events)
    {
        var answerPath = Path.Combine(runRoot, "answers", $"{key.RunId}.md");
        var eventPath = Path.Combine(runRoot, "events", $"{key.RunId}.jsonl");
        var evidencePath = Path.Combine(runRoot, "evidence", $"{key.RunId}.txt");
        var stderrPath = Path.Combine(runRoot, "stderr", $"{key.RunId}.txt");
        var timingPath = Path.Combine(runRoot, "timing", $"{key.RunId}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(answerPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(eventPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(stderrPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(timingPath)!);
        File.WriteAllText(answerPath, answer);
        File.WriteAllText(eventPath, events);
        File.WriteAllText(evidencePath, "retrieved evidence\n");
        File.WriteAllText(stderrPath, string.Empty);
        File.WriteAllText(timingPath, System.Diagnostics.Stopwatch.GetTimestamp().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class CountingProcessRunner : IProcessRunner
    {
        public int InvocationCount { get; private set; }

        public Task<ProcessResult> RunAsync(
            ProcessInvocation invocation,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            throw new InvalidOperationException("This command unexpectedly started a process.");
        }
    }

    private sealed class SuccessfulProcessRunner : IProcessRunner
    {
        public int InvocationCount { get; private set; }

        public Task<ProcessResult> RunAsync(
            ProcessInvocation invocation,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            return Task.FromResult(invocation.Arguments[0] switch
            {
                "index" => new ProcessResult(0, "command: index\n", string.Empty),
                "search" => new ProcessResult(0, "results: 1/1\n", string.Empty),
                _ => throw new InvalidOperationException($"Unexpected process: {invocation.FileName}"),
            });
        }
    }
}
