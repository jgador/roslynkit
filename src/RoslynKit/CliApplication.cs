using System.Reflection;

namespace RoslynKit;

/// <summary>
/// Owns the top-level CLI flow from argument parsing through buffered command results and process output.
/// </summary>
public sealed class CliApplication
{
    private static readonly string VersionText = $"roslynkit version {ResolveDisplayVersion()}";

    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly Func<ParsedCommand, CancellationToken, Task<CliProcessResult>> _executeWorkspaceCommand;

    public CliApplication(TextWriter stdout)
        : this(stdout, TextWriter.Null)
    {
    }

    public CliApplication(TextWriter stdout, TextWriter stderr)
        : this(stdout, stderr, WorkspaceCommandRouter.ExecuteAsync)
    {
    }

    public CliApplication(
        TextWriter stdout,
        TextWriter stderr,
        Func<ParsedCommand, CancellationToken, Task<CliProcessResult>> executeWorkspaceCommand)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(executeWorkspaceCommand);

        _stdout = stdout;
        _stderr = stderr;
        _executeWorkspaceCommand = executeWorkspaceCommand;
    }

    /// <summary>
    /// Processes one command and writes its buffered standard output and standard error to their configured streams.
    /// A zero exit code means stdout is command, help, or version output; a non-zero exit code means
    /// stdout is a plain-text error (<c>error:</c> code, <c>message:</c> text, and optional <c>hint:</c> text).
    /// </summary>
    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(args, cancellationToken).ConfigureAwait(false);
        await _stdout.WriteAsync(result.Stdout).ConfigureAwait(false);
        await _stderr.WriteAsync(result.Stderr).ConfigureAwait(false);
        return result.ExitCode;
    }

    /// <summary>
    /// Processes one command into exact buffered process streams without writing to the configured writers.
    /// </summary>
    public async Task<CliProcessResult> ExecuteAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        string errorCode;
        string errorMessage;
        string? errorHint = null;
        int exitCode;

        try
        {
            var command = CliParser.Parse(args);

            if (command.IsHelp)
            {
                return CliProcessResult.Success(MarkdownProjection.RenderHelp(command.HelpSubject));
            }

            if (command.Name == "version")
            {
                return CliProcessResult.Success(VersionText);
            }

            if (command.Name == "init")
            {
                var result = InitCommandExecutor.Execute(command);
                return CliProcessResult.Success(MarkdownProjection.Render(result));
            }

            if (command.Name is "daemon status" or "daemon stop")
            {
                return DaemonCommandExecutor.Execute(command);
            }

            return await _executeWorkspaceCommand(command, cancellationToken).ConfigureAwait(false);
        }
        catch (CliUsageException ex)
        {
            exitCode = 2;
            errorCode = "usage";
            errorMessage = ex.Message;
            errorHint = ex.Hint;
        }
        catch (OperationCanceledException)
        {
            exitCode = 130;
            errorCode = "canceled";
            errorMessage = "Operation was canceled.";
        }
        catch (Exception ex)
        {
            exitCode = 1;
            errorCode = ex.GetType().Name;
            errorMessage = ex.Message;
        }

        return CliProcessResult.Failure(exitCode, errorCode, errorMessage, errorHint);
    }

    private static string ResolveDisplayVersion()
    {
        var assembly = typeof(CliApplication).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
