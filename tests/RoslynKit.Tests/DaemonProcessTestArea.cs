using System.Diagnostics;
using System.Globalization;

namespace RoslynKit.Tests;

/// <summary>
/// Creates an isolated Git worktree for daemon process integration tests.
/// </summary>
internal sealed class DaemonProcessTestArea : IAsyncDisposable
{
    private const string DaemonRunningState = "running";
    private const string DaemonNotRunningState = "not-running";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProcessTerminationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly string _roslynKitAssemblyPath;
    private readonly IReadOnlyDictionary<string, string> _childEnvironment;
    private int? _daemonProcessId;
    private bool _disposed;

    private DaemonProcessTestArea(
        string rootPath,
        string targetPath,
        string sourcePath,
        string roslynKitAssemblyPath)
    {
        RootPath = rootPath;
        TargetPath = targetPath;
        SourcePath = sourcePath;
        _roslynKitAssemblyPath = roslynKitAssemblyPath;
        _childEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_CLI_HOME"] = Path.Combine(rootPath, ".dotnet-cli"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        };
    }

    public string RootPath { get; }

    public string TargetPath { get; }

    public string SourcePath { get; }

    public static Task<DaemonProcessTestArea> CreateAsync()
    {
        return CreateCoreAsync(TestContext.Current.CancellationToken);
    }

    public static Task<DaemonProcessTestArea> CreateAsync(CancellationToken cancellationToken)
    {
        return CreateCoreAsync(cancellationToken);
    }

    private static async Task<DaemonProcessTestArea> CreateCoreAsync(CancellationToken cancellationToken)
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "roslynkit-tests",
            "daemon-process",
            Guid.NewGuid().ToString("N"));
        var projectDirectory = Path.Combine(rootPath, "src");
        var targetPath = Path.Combine(projectDirectory, "App.csproj");
        var sourcePath = Path.Combine(projectDirectory, "Program.cs");
        var area = new DaemonProcessTestArea(
            rootPath,
            targetPath,
            sourcePath,
            ResolveRoslynKitAssemblyPath());

        try
        {
            Directory.CreateDirectory(projectDirectory);
            File.Copy(TestPaths.RepoFile("global.json"), Path.Combine(rootPath, "global.json"));
            await File.WriteAllTextAsync(
                targetPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """,
                cancellationToken);
            await area.WriteSourceCoreAsync(
                "Console.WriteLine(\"Daemon process test fixture\");\n",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(rootPath, ".gitignore"),
                "bin/\nobj/\n.dotnet-cli/\n",
                cancellationToken);

            await area.RunGitCoreAsync(cancellationToken, "init");
            await area.RunGitCoreAsync(cancellationToken, "config", "user.name", "RoslynKit Tests");
            await area.RunGitCoreAsync(cancellationToken, "config", "user.email", "roslynkit-tests@example.invalid");
            await area.RunGitCoreAsync(cancellationToken, "config", "core.autocrlf", "false");
            await area.RunGitCoreAsync(cancellationToken, "add", ".");
            await area.RunGitCoreAsync(cancellationToken, "commit", "-m", "Initial commit");
            await area.RunGitCoreAsync(cancellationToken, "rev-parse", "--verify", "HEAD");
            return area;
        }
        catch
        {
            await area.DisposeAsync();
            throw;
        }
    }

    public Task<ProcessResult> RunCliAsync(params string[] arguments)
    {
        return RunCliCoreAsync(arguments, TestContext.Current.CancellationToken);
    }

    public Task<ProcessResult> RunCliAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        return RunCliCoreAsync(arguments, cancellationToken);
    }

    private Task<ProcessResult> RunCliCoreAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return RunProcessAsync("dotnet", [_roslynKitAssemblyPath, .. arguments], cancellationToken);
    }

    public Task<ProcessResult> RunGitAsync(params string[] arguments)
    {
        return RunGitCoreAsync(TestContext.Current.CancellationToken, arguments);
    }

    public Task<ProcessResult> RunGitAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        return RunGitCoreAsync(cancellationToken, arguments);
    }

    private async Task<ProcessResult> RunGitCoreAsync(
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var result = await RunProcessAsync("git", arguments, cancellationToken);
        result.EnsureSuccess($"git {string.Join(' ', arguments)}");
        return result;
    }

    public Task WriteSourceAsync(string source)
    {
        return WriteSourceCoreAsync(source, TestContext.Current.CancellationToken);
    }

    public Task WriteSourceAsync(string source, CancellationToken cancellationToken)
    {
        return WriteSourceCoreAsync(source, cancellationToken);
    }

    private Task WriteSourceCoreAsync(string source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return File.WriteAllTextAsync(SourcePath, source, cancellationToken);
    }

    private async Task<DaemonProcessStatus> GetDaemonStatusCoreAsync(CancellationToken cancellationToken)
    {
        var result = await RunCliCoreAsync(["daemon", "status", "--target", TargetPath], cancellationToken);
        var status = ParseDaemonStatus(result);
        CaptureDaemonProcessId(status);
        return status;
    }

    public Task<DaemonProcessStatus> GetDaemonStatusAsync()
    {
        return GetDaemonStatusCoreAsync(TestContext.Current.CancellationToken);
    }

    public Task<DaemonProcessStatus> GetDaemonStatusAsync(CancellationToken cancellationToken)
    {
        return GetDaemonStatusCoreAsync(cancellationToken);
    }

    private async Task<DaemonProcessStatus> WaitForDaemonStateCoreAsync(
        string expectedState,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedState);
        using var timeout = new CancellationTokenSource(StatusTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        while (true)
        {
            var status = await GetDaemonStatusCoreAsync(linkedCancellation.Token);
            if (string.Equals(status.State, expectedState, StringComparison.Ordinal))
            {
                return status;
            }

            await Task.Delay(PollInterval, linkedCancellation.Token);
        }
    }

    public Task<DaemonProcessStatus> WaitForRunningStatusAsync()
    {
        return WaitForDaemonStateCoreAsync(DaemonRunningState, TestContext.Current.CancellationToken);
    }

    public Task<DaemonProcessStatus> WaitForRunningStatusAsync(CancellationToken cancellationToken)
    {
        return WaitForDaemonStateCoreAsync(DaemonRunningState, cancellationToken);
    }

    public async Task WaitForNotRunningStatusAsync()
    {
        await WaitForDaemonStateCoreAsync(DaemonNotRunningState, TestContext.Current.CancellationToken);
    }

    public async Task WaitForNotRunningStatusAsync(CancellationToken cancellationToken)
    {
        await WaitForDaemonStateCoreAsync(DaemonNotRunningState, cancellationToken);
    }

    public Task<DaemonProcessStatus> WaitForDaemonStateAsync(
        string expectedState,
        CancellationToken cancellationToken)
    {
        return WaitForDaemonStateCoreAsync(expectedState, cancellationToken);
    }

    public static DaemonProcessStatus ParseDaemonStatus(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        result.EnsureSuccess("roslynkit daemon status");

        string? state = null;
        int? processId = null;
        int? generation = null;
        string? workspaceState = null;
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("state: ", StringComparison.Ordinal))
            {
                state = line["state: ".Length..];
            }
            else if (line.StartsWith("pid: ", StringComparison.Ordinal))
            {
                if (!int.TryParse(line["pid: ".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedProcessId)
                    || parsedProcessId <= 0)
                {
                    throw new InvalidOperationException($"Invalid daemon process ID in status output: {line}");
                }

                processId = parsedProcessId;
            }
            else if (line.StartsWith("generation: ", StringComparison.Ordinal))
            {
                if (!int.TryParse(line["generation: ".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedGeneration)
                    || parsedGeneration < 0)
                {
                    throw new InvalidOperationException($"Invalid daemon generation in status output: {line}");
                }

                generation = parsedGeneration;
            }
            else if (line.StartsWith("workspace: ", StringComparison.Ordinal))
            {
                workspaceState = line["workspace: ".Length..];
            }
        }

        if (state is null)
        {
            throw new InvalidOperationException($"Daemon status output did not contain a state: {result.StandardOutput}");
        }

        if (string.Equals(state, DaemonRunningState, StringComparison.Ordinal) && processId is null)
        {
            throw new InvalidOperationException($"Running daemon status did not contain a process ID: {result.StandardOutput}");
        }

        return new DaemonProcessStatus(state, processId, generation, workspaceState);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopDaemonAsync();
        }
        finally
        {
            await DeleteRootWithRetriesAsync();
        }
    }

    private async Task StopDaemonAsync()
    {
        using var cleanupTimeout = new CancellationTokenSource(StatusTimeout);
        try
        {
            await GetDaemonStatusCoreAsync(cleanupTimeout.Token);
            var stop = await RunCliCoreAsync(["daemon", "stop", "--target", TargetPath], cleanupTimeout.Token);
            stop.EnsureSuccess("roslynkit daemon stop");
            await WaitForDaemonStateCoreAsync(DaemonNotRunningState, cleanupTimeout.Token);
            return;
        }
        catch (Exception) when (!cleanupTimeout.IsCancellationRequested)
        {
            // The captured PID below is the only process this fixture may forcefully stop.
        }
        catch (OperationCanceledException)
        {
            // The captured PID below is the only process this fixture may forcefully stop.
        }

        await KillCapturedDaemonAsync();
        try
        {
            using var finalStatusTimeout = new CancellationTokenSource(ProcessTerminationTimeout);
            await WaitForDaemonStateCoreAsync(DaemonNotRunningState, finalStatusTimeout.Token);
        }
        catch (Exception)
        {
            // Deletion below remains necessary even when the last status poll cannot complete.
        }
    }

    private async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(fileName, arguments),
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start process '{fileName}'.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessAsync(process);
            throw;
        }

        await Task.WhenAll(standardOutput, standardError).WaitAsync(ProcessTerminationTimeout);
        return new ProcessResult(process.ExitCode, standardOutput.Result, standardError.Result);
    }

    private ProcessStartInfo CreateStartInfo(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = RootPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var pair in _childEnvironment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return startInfo;
    }

    private void CaptureDaemonProcessId(DaemonProcessStatus status)
    {
        if (string.Equals(status.State, DaemonRunningState, StringComparison.Ordinal)
            && status.ProcessId is int processId)
        {
            _daemonProcessId = processId;
        }
    }

    private async Task KillCapturedDaemonAsync()
    {
        if (_daemonProcessId is not int processId)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(ProcessTerminationTimeout);
        }
        catch (ArgumentException)
        {
            // The captured daemon already exited.
        }
        catch (InvalidOperationException)
        {
            // The captured daemon already exited.
        }
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(ProcessTerminationTimeout);
        }
        catch (InvalidOperationException)
        {
            // The process exited while cancellation was being handled.
        }
    }

    private async Task DeleteRootWithRetriesAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!Directory.Exists(RootPath))
            {
                return;
            }

            try
            {
                ResetAttributes(RootPath);
                Directory.Delete(RootPath, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(DeleteRetryDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                await Task.Delay(DeleteRetryDelay);
            }
        }
    }

    private static void ResetAttributes(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(entry, FileAttributes.Normal);
        }

        File.SetAttributes(path, FileAttributes.Normal);
    }

    private static string ResolveRoslynKitAssemblyPath()
    {
        var candidates = new[]
        {
            typeof(Program).Assembly.Location,
            Path.Combine(AppContext.BaseDirectory, "RoslynKit.dll"),
        };
        foreach (var assemblyPath in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var runtimeConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
            if (File.Exists(assemblyPath) && File.Exists(runtimeConfigPath))
            {
                return assemblyPath;
            }
        }

        throw new InvalidOperationException(
            $"Expected a built RoslynKit CLI with a sibling runtime config. Checked: {string.Join(", ", candidates)}");
    }
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public void EnsureSuccess(string command)
    {
        if (ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{command} failed with exit code {ExitCode}.{Environment.NewLine}"
                + $"stdout: {StandardOutput}{Environment.NewLine}"
                + $"stderr: {StandardError}");
        }
    }
}

internal sealed record DaemonProcessStatus(
    string State,
    int? ProcessId,
    int? Generation,
    string? WorkspaceState);
