namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Verifies that cancellation terminates a long-lived native child process promptly.
/// </summary>
public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_CancellationTerminatesProcessPromptly()
    {
        var invocation = OperatingSystem.IsWindows()
            ? new ProcessInvocation("ping.exe", Environment.CurrentDirectory, ["127.0.0.1", "-n", "30"])
            : new ProcessInvocation("/bin/sleep", Environment.CurrentDirectory, ["30"]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        var runner = new ProcessRunner();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(invocation, cancellation.Token).WaitAsync(
                TimeSpan.FromSeconds(15),
                TestContext.Current.CancellationToken));
    }
}
