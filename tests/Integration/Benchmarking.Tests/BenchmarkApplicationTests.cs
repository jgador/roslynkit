namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Verifies dry-run orchestration without child-process execution.
/// </summary>
public sealed class BenchmarkApplicationTests
{
    [Fact]
    public async Task RunAsync_DryRunDoesNotStartAnyProcess()
    {
        using var repository = new TemporaryBenchmarkRepository();
        repository.Write("src/RoslynKit/Alpha.cs", "internal class Alpha { }\n");
        repository.Write("tests/RoslynKit.Tests/AlphaTests.cs", "internal class AlphaTests { }\n");
        repository.Write(
            "tests/Integration/Benchmarking/cases.json",
            BenchmarkTestData.CatalogJson(BenchmarkTestData.Case()));
        var processRunner = new CountingProcessRunner();
        using var output = new StringWriter();
        var application = new BenchmarkApplication(
            repository.RootPath,
            processRunner,
            output,
            TimeProvider.System);

        var exitCode = await application.RunAsync(
            ["--dry-run", "--trials", "1", "--case", "sample-case"],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, processRunner.InvocationCount);
        Assert.Contains("Search-retrieval benchmark condition: raw-text.", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Search-retrieval benchmark condition: roslynkit-search.", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--text-only --compact --balanced", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReportModeRegeneratesReportsWithoutStartingProcess()
    {
        using var repository = CreateRepository();
        var runRoot = Path.Combine(repository.RootPath, "artifacts", "benchmark", "report-run");
        Directory.CreateDirectory(runRoot);
        var document = BenchmarkTestData.Document(
            sessions:
            [
                BenchmarkTestData.Session(BenchmarkConditions.RawText, 1, 100),
                BenchmarkTestData.Session(BenchmarkConditions.RoslynKitSearch, 1, 75),
            ]);
        await BenchmarkRunStore.SaveAsync(runRoot, document, TestContext.Current.CancellationToken);
        var processRunner = new CountingProcessRunner();
        using var output = new StringWriter();
        var application = new BenchmarkApplication(repository.RootPath, processRunner, output, TimeProvider.System);

        var exitCode = await application.RunAsync(
            ["--report-run-root", "./artifacts/benchmark/report-run"],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, processRunner.InvocationCount);
        Assert.True(File.Exists(Path.Combine(runRoot, "runs.csv")));
        Assert.True(File.Exists(Path.Combine(runRoot, "summary.md")));
    }

    [Fact]
    public async Task RunAsync_CompletedResumeDoesNotPrepareOrStartCodex()
    {
        using var repository = CreateRepository();
        var runRoot = Path.Combine(repository.RootPath, "artifacts", "benchmark", "resume-run");
        Directory.CreateDirectory(runRoot);
        var document = BenchmarkTestData.Document(
            sessions:
            [
                BenchmarkTestData.Session(BenchmarkConditions.RawText, 1, 100),
                BenchmarkTestData.Session(BenchmarkConditions.RoslynKitSearch, 1, 75),
            ]);
        await BenchmarkRunStore.SaveAsync(runRoot, document, TestContext.Current.CancellationToken);
        var processRunner = new CountingProcessRunner();
        using var output = new StringWriter();
        var application = new BenchmarkApplication(repository.RootPath, processRunner, output, TimeProvider.System);

        var exitCode = await application.RunAsync(
            ["--resume-run-root", "./artifacts/benchmark/resume-run"],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, processRunner.InvocationCount);
        Assert.Contains("already complete", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PartialResumeRunsOnlyMissingSessionAndPersistsIt()
    {
        using var repository = CreateRepository();
        var runRoot = Path.Combine(repository.RootPath, "artifacts", "benchmark", "partial-run");
        Directory.CreateDirectory(runRoot);
        var apphostName = OperatingSystem.IsWindows() ? "RoslynKit.exe" : "RoslynKit";
        var apphostPath = Path.Combine(repository.RootPath, apphostName);
        File.WriteAllText(apphostPath, string.Empty);
        var original = BenchmarkTestData.Document(
            sessions: [BenchmarkTestData.Session(BenchmarkConditions.RawText, 1, 100)]);
        original = original with
        {
            Configuration = original.Configuration with
            {
                RoslynKitPath = apphostPath,
                BuildRoslynKit = false,
            },
        };
        await BenchmarkRunStore.SaveAsync(runRoot, original, TestContext.Current.CancellationToken);
        var processRunner = new ResumeProcessRunner();
        using var output = new StringWriter();
        var application = new BenchmarkApplication(repository.RootPath, processRunner, output, TimeProvider.System);

        var exitCode = await application.RunAsync(
            ["--resume-run-root", "./artifacts/benchmark/partial-run"],
            TestContext.Current.CancellationToken);

        var completed = BenchmarkRunStore.Load(repository.RootPath, runRoot);
        Assert.Equal(0, exitCode);
        Assert.Equal(3, processRunner.InvocationCount);
        Assert.Equal(2, completed.Sessions.Count);
        Assert.Contains(completed.Sessions, session => session.Condition == BenchmarkConditions.RoslynKitSearch);
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

    private sealed class CountingProcessRunner : IProcessRunner
    {
        public int InvocationCount { get; private set; }

        public Task<ProcessResult> RunAsync(
            ProcessInvocation invocation,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            throw new InvalidOperationException("Dry-run unexpectedly started a process.");
        }
    }

    private sealed class ResumeProcessRunner : IProcessRunner
    {
        public int InvocationCount { get; private set; }

        public Task<ProcessResult> RunAsync(
            ProcessInvocation invocation,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            if (invocation.FileName == "codex")
            {
                var answerOption = invocation.Arguments.ToList().IndexOf("--output-last-message");
                var answerPath = invocation.Arguments[answerOption + 1];
                File.WriteAllText(
                    answerPath,
                    "src/RoslynKit/Alpha.cs:1 and tests/RoslynKit.Tests/AlphaTests.cs:1");
                return Task.FromResult(new ProcessResult(
                    0,
                    "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":75,\"cached_input_tokens\":0,\"output_tokens\":10,\"reasoning_output_tokens\":4}}\n",
                    string.Empty));
            }

            if (invocation.Arguments[0] == "search")
            {
                return Task.FromResult(new ProcessResult(0, "results: 1/1\n", string.Empty));
            }

            Assert.Equal("index", invocation.Arguments[0]);
            return Task.FromResult(new ProcessResult(0, "command: index\n", string.Empty));
        }
    }
}
