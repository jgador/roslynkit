namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Verifies canonical-document hydration and missing-session resume selection.
/// </summary>
public sealed class BenchmarkResumeTests
{
    [Fact]
    public async Task LoadAndPending_RetainCompletedSessionAndScheduleOnlyMissingTuple()
    {
        using var repository = new TemporaryBenchmarkRepository();
        repository.Write("src/RoslynKit/Alpha.cs", "internal class Alpha { }\n");
        repository.Write("tests/RoslynKit.Tests/AlphaTests.cs", "internal class AlphaTests { }\n");
        var runRoot = Path.Combine(repository.RootPath, "artifacts", "benchmark", "20260822-000000");
        Directory.CreateDirectory(runRoot);
        var document = BenchmarkTestData.Document(
            sessions: [BenchmarkTestData.Session(BenchmarkConditions.RawText, 1, 100)]);
        await BenchmarkRunStore.SaveAsync(runRoot, document, TestContext.Current.CancellationToken);

        var loaded = BenchmarkRunStore.Load(repository.RootPath, runRoot);
        var pending = BenchmarkSchedule.Pending(loaded);

        Assert.Single(loaded.Sessions);
        var key = Assert.Single(pending);
        Assert.Equal(BenchmarkConditions.RoslynKitSearch, key.Condition);
        Assert.Equal(1, key.Trial);
    }

    [Fact]
    public async Task Load_RejectsUnknownPropertiesAndDuplicateSessions()
    {
        using var repository = new TemporaryBenchmarkRepository();
        repository.Write("src/RoslynKit/Alpha.cs", "internal class Alpha { }\n");
        repository.Write("tests/RoslynKit.Tests/AlphaTests.cs", "internal class AlphaTests { }\n");
        var runRoot = Path.Combine(repository.RootPath, "artifacts", "benchmark", "20260822-000000");
        Directory.CreateDirectory(runRoot);
        var document = BenchmarkTestData.Document(
            sessions:
            [
                BenchmarkTestData.Session(BenchmarkConditions.RawText, 1, 100),
                BenchmarkTestData.Session(BenchmarkConditions.RawText, 1, 100),
            ]);
        await BenchmarkRunStore.SaveAsync(runRoot, document, TestContext.Current.CancellationToken);

        Assert.Throws<BenchmarkException>(() => BenchmarkRunStore.Load(repository.RootPath, runRoot));

        var runPath = Path.Combine(runRoot, BenchmarkRunStore.FileName);
        var json = System.Text.Json.JsonSerializer.Serialize(
            BenchmarkTestData.Document(),
            BenchmarkJson.Options);
        File.WriteAllText(runPath, json.Insert(json.LastIndexOf('}'), ",\n  \"unknown\": true\n"));
        Assert.Throws<BenchmarkException>(() => BenchmarkRunStore.Load(repository.RootPath, runRoot));
    }
}
