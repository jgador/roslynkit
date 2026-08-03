using System.Diagnostics;
using System.Text;

namespace RoslynKit;

/// <summary>
/// Runs one non-interactive child process with argument-list escaping and fully buffered output.
/// </summary>
internal static class ProcessCommandRunner
{
    public static async Task<ProcessCommandResult> RunAsync(
        string fileName,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunBytesAsync(
            fileName,
            workingDirectory,
            arguments,
            cancellationToken).ConfigureAwait(false);
        return new ProcessCommandResult(
            result.ExitCode,
            Encoding.UTF8.GetString(result.StandardOutput),
            result.StandardError);
    }

    public static async Task<ProcessByteCommandResult> RunBytesAsync(
        string fileName,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }

        await using var standardOutput = new MemoryStream();
        var standardOutputTask = process.StandardOutput.BaseStream.CopyToAsync(
            standardOutput,
            cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await standardOutputTask.ConfigureAwait(false);
            var standardError = await standardErrorTask.ConfigureAwait(false);
            return new ProcessByteCommandResult(process.ExitCode, standardOutput.ToArray(), standardError);
        }
        catch (OperationCanceledException)
        {
            if (TryTerminate(process))
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static bool TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Contains the exit code and buffered standard streams from one child process.
/// </summary>
internal sealed record ProcessCommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Contains an exit code, raw standard output, and diagnostic standard error from one child process.
/// </summary>
internal sealed record ProcessByteCommandResult(int ExitCode, byte[] StandardOutput, string StandardError);
