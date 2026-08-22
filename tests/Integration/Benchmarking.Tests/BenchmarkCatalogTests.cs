namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Verifies strict benchmark catalog validation and selection.
/// </summary>
public sealed class BenchmarkCatalogTests
{
    [Fact]
    public void Load_RejectsUnknownCatalogProperty()
    {
        using var repository = CreateValidRepository();
        repository.Write(
            "tests/Integration/Benchmarking/cases.json",
            BenchmarkTestData.CatalogJson(BenchmarkTestData.Case(), "unexpected"));

        var exception = Assert.Throws<BenchmarkException>(() => BenchmarkCatalog.Load(repository.RootPath));

        Assert.Contains("strict JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsMalformedCaseProperty()
    {
        using var repository = CreateValidRepository();
        repository.Write(
            "tests/Integration/Benchmarking/cases.json",
            """
            {
              "schemaVersion": 1,
              "cases": [{
                "id": "sample-case",
                "intent": "intent",
                "query": 42,
                "requiredEvidenceGroups": [["src/RoslynKit/Alpha.cs"]]
              }]
            }
            """);

        Assert.Throws<BenchmarkException>(() => BenchmarkCatalog.Load(repository.RootPath));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"cases\":null}")]
    [InlineData("{\"schemaVersion\":1,\"cases\":[{\"id\":\"sample-case\",\"intent\":\"intent\",\"query\":\"query\",\"requiredEvidenceGroups\":null}]}")]
    [InlineData("{\"schemaVersion\":1,\"cases\":[{\"id\":\"sample-case\",\"intent\":\"intent\",\"query\":\"query\",\"requiredEvidenceGroups\":[null]}]}")]
    public void Load_RejectsNullCollections(string json)
    {
        using var repository = CreateValidRepository();
        repository.Write("tests/Integration/Benchmarking/cases.json", json);

        Assert.Throws<BenchmarkException>(() => BenchmarkCatalog.Load(repository.RootPath));
    }

    [Theory]
    [InlineData("../Alpha.cs")]
    [InlineData("src/RoslynKit/Missing.cs")]
    [InlineData("docs/Alpha.cs")]
    [InlineData("src\\RoslynKit\\Alpha.cs")]
    public void Load_RejectsInvalidEvidencePath(string evidencePath)
    {
        using var repository = CreateValidRepository();
        var benchmarkCase = BenchmarkTestData.Case(evidenceGroups: [[evidencePath]]);
        repository.Write(
            "tests/Integration/Benchmarking/cases.json",
            BenchmarkTestData.CatalogJson(benchmarkCase));

        Assert.Throws<BenchmarkException>(() => BenchmarkCatalog.Load(repository.RootPath));
    }

    [Fact]
    public void Select_ReturnsRequestedCaseAndRejectsUnknownCase()
    {
        var cases = new[] { BenchmarkTestData.Case("first"), BenchmarkTestData.Case("second") };

        var selected = BenchmarkCatalog.Select(cases, "second");

        Assert.Equal("second", Assert.Single(selected).Id);
        Assert.Throws<BenchmarkException>(() => BenchmarkCatalog.Select(cases, "missing"));
    }

    [Fact]
    public void CheckedInCatalog_PreservesTheSixSearchTextCases()
    {
        var repositoryRoot = BenchmarkPaths.FindRepositoryRoot(AppContext.BaseDirectory);

        var cases = BenchmarkCatalog.Load(repositoryRoot);

        Assert.Equal(
            [
                "daemon-disconnect",
                "workspace-generation",
                "stale-search-index",
                "symbol-comments",
                "text-only-workspace-routing",
                "search-ranking-pipeline",
            ],
            cases.Select(benchmarkCase => benchmarkCase.Id));
        Assert.All(cases, benchmarkCase => Assert.NotEmpty(benchmarkCase.RequiredEvidenceGroups));
        Assert.Contains("bypass daemon routing", cases[4].Intent, StringComparison.Ordinal);
        Assert.Contains("query-term coverage", cases[5].Intent, StringComparison.Ordinal);
    }

    private static TemporaryBenchmarkRepository CreateValidRepository()
    {
        var repository = new TemporaryBenchmarkRepository();
        repository.Write("src/RoslynKit/Alpha.cs", "internal class Alpha { }\n");
        repository.Write("tests/RoslynKit.Tests/AlphaTests.cs", "internal class AlphaTests { }\n");
        return repository;
    }
}
