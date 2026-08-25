using Microsoft.CodeAnalysis;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies the socket-free source workspace used by text-only search.
/// </summary>
public sealed class TextOnlyWorkspaceLoaderTests
{
    [Fact]
    public async Task LoadTextOnlyAsync_LoadsRepositorySourcesWithoutMSBuildWorkspace()
    {
        using var loaded = await RoslynWorkspaceLoader.LoadTextOnlyAsync(
            TestPaths.SolutionPath(),
            TestContext.Current.CancellationToken);

        Assert.IsType<AdhocWorkspace>(loaded.Workspace);
        var project = Assert.Single(loaded.Solution.Projects);
        Assert.Contains(
            project.Documents,
            document => string.Equals(
                document.FilePath,
                TestPaths.RepoFile("src", "RoslynKit", "SearchCommandService.cs"),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Assert.DoesNotContain(
            project.Documents,
            document => document.FilePath?.Contains(
                $"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase) == true);
    }
}
