using System.Security.Cryptography;

namespace RoslynKit.Tests;

public sealed class GitWorkspaceIdentityResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsCompleteIdentity_ForCommittedRepository()
    {
        await using var area = GitTestArea.Create();
        var repository = await area.CreateRepositoryAsync("supported", includeGlobalJson: true);
        var targetWithSegments = Path.Combine(
            Path.GetDirectoryName(repository.TargetPath)!,
            "..",
            "src",
            Path.GetFileName(repository.TargetPath));

        var result = await new GitWorkspaceIdentityResolver().ResolveAsync(
            targetWithSegments,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSupported, result.Diagnostic);
        var identity = Assert.IsType<GitWorkspaceIdentity>(result.Identity);
        Assert.Equal("src/App.csproj", NormalizeRelativePath(Path.GetRelativePath(identity.WorktreeRoot, identity.TargetPath)));
        Assert.Equal(RoslynKitBuildInfo.DaemonProtocolVersion, identity.ProtocolVersion);
        Assert.Equal(RoslynKitBuildInfo.Identity, identity.RoslynKit);
        Assert.False(string.IsNullOrWhiteSpace(identity.ProcessArchitecture));
        Assert.False(string.IsNullOrWhiteSpace(identity.DotNetSdk.Version));
        Assert.True(Directory.Exists(identity.MSBuild.MSBuildPath));
        Assert.False(string.IsNullOrWhiteSpace(identity.MSBuild.InstanceVersion));
        Assert.Contains("DOTNET_ROOT", identity.BuildEnvironment.Keys);
        Assert.Contains("MSBuildSDKsPath", identity.BuildEnvironment.Keys);
        Assert.Contains("NUGET_PACKAGES", identity.BuildEnvironment.Keys);

        var globalJson = Assert.IsType<GlobalJsonIdentity>(identity.GlobalJson);
        Assert.Equal(Path.Combine(identity.WorktreeRoot, "global.json"), globalJson.Path);
        var expectedDigest = Convert.ToHexStringLower(
            SHA256.HashData(await File.ReadAllBytesAsync(globalJson.Path, TestContext.Current.CancellationToken)));
        Assert.Equal(expectedDigest, globalJson.Sha256);
    }

    [Fact]
    public async Task ResolveAsync_ProducesStableEndpoint_AfterHeadChanges()
    {
        await using var area = GitTestArea.Create();
        var repository = await area.CreateRepositoryAsync("head-change");
        var workspaceResolver = new GitWorkspaceIdentityResolver();
        var daemonResolver = new DaemonIdentityResolver(
            () => new DaemonUserIdentity(DaemonIdentityResolver.UnixEffectiveUserIdKind, "test-user"),
            () => Path.Combine(area.RootPath, "runtime"));
        var initialResolution = await workspaceResolver.ResolveAsync(
            repository.TargetPath,
            TestContext.Current.CancellationToken);
        Assert.True(initialResolution.IsSupported, initialResolution.Diagnostic);
        var initialIdentity = Assert.IsType<GitWorkspaceIdentity>(initialResolution.Identity);

        await File.WriteAllTextAsync(
            Path.Combine(repository.RootPath, "notes.txt"),
            "new commit",
            TestContext.Current.CancellationToken);
        await area.RunGitAsync(repository.RootPath, "add", "notes.txt");
        await area.RunGitAsync(repository.RootPath, "commit", "-m", "Change HEAD");
        var changedResolution = await workspaceResolver.ResolveAsync(
            repository.TargetPath,
            TestContext.Current.CancellationToken);
        Assert.True(changedResolution.IsSupported, changedResolution.Diagnostic);
        var changedIdentity = Assert.IsType<GitWorkspaceIdentity>(changedResolution.Identity);

        Assert.Equal(
            DaemonEndpointName.Create(daemonResolver.Resolve(initialIdentity)),
            DaemonEndpointName.Create(daemonResolver.Resolve(changedIdentity)));
    }

    [Fact]
    public async Task ResolveAsync_ReturnsSupportedIdentity_ForLinkedWorktree()
    {
        await using var area = GitTestArea.Create();
        var repository = await area.CreateRepositoryAsync("main", includeGlobalJson: true);
        var linkedRoot = Path.Combine(area.RootPath, "linked");
        await area.RunGitAsync(repository.RootPath, "worktree", "add", "--detach", linkedRoot, "HEAD");
        var linkedTarget = Path.Combine(linkedRoot, "src", "App.csproj");

        var result = await new GitWorkspaceIdentityResolver().ResolveAsync(
            linkedTarget,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSupported, result.Diagnostic);
        var identity = Assert.IsType<GitWorkspaceIdentity>(result.Identity);
        Assert.Equal(Path.GetFileName(linkedRoot), Path.GetFileName(identity.WorktreeRoot));
        Assert.Equal("src/App.csproj", NormalizeRelativePath(Path.GetRelativePath(identity.WorktreeRoot, identity.TargetPath)));
    }

    [Fact]
    public async Task ResolveAsync_RejectsRepositoryWithoutCommittedHead()
    {
        await using var area = GitTestArea.Create();
        var repository = await area.CreateRepositoryAsync("unborn", commit: false);

        var result = await new GitWorkspaceIdentityResolver().ResolveAsync(
            repository.TargetPath,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSupported);
        Assert.Equal(GitWorkspaceIdentityFailureKind.UnsupportedWorkspace, result.FailureKind);
        Assert.Contains("committed HEAD", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_RejectsTargetInNestedRepository()
    {
        await using var area = GitTestArea.Create();
        var outer = await area.CreateRepositoryAsync("outer");
        var nested = await area.CreateRepositoryAsync(Path.Combine("outer", "nested"));

        var result = await new GitWorkspaceIdentityResolver().ResolveAsync(
            nested.TargetPath,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSupported);
        Assert.Equal(GitWorkspaceIdentityFailureKind.UnsupportedWorkspace, result.FailureKind);
        Assert.Contains("Nested Git repositories", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(outer.RootPath), result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_RejectsRepositoryContainingSubmodule()
    {
        await using var area = GitTestArea.Create();
        var outer = await area.CreateRepositoryAsync("outer");
        var dependency = await area.CreateRepositoryAsync("dependency");
        await area.RunGitAsync(
            outer.RootPath,
            "-c",
            "protocol.file.allow=always",
            "submodule",
            "add",
            dependency.RootPath,
            Path.Combine("modules", "dependency"));
        await area.RunGitAsync(outer.RootPath, "commit", "-am", "Add submodule");

        var result = await new GitWorkspaceIdentityResolver().ResolveAsync(
            outer.TargetPath,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSupported);
        Assert.Equal(GitWorkspaceIdentityFailureKind.UnsupportedWorkspace, result.FailureKind);
        Assert.Contains("containing Git submodules", result.Diagnostic, StringComparison.Ordinal);

        var submoduleTarget = Path.Combine(outer.RootPath, "modules", "dependency", "src", "App.csproj");
        var submoduleResult = await new GitWorkspaceIdentityResolver().ResolveAsync(
            submoduleTarget,
            TestContext.Current.CancellationToken);

        Assert.False(submoduleResult.IsSupported);
        Assert.Equal(GitWorkspaceIdentityFailureKind.UnsupportedWorkspace, submoduleResult.FailureKind);
        Assert.Contains("submodule worktrees", submoduleResult.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_RejectsTargetOutsideGitWorktree()
    {
        await using var area = GitTestArea.Create();
        var targetPath = Path.Combine(area.RootPath, "src", "App.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(
            targetPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\" />",
            TestContext.Current.CancellationToken);

        var result = await new GitWorkspaceIdentityResolver().ResolveAsync(
            targetPath,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSupported);
        Assert.Equal(GitWorkspaceIdentityFailureKind.UnsupportedWorkspace, result.FailureKind);
        Assert.Contains("not inside a Git worktree", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_ClassifiesProcessStartFailure_AsInfrastructure()
    {
        await using var area = GitTestArea.Create();
        var repository = await area.CreateRepositoryAsync("process-failure");
        var resolver = new GitWorkspaceIdentityResolver(
            (_, _, _, _) => throw new InvalidOperationException("git unavailable"),
            _ => null,
            _ => throw new InvalidOperationException("MSBuild should not be queried"));

        var result = await resolver.ResolveAsync(
            repository.TargetPath,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSupported);
        Assert.Equal(GitWorkspaceIdentityFailureKind.Infrastructure, result.FailureKind);
        Assert.Contains("git unavailable", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_PropagatesCancellation()
    {
        await using var area = GitTestArea.Create();
        var repository = await area.CreateRepositoryAsync("canceled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var resolver = new GitWorkspaceIdentityResolver(
            (_, _, _, cancellationToken) => Task.FromCanceled<ProcessCommandResult>(cancellationToken),
            _ => null,
            _ => throw new InvalidOperationException("MSBuild should not be queried"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveAsync(repository.TargetPath, cancellation.Token));
    }

    private sealed class GitTestArea : IAsyncDisposable
    {
        private GitTestArea(string rootPath)
        {
            RootPath = rootPath;
        }

        public string RootPath { get; }

        public static GitTestArea Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "roslynkit-tests",
                "git-workspace-identity",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new GitTestArea(root);
        }

        public async Task<GitTestRepository> CreateRepositoryAsync(
            string relativePath,
            bool commit = true,
            bool includeGlobalJson = false)
        {
            var repositoryRoot = Path.Combine(RootPath, relativePath);
            var targetPath = Path.Combine(repositoryRoot, "src", "App.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await File.WriteAllTextAsync(
                targetPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
                TestContext.Current.CancellationToken);
            if (includeGlobalJson)
            {
                File.Copy(TestPaths.RepoFile("global.json"), Path.Combine(repositoryRoot, "global.json"));
            }

            await RunGitAsync(repositoryRoot, "init");
            await RunGitAsync(repositoryRoot, "config", "user.name", "RoslynKit Tests");
            await RunGitAsync(repositoryRoot, "config", "user.email", "roslynkit-tests@example.invalid");
            await RunGitAsync(repositoryRoot, "config", "core.autocrlf", "false");
            if (commit)
            {
                await RunGitAsync(repositoryRoot, "add", ".");
                await RunGitAsync(repositoryRoot, "commit", "-m", "Initial commit");
            }

            return new GitTestRepository(repositoryRoot, targetPath);
        }

        public async Task RunGitAsync(string workingDirectory, params string[] arguments)
        {
            var result = await ProcessCommandRunner.RunAsync(
                "git",
                workingDirectory,
                arguments,
                TestContext.Current.CancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', arguments)} failed with exit code {result.ExitCode}: {result.StandardError}");
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootPath))
            {
                foreach (var file in Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(RootPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/');
    }

    private sealed record GitTestRepository(string RootPath, string TargetPath);
}
