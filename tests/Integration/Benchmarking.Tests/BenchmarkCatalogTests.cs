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
              "cases": [{
                "id": "sample-case",
                "isDefault": true,
                "intent": "intent",
                "query": 42,
                "requiredEvidenceGroups": [["src/RoslynKit/Alpha.cs"]]
              }]
            }
            """);

        Assert.Throws<BenchmarkException>(() => BenchmarkCatalog.Load(repository.RootPath));
    }

    [Theory]
    [InlineData("{\"cases\":null}")]
    [InlineData("{\"cases\":[{\"id\":\"sample-case\",\"isDefault\":true,\"intent\":\"intent\",\"query\":\"query\",\"requiredEvidenceGroups\":null}]}")]
    [InlineData("{\"cases\":[{\"id\":\"sample-case\",\"isDefault\":true,\"intent\":\"intent\",\"query\":\"query\",\"requiredEvidenceGroups\":[null]}]}")]
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
    public void Load_RejectsCatalogWithoutDefaultCase()
    {
        using var repository = CreateValidRepository();
        repository.Write(
            "tests/Integration/Benchmarking/cases.json",
            BenchmarkTestData.CatalogJson(BenchmarkTestData.Case(isDefault: false)));

        var exception = Assert.Throws<BenchmarkException>(() => BenchmarkCatalog.Load(repository.RootPath));

        Assert.Contains("default case", exception.Message, StringComparison.Ordinal);
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
    public void Select_DefaultReturnsMarkedCasesInCatalogOrder()
    {
        var cases = new[]
        {
            BenchmarkTestData.Case("first", isDefault: true),
            BenchmarkTestData.Case("optional", isDefault: false),
            BenchmarkTestData.Case("last", isDefault: true),
        };

        var selected = BenchmarkCatalog.Select(cases, "default");

        Assert.Equal(["first", "last"], selected.Select(benchmarkCase => benchmarkCase.Id));
    }

    [Fact]
    public void CheckedInCatalog_PreservesSearchFirstDefaultSuiteAndOptionalCases()
    {
        var repositoryRoot = BenchmarkPaths.FindRepositoryRoot(AppContext.BaseDirectory);

        var cases = BenchmarkCatalog.Load(repositoryRoot);

        Assert.Equal(
            [
                "search-option-parsing",
                "search-query-tokenization",
                "text-only-workspace",
                "search-corpus-building",
                "search-result-ranking",
                "search-command-flow",
            ],
            BenchmarkCatalog.Select(cases, "default").Select(benchmarkCase => benchmarkCase.Id));
        Assert.Equal(
            [
                "daemon-disconnect",
                "workspace-generation",
                "stale-search-index",
                "symbol-comments",
            ],
            cases.Where(benchmarkCase => !benchmarkCase.IsDefault).Select(benchmarkCase => benchmarkCase.Id));
        Assert.All(cases, benchmarkCase => Assert.NotEmpty(benchmarkCase.RequiredEvidenceGroups));
    }

    private static TemporaryBenchmarkRepository CreateValidRepository()
    {
        var repository = new TemporaryBenchmarkRepository();
        repository.Write("src/RoslynKit/Alpha.cs", "internal class Alpha { }\n");
        repository.Write("tests/RoslynKit.Tests/AlphaTests.cs", "internal class AlphaTests { }\n");
        return repository;
    }
}
