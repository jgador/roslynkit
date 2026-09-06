namespace RoslynKit.Tests;

/// <summary>
/// Verifies that explicit repository identities are actual Git roots with normalized directory paths.
/// </summary>
public sealed class ExplicitRepositoryRootTests
{
    [Fact]
    public void ResolveExplicitRoot_RejectsRelativePath()
    {
        var exception = Assert.Throws<RepositoryContextException>(() => RepositoryContextResolver.ResolveExplicitRoot(".", TestContext.Current.CancellationToken));
        Assert.Contains("fully qualified", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveExplicitRoot_RejectsNestedDirectory()
    {
        await using var area = await PreparationTestArea.CreateAsync();
        var nested = Path.Combine(area.RootPath, "nested");
        Directory.CreateDirectory(nested);

        var exception = Assert.Throws<RepositoryContextException>(() => RepositoryContextResolver.ResolveExplicitRoot(nested, TestContext.Current.CancellationToken));

        Assert.Contains("actual Git repository root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveExplicitRoot_RejectsIndirectionFile()
    {
        await using var area = await PreparationTestArea.CreateAsync();
        var nested = Path.Combine(area.RootPath, "linked-worktree");
        await area.WriteAsync("linked-worktree/.git", "gitdir: ../.git/worktrees/example\n");

        var exception = Assert.Throws<RepositoryContextException>(() => RepositoryContextResolver.ResolveExplicitRoot(nested, TestContext.Current.CancellationToken));

        Assert.Contains("Linked worktrees", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveExplicitRoot_RejectsDirectoryThatOnlyLooksLikeGitRepository()
    {
        await using var area = await PreparationTestArea.CreateAsync();
        var nested = Path.Combine(area.RootPath, "not-git");
        Directory.CreateDirectory(Path.Combine(nested, ".git"));

        var exception = Assert.Throws<RepositoryContextException>(() => RepositoryContextResolver.ResolveExplicitRoot(nested, TestContext.Current.CancellationToken));

        Assert.Contains("not a valid Git repository root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveExplicitRoot_NormalizesChainedDirectorySymlinks()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creating directory symlinks without additional privileges is covered on Linux.");
        await using var area = await PreparationTestArea.CreateAsync();
        var firstLink = Path.Combine(area.RootPath, "first-link");
        var secondLink = Path.Combine(area.RootPath, "second-link");
        Directory.CreateSymbolicLink(firstLink, area.RootPath);
        Directory.CreateSymbolicLink(secondLink, firstLink);
        try
        {
            var context = RepositoryContextResolver.ResolveExplicitRoot(secondLink + Path.DirectorySeparatorChar, TestContext.Current.CancellationToken);
            Assert.Equal(area.RootPath, context.RootPath);
        }
        finally
        {
            Directory.Delete(secondLink);
            Directory.Delete(firstLink);
        }
    }
}
