using System.Text;

namespace RoslynKit.Tests;

public sealed class SearchIndexPolicyTests
{
    [Fact]
    public async Task ResolveAsync_ResolvesRelativeDatabasePathFromProcessDirectory()
    {
        await using var repository = await GitRepository.CreateAsync("artifacts/\n");
        var indexPath = Path.GetRelativePath(
            Environment.CurrentDirectory,
            repository.GetPath("artifacts/roslynkit.db"));

        var result = await new SearchIndexPathPolicy().ResolveAsync(
            repository.GetPath("src/App.csproj"),
            indexPath,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccessful, result.Diagnostic);
        Assert.Equal(repository.RootPath, result.Path!.RepositoryRoot);
        Assert.Equal("artifacts/roslynkit.db", result.Path.RelativeDatabasePath);
        Assert.Equal(
            Path.GetFullPath(repository.GetPath("artifacts/roslynkit.db")),
            result.Path.DatabasePath);
        Assert.False(File.Exists(result.Path.DatabasePath));
    }

    [Fact]
    public async Task ResolveAsync_RejectsDatabasePathOutsideTargetRepository()
    {
        await using var repository = await GitRepository.CreateAsync("artifacts/\n");
        var outsidePath = Path.Combine(Path.GetTempPath(), "roslynkit-search-index-outside", "index.db");

        var result = await new SearchIndexPathPolicy().ResolveAsync(
            repository.GetPath("src/App.csproj"),
            outsidePath,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.Equal(SearchIndexPathFailureKind.OutsideRepository, result.FailureKind);
        Assert.Contains("outside", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_RequiresIgnoredSqliteSidecarPaths()
    {
        await using var repository = await GitRepository.CreateAsync("artifacts/roslynkit.db\n");

        var result = await new SearchIndexPathPolicy().ResolveAsync(
            repository.GetPath("src/App.csproj"),
            repository.GetPath("artifacts/roslynkit.db"),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.Equal(SearchIndexPathFailureKind.NotIgnored, result.FailureKind);
        Assert.Contains("roslynkit.db-wal", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_RejectsTrackedDatabaseDespiteIgnoreRule()
    {
        await using var repository = await GitRepository.CreateAsync("artifacts/\n");
        await repository.WriteAsync("artifacts/roslynkit.db", "not a search index");
        await repository.RunGitAsync("add", "-f", "--", "artifacts/roslynkit.db");

        var result = await new SearchIndexPathPolicy().ResolveAsync(
            repository.GetPath("src/App.csproj"),
            repository.GetPath("artifacts/roslynkit.db"),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.Equal(SearchIndexPathFailureKind.TrackedPath, result.FailureKind);
        Assert.Contains("already tracks", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_RejectsTrackedSqliteSidecarDespiteIgnoreRule()
    {
        await using var repository = await GitRepository.CreateAsync("artifacts/\n");
        await repository.WriteAsync("artifacts/roslynkit.db-wal", "not a search index sidecar");
        await repository.RunGitAsync("add", "-f", "--", "artifacts/roslynkit.db-wal");

        var result = await new SearchIndexPathPolicy().ResolveAsync(
            repository.GetPath("src/App.csproj"),
            repository.GetPath("artifacts/roslynkit.db"),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.Equal(SearchIndexPathFailureKind.TrackedPath, result.FailureKind);
        Assert.Contains("roslynkit.db-wal", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureAsync_TracksCSharpChangesAndBuildConfigurationChanges()
    {
        await using var repository = await GitRepository.CreateAsync("artifacts/\n");
        var pathResolution = await new SearchIndexPathPolicy().ResolveAsync(
            repository.GetPath("src/App.csproj"),
            repository.GetPath("artifacts/roslynkit.db"),
            TestContext.Current.CancellationToken);
        Assert.True(pathResolution.IsSuccessful, pathResolution.Diagnostic);

        var service = new SearchIndexFingerprintService(pathResolution.Path!);
        var initial = await service.CaptureAsync(TestContext.Current.CancellationToken);
        await repository.WriteAsync("src/App.cs", "internal class App { public int Value => 2; }");
        var sourceChanged = await service.CaptureAsync(TestContext.Current.CancellationToken);
        await repository.WriteAsync("Directory.Build.props", "<Project />");
        var configurationChanged = await service.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.True(initial.IsSuccessful, initial.Diagnostic);
        Assert.True(sourceChanged.IsSuccessful, sourceChanged.Diagnostic);
        Assert.True(configurationChanged.IsSuccessful, configurationChanged.Diagnostic);
        var initialFingerprint = initial.Fingerprint!;
        var sourceFingerprint = sourceChanged.Fingerprint!;
        var configurationFingerprint = configurationChanged.Fingerprint!;
        Assert.NotEqual(initialFingerprint.Value, sourceFingerprint.Value);
        Assert.Equal(initialFingerprint.HeadCommit, sourceFingerprint.HeadCommit);
        Assert.Contains("src/App.cs", sourceFingerprint.ChangedSourcePaths);
        Assert.False(sourceFingerprint.RequiresFullRebuild);
        Assert.NotEqual(sourceFingerprint.Value, configurationFingerprint.Value);
        Assert.True(configurationFingerprint.RequiresFullRebuild);
    }

    [Fact]
    public async Task ListChangedPathsAsync_ClassifiesCommittedDocumentationSourceAndConfigurationChanges()
    {
        await using var repository = await GitRepository.CreateAsync("artifacts/\n");
        var pathResolution = await new SearchIndexPathPolicy().ResolveAsync(
            repository.GetPath("src/App.csproj"),
            repository.GetPath("artifacts/roslynkit.db"),
            TestContext.Current.CancellationToken);
        Assert.True(pathResolution.IsSuccessful, pathResolution.Diagnostic);

        var service = new SearchIndexFingerprintService(pathResolution.Path!);
        var initialCommit = await repository.ReadHeadCommitAsync();
        await repository.WriteAsync("docs/notes.md", "notes");
        await repository.CommitPathsAsync("Add notes", "docs/notes.md");
        var documentationCommit = await repository.ReadHeadCommitAsync();
        var documentation = await service.ListChangedPathsAsync(
            initialCommit,
            documentationCommit,
            TestContext.Current.CancellationToken);

        await repository.WriteAsync("src/App.cs", "internal class App { public int Value => 2; }");
        await repository.CommitPathsAsync("Update source", "src/App.cs");
        var sourceCommit = await repository.ReadHeadCommitAsync();
        var source = await service.ListChangedPathsAsync(
            documentationCommit,
            sourceCommit,
            TestContext.Current.CancellationToken);

        await repository.WriteAsync("Directory.Build.props", "<Project />");
        await repository.CommitPathsAsync("Update build configuration", "Directory.Build.props");
        var configurationCommit = await repository.ReadHeadCommitAsync();
        var configuration = await service.ListChangedPathsAsync(
            sourceCommit,
            configurationCommit,
            TestContext.Current.CancellationToken);

        Assert.True(documentation.IsSuccessful, documentation.Diagnostic);
        Assert.True(source.IsSuccessful, source.Diagnostic);
        Assert.True(configuration.IsSuccessful, configuration.Diagnostic);
        var documentationChanges = documentation.Changes!;
        var sourceChanges = source.Changes!;
        var configurationChanges = configuration.Changes!;
        Assert.Equal(["docs/notes.md"], documentationChanges.Paths);
        Assert.Empty(documentationChanges.ChangedSourcePaths);
        Assert.False(documentationChanges.RequiresFullRebuild);
        Assert.Equal(["src/App.cs"], sourceChanges.ChangedSourcePaths);
        Assert.False(sourceChanges.RequiresFullRebuild);
        Assert.True(configurationChanges.RequiresFullRebuild);
    }

    [Fact]
    public async Task ListChangedPathsAsync_RejectsInvalidCommitIds()
    {
        await using var repository = await GitRepository.CreateAsync("artifacts/\n");
        var pathResolution = await new SearchIndexPathPolicy().ResolveAsync(
            repository.GetPath("src/App.csproj"),
            repository.GetPath("artifacts/roslynkit.db"),
            TestContext.Current.CancellationToken);
        Assert.True(pathResolution.IsSuccessful, pathResolution.Diagnostic);

        var result = await new SearchIndexFingerprintService(pathResolution.Path!).ListChangedPathsAsync(
            "not-a-commit",
            new string('a', 40),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.Equal(SearchIndexChangedPathsFailureKind.InvalidCommit, result.FailureKind);
    }

    [Fact]
    public void TryParseChangedPaths_PreservesUnicodePathsAndRejectsMissingNulTerminator()
    {
        var valid = SearchIndexFingerprintService.TryParseChangedPaths(
            Encoding.UTF8.GetBytes("src/A.cs\0docs/λ.md\0"),
            out var paths,
            out var validDiagnostic);
        var invalid = SearchIndexFingerprintService.TryParseChangedPaths(
            Encoding.UTF8.GetBytes("src/A.cs"),
            out _,
            out var invalidDiagnostic);

        Assert.True(valid, validDiagnostic);
        Assert.Equal(["docs/λ.md", "src/A.cs"], paths);
        Assert.False(invalid);
        Assert.Contains("NUL", invalidDiagnostic, StringComparison.Ordinal);
    }

    private sealed class GitRepository : IAsyncDisposable
    {
        private GitRepository(string rootPath)
        {
            RootPath = rootPath;
        }

        public string RootPath { get; }

        public static async Task<GitRepository> CreateAsync(string gitIgnore)
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                "roslynkit-tests",
                "search-index-policy",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            var repository = new GitRepository(rootPath);
            await repository.RunGitAsync("init");
            await repository.RunGitAsync("config", "user.name", "RoslynKit Tests");
            await repository.RunGitAsync("config", "user.email", "roslynkit@example.test");
            await repository.WriteAsync(".gitignore", gitIgnore);
            await repository.WriteAsync("src/App.cs", "internal class App { public int Value => 1; }");
            await repository.WriteAsync("src/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            await repository.RunGitAsync("add", ".");
            await repository.RunGitAsync("commit", "-m", "Initial commit");
            return repository;
        }

        public string GetPath(string relativePath)
        {
            return Path.Combine(RootPath, relativePath);
        }

        public async Task WriteAsync(string relativePath, string content)
        {
            var path = GetPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        }

        public async Task RunGitAsync(params string[] arguments)
        {
            var result = await ProcessCommandRunner.RunAsync(
                "git",
                RootPath,
                arguments,
                TestContext.Current.CancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', arguments)} failed with exit code {result.ExitCode}: {result.StandardError}");
            }
        }

        public async Task<string> ReadHeadCommitAsync()
        {
            var result = await ProcessCommandRunner.RunAsync(
                "git",
                RootPath,
                ["rev-parse", "--verify", "HEAD^{commit}"],
                TestContext.Current.CancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git rev-parse failed with exit code {result.ExitCode}: {result.StandardError}");
            }

            return result.StandardOutput.Trim();
        }

        public async Task CommitPathsAsync(string message, params string[] paths)
        {
            var addArguments = new List<string> { "add", "--" };
            addArguments.AddRange(paths);
            await RunGitAsync([.. addArguments]);
            await RunGitAsync("commit", "-m", message);
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
