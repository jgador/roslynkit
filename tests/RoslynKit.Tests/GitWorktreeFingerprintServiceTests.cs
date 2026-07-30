using System.Text;

namespace RoslynKit.Tests;

public sealed class GitWorktreeFingerprintServiceTests
{
    private const string HeadObjectId = "0123456789abcdef0123456789abcdef01234567";
    private const string BlobObjectId = "89abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task CaptureAsync_ChangesBlobId_ForRepeatedEditsWithSameStatus()
    {
        await using var repository = await GitTestRepository.CreateAsync();
        var service = new GitWorktreeFingerprintService(repository.RootPath);

        await repository.WriteAsync("src/App.cs", "internal class App { public int Value => 1; }");
        var first = await service.CaptureAsync(TestContext.Current.CancellationToken);
        await repository.WriteAsync("src/App.cs", "internal class App { public int Value => 2; }");
        var second = await service.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccessful, first.Diagnostic);
        Assert.True(second.IsSuccessful, second.Diagnostic);
        Assert.Equal(first.Fingerprint!.HeadCommit, second.Fingerprint!.HeadCommit);
        Assert.Equal(first.Fingerprint.StatusEntries, second.Fingerprint.StatusEntries);
        Assert.NotEqual(
            Assert.Single(first.Fingerprint.Files).ObjectId,
            Assert.Single(second.Fingerprint.Files).ObjectId);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public async Task CaptureAsync_ChangesHead_AfterCleanCommit()
    {
        await using var repository = await GitTestRepository.CreateAsync();
        var service = new GitWorktreeFingerprintService(repository.RootPath);
        var first = await service.CaptureAsync(TestContext.Current.CancellationToken);

        await repository.WriteAsync("src/App.cs", "internal class App { public int Value => 3; }");
        await repository.RunGitAsync("add", "--", "src/App.cs");
        await repository.RunGitAsync("commit", "-m", "Update App");
        var second = await service.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccessful, first.Diagnostic);
        Assert.True(second.IsSuccessful, second.Diagnostic);
        Assert.Empty(first.Fingerprint!.StatusEntries);
        Assert.Empty(second.Fingerprint!.StatusEntries);
        Assert.NotEqual(first.Fingerprint.HeadCommit, second.Fingerprint.HeadCommit);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public async Task CaptureAsync_TracksStagedUntrackedAndDeletedPaths()
    {
        await using var repository = await GitTestRepository.CreateAsync();
        await repository.WriteAsync("src/App.cs", "internal class App { public int Value => 4; }");
        await repository.RunGitAsync("add", "--", "src/App.cs");
        File.Delete(repository.GetPath("src/Delete.cs"));
        await repository.WriteAsync("src/New File.cs", "internal class NewFile { }");

        var result = await new GitWorktreeFingerprintService(repository.RootPath)
            .CaptureAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccessful, result.Diagnostic);
        var fingerprint = result.Fingerprint!;
        Assert.Contains(fingerprint.StatusEntries, entry => entry is { StatusCode: "M ", Path: "src/App.cs" });
        Assert.Contains(fingerprint.StatusEntries, entry => entry is { StatusCode: " D", Path: "src/Delete.cs" });
        Assert.Contains(fingerprint.StatusEntries, entry => entry is { StatusCode: "??", Path: "src/New File.cs" });
        Assert.True(Assert.Single(fingerprint.Files, file => file.Path == "src/App.cs").Exists);
        Assert.False(Assert.Single(fingerprint.Files, file => file.Path == "src/Delete.cs").Exists);
        Assert.True(Assert.Single(fingerprint.Files, file => file.Path == "src/New File.cs").Exists);
    }

    [Fact]
    public async Task CaptureAsync_SortsSpaceAndUnicodePathsDeterministically()
    {
        await using var repository = await GitTestRepository.CreateAsync();
        string[] paths = ["src/z space.cs", "src/λ.cs", "src/a.cs"];
        foreach (var path in paths)
        {
            await repository.WriteAsync(path, $"// {path}");
        }

        var result = await new GitWorktreeFingerprintService(repository.RootPath)
            .CaptureAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccessful, result.Diagnostic);
        var expected = paths.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, result.Fingerprint!.StatusEntries.Select(static entry => entry.Path));
        Assert.Equal(expected, result.Fingerprint.Files.Select(static file => file.Path));
        Assert.All(result.Fingerprint.Files, static file => Assert.True(file.Exists));
    }

    [Fact]
    public async Task CaptureAsync_DoesNotObserveGitIgnoredChanges()
    {
        await using var repository = await GitTestRepository.CreateAsync();
        var service = new GitWorktreeFingerprintService(repository.RootPath);
        var first = await service.CaptureAsync(TestContext.Current.CancellationToken);
        await repository.WriteAsync("ignored/Generated.cs", "internal class Generated1 { }");
        var second = await service.CaptureAsync(TestContext.Current.CancellationToken);
        await repository.WriteAsync("ignored/Generated.cs", "internal class Generated2 { }");
        var third = await service.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccessful, first.Diagnostic);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(second.Fingerprint, third.Fingerprint);
        Assert.Empty(third.Fingerprint!.Files);
    }

    [Fact]
    public void TryParse_PreservesRenamePaths_FromNulDelimitedBytes()
    {
        var output = Encoding.UTF8.GetBytes("R  src/new λ.cs\0src/old name.cs\0");

        var success = GitPorcelainParser.TryParse(output, out var entries, out var diagnostic);

        Assert.True(success, diagnostic);
        var entry = Assert.Single(entries);
        Assert.Equal("R ", entry.StatusCode);
        Assert.Equal("src/new λ.cs", entry.Path);
        Assert.Equal("src/old name.cs", entry.OriginalPath);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsInvalidOutput_ForMalformedPorcelain()
    {
        using var directory = TemporaryDirectory.Create();
        var service = CreateService(
            directory.Path,
            (_, _, arguments, _) => Task.FromResult(
                IsCommand(arguments, "rev-parse")
                    ? Success($"{HeadObjectId}\n")
                    : Success("?? unterminated.cs")));

        var result = await service.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.Equal(GitWorktreeFingerprintFailureKind.InvalidOutput, result.FailureKind);
        Assert.Contains("NUL", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsInvalidOutput_ForIncompleteHashResults()
    {
        using var directory = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "changed.cs"),
            "content",
            TestContext.Current.CancellationToken);
        var service = CreateService(
            directory.Path,
            (_, _, arguments, _) => Task.FromResult(
                IsCommand(arguments, "rev-parse")
                    ? Success($"{HeadObjectId}\n")
                    : IsCommand(arguments, "status")
                        ? Success("?? changed.cs\0")
                        : Success(string.Empty)));

        var result = await service.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.Equal(GitWorktreeFingerprintFailureKind.InvalidOutput, result.FailureKind);
        Assert.Contains("0 object IDs for 1 worktree paths", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsGitFailure_ForNonzeroGitExit()
    {
        using var directory = TemporaryDirectory.Create();
        var service = CreateService(
            directory.Path,
            (_, _, _, _) => Task.FromResult(new ProcessByteCommandResult(128, [], "repository unavailable")));

        var result = await service.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.Equal(GitWorktreeFingerprintFailureKind.GitFailure, result.FailureKind);
        Assert.Contains("repository unavailable", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsUnstableCapture_WhenStatusChanges()
    {
        using var directory = TemporaryDirectory.Create();
        var statusCalls = 0;
        var service = CreateService(
            directory.Path,
            (_, _, arguments, _) => Task.FromResult(
                IsCommand(arguments, "rev-parse")
                    ? Success($"{HeadObjectId}\n")
                    : Success(++statusCalls == 1 ? " D first.cs\0" : " D second.cs\0")));

        var result = await service.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.Equal(GitWorktreeFingerprintFailureKind.UnstableCapture, result.FailureKind);
        Assert.Contains("changed during", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsUnstableCapture_WhenHeadChanges()
    {
        using var directory = TemporaryDirectory.Create();
        var headCalls = 0;
        var service = CreateService(
            directory.Path,
            (_, _, arguments, _) => Task.FromResult(
                IsCommand(arguments, "rev-parse")
                    ? Success($"{(headCalls++ == 0 ? HeadObjectId : BlobObjectId)}\n")
                    : Success(string.Empty)));

        var result = await service.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.Equal(GitWorktreeFingerprintFailureKind.UnstableCapture, result.FailureKind);
        Assert.Contains("changed during", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsTimedOut_WhenTotalDeadlineExpires()
    {
        using var directory = TemporaryDirectory.Create();
        var service = CreateService(
            directory.Path,
            async (_, _, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable");
            },
            TimeSpan.FromMilliseconds(25));

        var result = await service.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccessful);
        Assert.Equal(GitWorktreeFingerprintFailureKind.TimedOut, result.FailureKind);
        Assert.Contains("deadline", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureAsync_CoalescesConcurrentCaptures()
    {
        using var directory = TemporaryDirectory.Create();
        var firstCommandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCommand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandCount = 0;
        var service = CreateService(
            directory.Path,
            async (_, _, arguments, cancellationToken) =>
            {
                if (Interlocked.Increment(ref commandCount) == 1)
                {
                    firstCommandStarted.SetResult();
                    await releaseFirstCommand.Task.WaitAsync(cancellationToken);
                }

                return IsCommand(arguments, "rev-parse")
                    ? Success($"{HeadObjectId}\n")
                    : Success(string.Empty);
            });

        var first = service.CaptureAsync(TestContext.Current.CancellationToken);
        await firstCommandStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = service.CaptureAsync(TestContext.Current.CancellationToken);
        releaseFirstCommand.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, static result => Assert.True(result.IsSuccessful, result.Diagnostic));
        Assert.Same(results[0].Fingerprint, results[1].Fingerprint);
        Assert.Equal(4, commandCount);
    }

    [Fact]
    public async Task CaptureAsync_CallerCancellation_DoesNotCancelSharedCapture()
    {
        using var directory = TemporaryDirectory.Create();
        var firstCommandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCommand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateService(
            directory.Path,
            async (_, _, arguments, cancellationToken) =>
            {
                firstCommandStarted.TrySetResult();
                await releaseFirstCommand.Task.WaitAsync(cancellationToken);
                return IsCommand(arguments, "rev-parse")
                    ? Success($"{HeadObjectId}\n")
                    : Success(string.Empty);
            });

        var survivingCaller = service.CaptureAsync(TestContext.Current.CancellationToken);
        await firstCommandStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var canceled = new CancellationTokenSource();
        var canceledCaller = service.CaptureAsync(canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCaller);
        releaseFirstCommand.SetResult();

        var result = await survivingCaller;
        Assert.True(result.IsSuccessful, result.Diagnostic);
    }

    [Fact]
    public async Task CaptureAsync_BatchesLargeChangedPathSets()
    {
        using var directory = TemporaryDirectory.Create();
        var status = new StringBuilder();
        for (var index = 0; index < 129; index++)
        {
            var path = $"file-{index:D3}.cs";
            await File.WriteAllTextAsync(
                Path.Combine(directory.Path, path),
                path,
                TestContext.Current.CancellationToken);
            status.Append("?? ").Append(path).Append('\0');
        }

        var hashCalls = 0;
        var service = CreateService(
            directory.Path,
            (_, _, arguments, _) =>
            {
                if (IsCommand(arguments, "rev-parse"))
                {
                    return Task.FromResult(Success($"{HeadObjectId}\n"));
                }

                if (IsCommand(arguments, "status"))
                {
                    return Task.FromResult(Success(status.ToString()));
                }

                Interlocked.Increment(ref hashCalls);
                var separator = arguments.ToList().IndexOf("--");
                var count = arguments.Count - separator - 1;
                return Task.FromResult(Success(string.Concat(Enumerable.Repeat($"{BlobObjectId}\n", count))));
            });

        var result = await service.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccessful, result.Diagnostic);
        Assert.Equal(129, result.Fingerprint!.Files.Count);
        Assert.Equal(2, hashCalls);
    }

    private static GitWorktreeFingerprintService CreateService(
        string rootPath,
        Func<string, string, IReadOnlyList<string>, CancellationToken, Task<ProcessByteCommandResult>> runner,
        TimeSpan? deadline = null)
    {
        return new GitWorktreeFingerprintService(
            rootPath,
            runner,
            deadline ?? TimeSpan.FromSeconds(2));
    }

    private static bool IsCommand(IReadOnlyList<string> arguments, string command)
    {
        return arguments.Contains(command, StringComparer.Ordinal);
    }

    private static ProcessByteCommandResult Success(string standardOutput)
    {
        return new ProcessByteCommandResult(0, Encoding.UTF8.GetBytes(standardOutput), string.Empty);
    }

    private sealed class GitTestRepository : IAsyncDisposable
    {
        private GitTestRepository(string rootPath)
        {
            RootPath = rootPath;
        }

        public string RootPath { get; }

        public static async Task<GitTestRepository> CreateAsync()
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                "roslynkit-tests",
                "git-worktree-fingerprint",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            var repository = new GitTestRepository(rootPath);
            await repository.RunGitAsync("init");
            await repository.RunGitAsync("config", "user.name", "RoslynKit Tests");
            await repository.RunGitAsync("config", "user.email", "roslynkit@example.test");
            await repository.WriteAsync(".gitignore", "ignored/\n");
            await repository.WriteAsync("src/App.cs", "internal class App { }");
            await repository.WriteAsync("src/Delete.cs", "internal class Delete { }");
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

        public ValueTask DisposeAsync()
        {
            DeleteDirectory(RootPath);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "roslynkit-tests",
                "git-worktree-fingerprint-fake",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            DeleteDirectory(Path);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
