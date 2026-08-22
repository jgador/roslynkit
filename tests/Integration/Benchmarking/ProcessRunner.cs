using System.Diagnostics;
using System.Text;

namespace RoslynKit.Benchmarking;

/// <summary>
/// Describes one direct child-process invocation without a shell wrapper.
/// </summary>
internal sealed record ProcessInvocation(
    string FileName,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    string? StandardInput = null,
    IReadOnlyList<string>? RemovedEnvironmentVariables = null);

/// <summary>
/// Contains the exit code and captured streams from one child process.
/// </summary>
internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Runs child processes for benchmark preparation, retrieval, and judging.
/// </summary>
internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>
/// Executes argument-list-only processes with cancellation-safe process-tree cleanup.
/// </summary>
internal sealed class ProcessRunner : IProcessRunner
{
    private static readonly TimeSpan ProcessTerminationTimeout = TimeSpan.FromSeconds(10);

    public async Task<ProcessResult> RunAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(invocation.FileName)
        {
            WorkingDirectory = invocation.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = invocation.StandardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (invocation.StandardInput is not null)
        {
            startInfo.StandardInputEncoding = Encoding.UTF8;
        }
        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var variable in invocation.RemovedEnvironmentVariables ?? [])
        {
            startInfo.Environment.Remove(variable);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new BenchmarkException($"Could not start process '{invocation.FileName}'.");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new BenchmarkException($"Could not start process '{invocation.FileName}'.", exception);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        var standardInputTask = WriteStandardInputAsync(process, invocation.StandardInput, cancellationToken);
        try
        {
            await Task.WhenAll(
                process.WaitForExitAsync(cancellationToken),
                standardInputTask).ConfigureAwait(false);
            var streams = await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, streams[0], streams[1]);
        }
        catch
        {
            await TerminateAsync(process).ConfigureAwait(false);
            await ObserveAsync(standardInputTask, standardOutputTask, standardErrorTask).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WriteStandardInputAsync(
        Process process,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        if (standardInput is null)
        {
            return;
        }

        await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(ProcessTerminationTimeout)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process exited while cancellation or another failure was being handled.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process exited before the operating system accepted the termination request.
        }
        catch (TimeoutException)
        {
            // Do not let a failed operating-system termination block cancellation indefinitely.
        }
    }

    private static async Task ObserveAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).WaitAsync(ProcessTerminationTimeout).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original process failure after observing all stream tasks.
        }
    }
}
