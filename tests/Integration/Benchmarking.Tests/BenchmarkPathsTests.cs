namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Verifies run-root filesystem boundary protections.
/// </summary>
public sealed class BenchmarkPathsTests
{
    [Theory]
    [InlineData("artifacts")]
    [InlineData("benchmark")]
    [InlineData("run-root")]
    public void ResolveExistingRunRoot_RejectsSymbolicLinkInRunDirectoryChain(string linkLocation)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repository = new TemporaryBenchmarkRepository();
        var runRoot = CreateExistingRunRootWithSymbolicLink(repository, linkLocation);

        Assert.Throws<BenchmarkException>(() => BenchmarkPaths.ResolveExistingRunRoot(repository.RootPath, runRoot));
    }

    [Fact]
    public void CreateRunRoot_RejectsSymbolicLinkedBenchmarkDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repository = new TemporaryBenchmarkRepository();
        var artifactsRoot = Path.Combine(repository.RootPath, "artifacts");
        var outsideDirectory = Path.Combine(repository.RootPath, "outside-benchmark");
        Directory.CreateDirectory(artifactsRoot);
        Directory.CreateDirectory(outsideDirectory);
        Directory.CreateSymbolicLink(Path.Combine(artifactsRoot, "benchmark"), outsideDirectory);

        Assert.Throws<BenchmarkException>(() => BenchmarkPaths.CreateRunRoot(
            repository.RootPath,
            DateTimeOffset.Parse("2026-08-23T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string CreateExistingRunRootWithSymbolicLink(
        TemporaryBenchmarkRepository repository,
        string linkLocation)
    {
        var artifactsRoot = Path.Combine(repository.RootPath, "artifacts");
        var benchmarkRoot = Path.Combine(artifactsRoot, "benchmark");
        var runRoot = Path.Combine(benchmarkRoot, "20260823-000000");
        var outsideDirectory = Path.Combine(repository.RootPath, "outside");
        switch (linkLocation)
        {
            case "artifacts":
                Directory.CreateDirectory(Path.Combine(outsideDirectory, "benchmark", "20260823-000000"));
                Directory.CreateSymbolicLink(artifactsRoot, outsideDirectory);
                break;
            case "benchmark":
                Directory.CreateDirectory(artifactsRoot);
                Directory.CreateDirectory(Path.Combine(outsideDirectory, "20260823-000000"));
                Directory.CreateSymbolicLink(benchmarkRoot, outsideDirectory);
                break;
            case "run-root":
                Directory.CreateDirectory(benchmarkRoot);
                Directory.CreateDirectory(outsideDirectory);
                Directory.CreateSymbolicLink(runRoot, outsideDirectory);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(linkLocation));
        }

        return runRoot;
    }
}
