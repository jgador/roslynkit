namespace RoslynKit.Tests;

public sealed class RepositoryDiscoveryTests
{
    [Fact]
    public async Task DiscoverAsync_ReturnsTrackedAndUntrackedNonIgnoredProjects()
    {
        await using var repository = await TestRepository.CreateAsync();
        await repository.WriteAsync("src/Tracked/Tracked.csproj", "<Project />");
        await repository.WriteAsync("src/Untracked/Untracked.csproj", "<Project />");
        await repository.WriteAsync("generated/Ignored.csproj", "<Project />");
        await repository.WriteAsync(".gitignore", "generated/\n");
        await repository.RunGitAsync("add", "--", "src/Tracked/Tracked.csproj", ".gitignore");

        var projects = await RepositoryProjectDiscovery.DiscoverAsync(
            repository.RootPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                repository.GetPath("src/Tracked/Tracked.csproj"),
                repository.GetPath("src/Untracked/Untracked.csproj"),
            ],
            projects);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsRepositoryWithoutCSharpProjects()
    {
        await using var repository = await TestRepository.CreateAsync();

        var exception = await Assert.ThrowsAsync<CliUsageException>(
            () => RepositoryProjectDiscovery.DiscoverAsync(
                repository.RootPath,
                TestContext.Current.CancellationToken));

        Assert.Contains("does not contain any tracked or unignored C# project files", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_UsesNearestStandardGitDirectory()
    {
        await using var repository = await TestRepository.CreateAsync();
        var nestedPath = repository.GetPath("src/App");
        Directory.CreateDirectory(nestedPath);

        var context = RepositoryContextResolver.Resolve(nestedPath);

        Assert.Equal(repository.RootPath, context.RootPath);
        Assert.Equal(repository.GetPath(".roslynkit/roslynkit.db"), context.DatabasePath);
    }

    [Fact]
    public async Task Resolve_RejectsGitIndirectionFile()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "roslynkit-tests",
            "git-indirection",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        await File.WriteAllTextAsync(
            Path.Combine(rootPath, ".git"),
            "gitdir: ../worktrees/example\n",
            TestContext.Current.CancellationToken);

        try
        {
            var exception = Assert.Throws<RepositoryContextException>(
                () => RepositoryContextResolver.Resolve(rootPath));

            Assert.Contains("Linked worktrees and submodules are not supported yet", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private sealed class TestRepository : IAsyncDisposable
    {
        private TestRepository(string rootPath)
        {
            RootPath = rootPath;
        }

        public string RootPath { get; }

        public static async Task<TestRepository> CreateAsync()
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                "roslynkit-tests",
                "repository-discovery",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            var repository = new TestRepository(rootPath);
            try
            {
                await repository.RunGitAsync("init");
                return repository;
            }
            catch
            {
                await repository.DisposeAsync();
                throw;
            }
        }

        public string GetPath(string relativePath)
        {
            return Path.Combine(
                RootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        public async Task WriteAsync(string relativePath, string content)
        {
            var path = GetPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(
                path,
                content,
                TestContext.Current.CancellationToken);
        }

        public async Task RunGitAsync(params string[] arguments)
        {
            var result = await ProcessCommandRunner.RunAsync(
                "git",
                RootPath,
                arguments,
                TestContext.Current.CancellationToken);
            Assert.True(
                result.ExitCode == 0,
                $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootPath))
            {
                foreach (var file in Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories))
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                }

                Directory.Delete(RootPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
