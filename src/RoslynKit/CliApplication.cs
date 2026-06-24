using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynKit;

public sealed class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly TextWriter _stdout;

    public CliApplication(TextWriter stdout)
    {
        _stdout = stdout;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        JsonEnvelope envelope;
        var exitCode = 0;

        try
        {
            var command = CliParser.Parse(args);

            if (command.IsHelp)
            {
                envelope = JsonEnvelope.ForSuccess("help", HelpResult.Create(command.HelpSubject));
            }
            else
            {
                var data = await RoslynCommandExecutor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                envelope = JsonEnvelope.ForSuccess(command.Name, data);
            }
        }
        catch (CliUsageException ex)
        {
            exitCode = 2;
            envelope = JsonEnvelope.Failure(ex.CommandName, ErrorInfo.Usage(ex.Message));
        }
        catch (OperationCanceledException)
        {
            exitCode = 130;
            envelope = JsonEnvelope.Failure("unknown", ErrorInfo.Canceled("Operation was canceled."));
        }
        catch (Exception ex)
        {
            exitCode = 1;
            envelope = JsonEnvelope.Failure("unknown", ErrorInfo.Internal(ex.GetType().Name, ex.Message));
        }

        await _stdout.WriteLineAsync(JsonSerializer.Serialize(envelope, JsonOptions)).ConfigureAwait(false);
        return exitCode;
    }
}

public sealed record JsonEnvelope(
    int SchemaVersion,
    string Tool,
    string Command,
    bool Success,
    object? Data,
    IReadOnlyList<ErrorInfo> Errors)
{
    public static JsonEnvelope ForSuccess(string command, object data)
    {
        return new JsonEnvelope(1, "roslynkit", command, true, data, Array.Empty<ErrorInfo>());
    }

    public static JsonEnvelope Failure(string command, params ErrorInfo[] errors)
    {
        return new JsonEnvelope(1, "roslynkit", command, false, null, errors);
    }
}

public sealed record ErrorInfo(string Code, string Message, string? Detail = null)
{
    public static ErrorInfo Usage(string message)
    {
        return new ErrorInfo("usage", message);
    }

    public static ErrorInfo Canceled(string message)
    {
        return new ErrorInfo("canceled", message);
    }

    public static ErrorInfo Internal(string code, string message)
    {
        return new ErrorInfo(code, message);
    }
}

public sealed record HelpResult(string Name, string Description, IReadOnlyList<CommandHelp> Commands)
{
    public static HelpResult Create(BuiltinCommand? subject)
    {
        IReadOnlyList<BuiltinCommand> commands = subject is null
            ? BuiltinCommandRegistry.Commands
            : [subject];

        return new HelpResult(
            "roslynkit",
            "Unofficial Roslyn-powered C# code intelligence CLI for coding agents and terminal workflows. This is not an MCP server and not an LSP client.",
            commands.Select(CommandHelp.FromBuiltin).ToArray());
    }
}

public sealed record CommandHelp(string Name, string Description, IReadOnlyList<string> Usage, IReadOnlyList<OptionHelp> Options)
{
    public static CommandHelp FromBuiltin(BuiltinCommand command)
    {
        return new CommandHelp(
            command.Name,
            command.Description,
            command.Usage,
            command.Options.Select(OptionHelp.FromSpec).ToArray());
    }
}

public sealed record OptionHelp(string Name, string? ShortName, string Kind, string? ValueName, string Description, bool Required)
{
    public static OptionHelp FromSpec(OptionSpec option)
    {
        return new OptionHelp(
            option.LongName,
            option.ShortName is null ? null : $"-{option.ShortName}",
            option.Kind.ToString(),
            option.ValueName,
            option.Description,
            option.Required);
    }
}
