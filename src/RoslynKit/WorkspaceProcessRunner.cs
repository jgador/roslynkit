using System.Diagnostics;
using System.Text;

namespace RoslynKit;

/// <summary>
/// Provides cancellable, captured process execution for SDK selection and dependency preparation.
/// </summary>
internal interface IWorkspaceProcessRunner
{
    Task<ProcessCommandResult> RunAsync(
        string fileName,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// Drains child output within memory limits and terminates the child process tree on cancellation.
/// </summary>
internal sealed class WorkspaceProcessRunner : IWorkspaceProcessRunner
{
    internal const int MaximumStandardOutputCharacters = 16 * 1024 * 1024;
    internal const int MaximumStandardErrorCharacters = 32 * 1024;

    public Task<ProcessCommandResult> RunAsync(
        string fileName,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return RunAsync(startInfo, timeout, cancellationToken);
    }

    internal async Task<ProcessCommandResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        cancellationToken.ThrowIfCancellationRequested();
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        startInfo.CreateNoWindow = true;
        DotnetProcessEnvironment.ClearInheritedSdkBindings(startInfo);
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new WorkspacePreparationException($"Could not start '{startInfo.FileName}'.");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new WorkspacePreparationException($"Could not start '{startInfo.FileName}': {exception.Message}");
        }

        process.StandardInput.Close();
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaximumStandardOutputCharacters);
        var stderrTask = ReadBoundedAsync(process.StandardError, MaximumStandardErrorCharacters);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            var stderr = await stderrTask.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new ProcessCommandResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or TimeoutException)
            {
                // Process-tree termination can close the captured streams before their readers finish.
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new WorkspacePreparationException(
                $"'{Path.GetFileName(startInfo.FileName)} {startInfo.ArgumentList.FirstOrDefault()}' timed out after {timeout.TotalSeconds:0} seconds.");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            var retained = Math.Min(read, limit - builder.Length);
            builder.Append(buffer, 0, retained);
            truncated |= retained != read;
        }

        if (truncated)
        {
            builder.Append("\n[output truncated]");
        }

        return builder.ToString();
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception or TimeoutException)
        {
            // Cancellation must complete even if the operating system cannot reap a child promptly.
        }
    }
}

/// <summary>
/// Reports a bounded SDK, project-support, or restore failure without writing to protocol output.
/// </summary>
internal sealed class WorkspacePreparationException(string message) : Exception(message);
