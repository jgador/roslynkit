namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Verifies benchmark preparation option aliases, bounds, and mode restrictions.
/// </summary>
public sealed class BenchmarkOptionsTests
{
    [Fact]
    public void Parse_UsesTerraAndDefaultSuite()
    {
        var parsed = BenchmarkOptionsParser.Parse([]);

        Assert.Equal("gpt-5.6-terra", parsed.Model);
        Assert.Equal("default", parsed.Case);
    }

    [Theory]
    [InlineData("--case")]
    [InlineData("--case-id")]
    public void Parse_AcceptsCaseOptionAndCompatibilityAlias(string option)
    {
        var parsed = BenchmarkOptionsParser.Parse([option, "sample-case"]);

        Assert.Equal("sample-case", parsed.Case);
    }

    [Fact]
    public void Parse_RejectsOutOfRangeAndConflictingOptions()
    {
        Assert.Throws<BenchmarkException>(() => BenchmarkOptionsParser.Parse(["--trials", "0"]));
        Assert.Throws<BenchmarkException>(() => BenchmarkOptionsParser.Parse(["--max-results", "51"]));
        Assert.Throws<BenchmarkException>(() => BenchmarkOptionsParser.Parse(["--report-run-root", "one"]));
        Assert.Throws<BenchmarkException>(() => BenchmarkOptionsParser.Parse(
            ["--dry-run", "--resume-run-root", "one"]));
    }

    [Theory]
    [InlineData("--model")]
    [InlineData("--reasoning-effort")]
    public void Parse_RejectsMultilineControllerValues(string option)
    {
        Assert.Throws<BenchmarkException>(() => BenchmarkOptionsParser.Parse([option, "first\nsecond"]));
    }

    [Fact]
    public void NormalizeIndexPath_RequiresOneDatabaseBelowArtifacts()
    {
        Assert.Equal("./artifacts/custom.db", BenchmarkOptionsParser.NormalizeIndexPath("artifacts/custom.db"));
        Assert.Throws<BenchmarkException>(() => BenchmarkOptionsParser.NormalizeIndexPath("../custom.db"));
        Assert.Throws<BenchmarkException>(() => BenchmarkOptionsParser.NormalizeIndexPath("./artifacts/nested/custom.db"));
    }
}
