using System.Diagnostics;
using System.Runtime.InteropServices;

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

    [Theory]
    [InlineData(SigHup)]
    [InlineData(SigInt)]
    [InlineData(SigTerm)]
    public async Task PosixSignal_GracefullyStopsRunningDaemonOnLinux(int signal)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var area = await DaemonProcessTestArea.CreateAsync(cancellationToken);

        using var daemon = new Process
        {
            StartInfo = CreateDaemonStartInfo(area),
        };
        Assert.True(daemon.Start());

        var standardOutput = daemon.StandardOutput
            .ReadToEndAsync(TestContext.Current.CancellationToken);
        var standardError = daemon.StandardError
            .ReadToEndAsync(TestContext.Current.CancellationToken);
        var status = await area.WaitForRunningStatusAsync(cancellationToken);

        Assert.Equal(daemon.Id, status.ProcessId!.Value);

        SendPosixSignal(daemon.Id, signal);
        await daemon.WaitForExitAsync(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        await Task.WhenAll(standardOutput, standardError)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        Assert.Equal(130, daemon.ExitCode);
        Assert.Empty(await standardOutput);
        Assert.Empty(await standardError);
        await area.WaitForNotRunningStatusAsync(cancellationToken);
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

    private static ProcessStartInfo CreateDaemonStartInfo(DaemonProcessTestArea area)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = area.RootPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add(DaemonServerRunner.InternalModeToken);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(area.TargetPath);
        startInfo.Environment["DOTNET_CLI_HOME"] = Path.Combine(area.RootPath, ".dotnet-cli");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        return startInfo;
    }

    private static void SendPosixSignal(int processId, int signal)
    {
        Assert.Equal(0, Kill(processId, signal));
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);

    private const int SigHup = 1;
    private const int SigInt = 2;
    private const int SigTerm = 15;
}

/// <summary>
/// Serializes external daemon tests that launch and stop detached RoslynKit processes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DaemonProcessIntegrationCollection
{
    public const string Name = "Daemon process integration";
}
