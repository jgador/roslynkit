namespace RoslynKit.Tests;

/// <summary>
/// Verifies canonical repository-relative path persistence and resolution.
/// </summary>
public sealed class RepositoryRelativePathTests
{
    [Fact]
    public void FromPhysicalPath_ReturnsCanonicalRepositoryRelativePathAndResolvesAtTheCurrentRoot()
    {
        var repositoryRoot = TestPaths.RepositoryRoot();

        var path = RepositoryRelativePath.FromPhysicalPath(
            repositoryRoot,
            TestPaths.FixtureProjectPath(),
            "fixture project");

        Assert.Equal("tests/FixtureWorkspace/App/App.csproj", path.Value);
        Assert.Equal(TestPaths.FixtureProjectPath(), path.Resolve(repositoryRoot), ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void FromPhysicalPath_RejectsMissingAndExternalPhysicalFiles()
    {
        var repositoryRoot = TestPaths.RepositoryRoot();
        var missingPath = TestPaths.RepoFile("artifacts", "repository-relative-path", Guid.NewGuid().ToString("N"), "Missing.cs");
        var externalPath = Path.GetTempFileName();

        try
        {
            var missingException = Assert.Throws<ArgumentException>(
                () => RepositoryRelativePath.FromPhysicalPath(repositoryRoot, missingPath, "missing source document"));
            var externalException = Assert.Throws<ArgumentException>(
                () => RepositoryRelativePath.FromPhysicalPath(repositoryRoot, externalPath, "external source document"));

            Assert.Contains("physical", missingException.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("outside", externalException.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(externalPath);
        }
    }

    [Theory]
    [InlineData("tests\\FixtureWorkspace\\App\\App.csproj")]
    [InlineData("./tests/FixtureWorkspace/App/App.csproj")]
    [InlineData("tests/FixtureWorkspace/../App/App.csproj")]
    [InlineData("C:/repo/App.csproj")]
    public void FromStoredValue_RejectsNonCanonicalPathValues(string value)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => RepositoryRelativePath.FromStoredValue(value, "persisted path"));

        Assert.Contains("repository-relative", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
