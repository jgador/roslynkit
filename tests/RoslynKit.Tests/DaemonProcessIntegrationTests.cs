using System.Diagnostics;

namespace RoslynKit.Tests;

/// <summary>
/// Exercises daemon startup, workspace reuse, reload, and lifecycle controls across CLI processes.
/// </summary>
[Collection(DaemonProcessIntegrationCollection.Name)]
public sealed class DaemonProcessIntegrationTests
{
    [Fact]
    public async Task LifecycleControls_WhenDaemonIsAbsent_DoNotStartIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var area = await DaemonProcessTestArea.CreateAsync(cancellationToken);

        var status = await area.RunCliAsync(
            ["daemon", "status", "--target", area.TargetPath],
            cancellationToken);
        var stop = await area.RunCliAsync(
            ["daemon", "stop", "--target", area.TargetPath],
            cancellationToken);

        Assert.Equal(0, status.ExitCode);
        Assert.Contains("state: not-running", status.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(status.StandardError);
        Assert.Equal(0, stop.ExitCode);
        Assert.Contains("state: not-running", stop.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(stop.StandardError);
    }

    [Fact]
    public async Task ConcurrentFirstCommands_StartOnePersistentDaemon_AndLifecycleControlsStopIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var area = await DaemonProcessTestArea.CreateAsync(cancellationToken);

        var commands = await Task.WhenAll(
            area.RunCliAsync(["workspace", "--target", area.TargetPath], cancellationToken),
            area.RunCliAsync(["workspace", "--target", area.TargetPath], cancellationToken));

        foreach (var command in commands)
        {
            Assert.Equal(0, command.ExitCode);
            Assert.Empty(command.StandardError);
        }

        var initialStatus = await area.WaitForRunningStatusAsync(cancellationToken);
        Assert.Equal("ready", initialStatus.WorkspaceState);
        Assert.NotNull(initialStatus.ProcessId);
        Assert.True(initialStatus.Generation > 0);

        var laterCommand = await area.RunCliAsync(
            ["workspace", "--target", area.TargetPath],
            cancellationToken);
        var laterStatus = await area.WaitForRunningStatusAsync(cancellationToken);

        Assert.Equal(0, laterCommand.ExitCode);
        Assert.Empty(laterCommand.StandardError);
        Assert.Equal(initialStatus.ProcessId, laterStatus.ProcessId);
        Assert.Equal(initialStatus.Generation, laterStatus.Generation);

        var stop = await area.RunCliAsync(
            ["daemon", "stop", "--target", area.TargetPath],
            cancellationToken);
        Assert.Equal(0, stop.ExitCode);
        Assert.Contains("state: stopping", stop.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(stop.StandardError);

        await area.WaitForNotRunningStatusAsync(cancellationToken);
        await WaitForProcessExitAsync(initialStatus.ProcessId!.Value);

        var repeatedStop = await area.RunCliAsync(
            ["daemon", "stop", "--target", area.TargetPath],
            cancellationToken);
        Assert.Equal(0, repeatedStop.ExitCode);
        Assert.Contains("state: not-running", repeatedStop.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(repeatedStop.StandardError);
    }

    [Fact]
    public async Task GitEdit_ReloadsWorkspaceWhileCleanCommandsReuseGeneration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var area = await DaemonProcessTestArea.CreateAsync(cancellationToken);

        var firstCommand = await area.RunCliAsync(
            ["workspace", "--target", area.TargetPath],
            cancellationToken);
        var firstStatus = await area.WaitForRunningStatusAsync(cancellationToken);
        var secondCommand = await area.RunCliAsync(
            ["workspace", "--target", area.TargetPath],
            cancellationToken);
        var secondStatus = await area.WaitForRunningStatusAsync(cancellationToken);

        Assert.Equal(0, firstCommand.ExitCode);
        Assert.Equal(0, secondCommand.ExitCode);
        Assert.Empty(firstCommand.StandardError);
        Assert.Empty(secondCommand.StandardError);
        Assert.Equal(firstStatus.ProcessId, secondStatus.ProcessId);
        Assert.Equal(firstStatus.Generation, secondStatus.Generation);

        const string changedSource = "Console.WriteLine(\"Reloaded daemon snapshot\");\n";
        await area.WriteSourceAsync(changedSource, cancellationToken);

        var changedDocument = await area.RunCliAsync(
            ["document-text", "--target", area.TargetPath, "--file", area.SourcePath],
            cancellationToken);
        var changedStatus = await area.WaitForRunningStatusAsync(cancellationToken);

        Assert.Equal(0, changedDocument.ExitCode);
        Assert.Contains("Reloaded daemon snapshot", changedDocument.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(changedDocument.StandardError);
        Assert.Equal(firstStatus.ProcessId, changedStatus.ProcessId);
        Assert.Equal(firstStatus.Generation + 1, changedStatus.Generation);
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        catch (ArgumentException)
        {
            // The daemon exited before the process handle was opened.
        }
    }
}

/// <summary>
/// Serializes external daemon tests that launch and stop detached RoslynKit processes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DaemonProcessIntegrationCollection
{
    public const string Name = "Daemon process integration";
}
