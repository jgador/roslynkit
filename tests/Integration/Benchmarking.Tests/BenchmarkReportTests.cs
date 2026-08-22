namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Verifies strict pair acceptance and report derivation from canonical state.
/// </summary>
public sealed class BenchmarkReportTests
{
    [Fact]
    public void Create_AcceptsOnlyWhenEveryPairSavesAtLeastTwentyPercent()
    {
        var accepted = BenchmarkTestData.Document(
            trials: 2,
            sessions:
            [
                BenchmarkTestData.Session(BenchmarkConditions.RawText, 1, 100),
                BenchmarkTestData.Session(BenchmarkConditions.RoslynKitSearch, 1, 80),
                BenchmarkTestData.Session(BenchmarkConditions.RawText, 2, 200),
                BenchmarkTestData.Session(BenchmarkConditions.RoslynKitSearch, 2, 100),
            ]);
        var belowThreshold = accepted with
        {
            Sessions =
            [
                BenchmarkTestData.Session(BenchmarkConditions.RawText, 1, 100),
                BenchmarkTestData.Session(BenchmarkConditions.RoslynKitSearch, 1, 81),
                BenchmarkTestData.Session(BenchmarkConditions.RawText, 2, 200),
                BenchmarkTestData.Session(BenchmarkConditions.RoslynKitSearch, 2, 100),
            ],
        };

        Assert.True(BenchmarkReports.Create(accepted).Accepted);
        Assert.False(BenchmarkReports.Create(belowThreshold).Accepted);
    }

    [Fact]
    public void Create_RejectsZeroTokenRoslynKitSession()
    {
        var document = BenchmarkTestData.Document(
            sessions:
            [
                BenchmarkTestData.Session(BenchmarkConditions.RawText, 1, 100),
                BenchmarkTestData.Session(BenchmarkConditions.RoslynKitSearch, 1, 0),
            ]);

        var report = BenchmarkReports.Create(document);

        Assert.False(report.Accepted);
        Assert.False(Assert.Single(report.Pairs).Comparable);
    }

    [Fact]
    public async Task WriteAsync_DerivesCsvAndMarkdownFromDocument()
    {
        using var repository = new TemporaryBenchmarkRepository();
        var document = BenchmarkTestData.Document(
            sessions:
            [
                BenchmarkTestData.Session(BenchmarkConditions.RawText, 1, 100),
                BenchmarkTestData.Session(BenchmarkConditions.RoslynKitSearch, 1, 75),
            ]);

        await BenchmarkReports.WriteAsync(repository.RootPath, document, TestContext.Current.CancellationToken);

        var csv = await File.ReadAllTextAsync(
            Path.Combine(repository.RootPath, "runs.csv"),
            TestContext.Current.CancellationToken);
        var markdown = await File.ReadAllTextAsync(
            Path.Combine(repository.RootPath, "summary.md"),
            TestContext.Current.CancellationToken);
        Assert.Contains("sample-case,raw-text", csv, StringComparison.Ordinal);
        Assert.Contains("Minimum input-token savings: 25.00%", markdown, StringComparison.Ordinal);
        Assert.Contains("saved at least 20%: yes", markdown, StringComparison.Ordinal);
    }
}
