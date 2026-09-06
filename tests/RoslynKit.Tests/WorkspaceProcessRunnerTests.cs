using System.Diagnostics;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies bounded subprocess capture and cancellation of dependency preparation descendants.
/// </summary>
public sealed class WorkspaceProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_CancelsChildProcessTree()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "This subprocess-tree fixture uses the Linux shell.");
        await using var area = await PreparationTestArea.CreateAsync();
        var childPidPath = Path.Combine(area.RootPath, "child.pid");
        using var cancellation = new CancellationTokenSource();
        var running = new WorkspaceProcessRunner().RunAsync(
            "/bin/bash", area.RootPath,
            ["-c", "sleep 30 & child=$!; printf '%s' \"$child\" > \"$1\"; wait", "preparation-test", childPidPath],
            TimeSpan.FromSeconds(30), cancellation.Token);
        try
        {
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(childPidPath) || new FileInfo(childPidPath).Length == 0)
            {
                await Task.Delay(20, ready.Token);
            }

            var childPid = int.Parse(await File.ReadAllTextAsync(childPidPath, TestContext.Current.CancellationToken));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            var statPath = $"/proc/{childPid}/stat";
            Assert.True(!File.Exists(statPath)
                || (await File.ReadAllTextAsync(statPath, TestContext.Current.CancellationToken)).Contains(") Z", StringComparison.Ordinal),
                "The descendant must have exited or be awaiting operating-system reaping.");
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await running;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task RunAsync_BoundsCapturedOutputAndError()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "This stream-size fixture uses Linux shell utilities.");
        await using var area = await PreparationTestArea.CreateAsync();
        var result = await new WorkspaceProcessRunner().RunAsync(
            "/bin/bash", area.RootPath,
            ["-c", "head -c 17000000 /dev/zero | tr '\\0' x; head -c 40000 /dev/zero | tr '\\0' e >&2"],
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.EndsWith("[output truncated]", result.StandardOutput, StringComparison.Ordinal);
        Assert.EndsWith("[output truncated]", result.StandardError, StringComparison.Ordinal);
        Assert.True(result.StandardOutput.Length < WorkspaceProcessRunner.MaximumStandardOutputCharacters + 32);
        Assert.True(result.StandardError.Length < WorkspaceProcessRunner.MaximumStandardErrorCharacters + 32);
    }

    [Fact]
    public async Task RunAsync_ReportsBoundedTimeout()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "This timeout fixture uses the Linux sleep command.");
        await using var area = await PreparationTestArea.CreateAsync();
        var elapsed = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<WorkspacePreparationException>(
            () => new WorkspaceProcessRunner().RunAsync(
                "/bin/sleep", area.RootPath, ["30"], TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));

        Assert.Contains("timed out", exception.Message, StringComparison.Ordinal);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
    }
}
