using System.Text.Json;

namespace RoslynKit.Tests;

public sealed class SolutionFilterWorkspaceLoaderTests
{
    [Fact]
    public async Task LoadAsync_LoadsOnlyProjectsSelectedBySolutionFilter()
    {
        var directory = TestPaths.RepoFile(
            "artifacts",
            "solution-filter-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var filterPath = Path.Combine(directory, "Fixture.slnf");
        var content = JsonSerializer.Serialize(new
        {
            solution = new
            {
                path = TestPaths.SolutionPath(),
                projects = new[] { TestPaths.FixtureProjectPath() },
            },
        });
        await File.WriteAllTextAsync(
            filterPath,
            content,
            TestContext.Current.CancellationToken);

        try
        {
            using var loaded = await RoslynWorkspaceLoader.LoadAsync(
                filterPath,
                TestContext.Current.CancellationToken);

            var project = Assert.Single(loaded.Solution.Projects);
            Assert.Equal(TestPaths.FixtureProjectPath(), project.FilePath);
            Assert.Equal("slnf", loaded.TargetKind);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
