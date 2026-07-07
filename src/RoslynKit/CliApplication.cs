using System.Reflection;

namespace RoslynKit;

/// <summary>
/// Owns the top-level CLI flow from argument parsing through command dispatch and stdout output.
/// </summary>
public sealed class CliApplication
{
    private static readonly string VersionText = $"roslynkit version {ResolveDisplayVersion()}";

    private readonly TextWriter _stdout;

    public CliApplication(TextWriter stdout)
    {
        _stdout = stdout;
    }

    /// <summary>
    /// Parses arguments, dispatches help or command execution, and writes markdown-flavored text output.
    /// A zero exit code means stdout is command, help, or version output; a non-zero exit code means
    /// stdout is a plain-text error (<c>error:</c> code, <c>message:</c> text, and optional <c>hint:</c> text).
    /// </summary>
    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
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
                await _stdout.WriteLineAsync(MarkdownProjection.RenderHelp(command.HelpSubject)).ConfigureAwait(false);
                return 0;
            }

            if (command.Name == "version")
            {
                await _stdout.WriteLineAsync(VersionText).ConfigureAwait(false);
                return 0;
            }

            if (command.Name == "init")
            {
                var result = InitCommandExecutor.Execute(command);
                await _stdout.WriteLineAsync(MarkdownProjection.Render(result)).ConfigureAwait(false);
                return 0;
            }

            var data = await RoslynCommandExecutor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            await _stdout.WriteLineAsync(MarkdownProjection.Render(data)).ConfigureAwait(false);
            return 0;
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

        var output = $"error: {errorCode}\nmessage: {errorMessage}";
        if (!string.IsNullOrWhiteSpace(errorHint))
        {
            output += $"\nhint: {errorHint}";
        }

        await _stdout.WriteLineAsync(output).ConfigureAwait(false);
        return exitCode;
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
