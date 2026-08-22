namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Verifies benchmark option aliases, bounds, and mutually exclusive modes.
/// </summary>
public sealed class BenchmarkOptionsTests
{
    [Theory]
    [InlineData("--case")]
    [InlineData("--case-id")]
    public void Parse_AcceptsCaseOptionAndCompatibilityAlias(string option)
    {
        var parsed = BenchmarkOptionsParser.Parse([option, "daemon-disconnect"]);

        Assert.Equal("daemon-disconnect", parsed.Case);
    }

    [Fact]
    public void Parse_RejectsOutOfRangeAndConflictingOptions()
    {
        Assert.Throws<BenchmarkException>(() => BenchmarkOptionsParser.Parse(["--trials", "0"]));
        Assert.Throws<BenchmarkException>(() => BenchmarkOptionsParser.Parse(["--max-results", "51"]));
        Assert.Throws<BenchmarkException>(() => BenchmarkOptionsParser.Parse(
            ["--resume-run-root", "one", "--report-run-root", "two"]));
        Assert.Throws<BenchmarkException>(() => BenchmarkOptionsParser.Parse(
            ["--dry-run", "--resume-run-root", "one"]));
    }

    [Fact]
    public void NormalizeIndexPath_RequiresOneDatabaseBelowArtifacts()
    {
        Assert.Equal("./artifacts/custom.db", BenchmarkOptionsParser.NormalizeIndexPath("artifacts/custom.db"));
        Assert.Throws<BenchmarkException>(() => BenchmarkOptionsParser.NormalizeIndexPath("../custom.db"));
        Assert.Throws<BenchmarkException>(() => BenchmarkOptionsParser.NormalizeIndexPath("./artifacts/nested/custom.db"));
    }
}
