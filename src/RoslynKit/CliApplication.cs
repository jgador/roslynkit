using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynKit;

/// <summary>
/// Owns the top-level CLI flow from argument parsing through command dispatch and stdout output.
/// </summary>
public sealed class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static readonly string VersionText = $"roslynkit version {ResolveDisplayVersion()}";

    private readonly TextWriter _stdout;

    public CliApplication(TextWriter stdout)
    {
        _stdout = stdout;
    }

    /// <summary>
    /// Parses arguments, dispatches help or command execution, and writes either a JSON envelope or the plain-text version response.
    /// </summary>
    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        JsonEnvelope envelope;
        var exitCode = 0;
        var compact = false;

        try
        {
            var command = CliParser.Parse(args);
            compact = command.IsCompact;

            if (command.IsHelp)
            {
                envelope = JsonEnvelope.ForSuccess(HelpResult.Create(command.HelpSubject));
            }
            else if (command.Name == "version")
            {
                return await WriteVersionAsync().ConfigureAwait(false);
            }
            else
            {
                var data = await RoslynCommandExecutor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                envelope = JsonEnvelope.ForSuccess(compact ? CompactProjection.ProjectData(data) : data);
            }
        }
        catch (CliUsageException ex)
        {
            exitCode = 2;
            envelope = JsonEnvelope.Failure(ErrorInfo.Usage(ex.Message));
        }
        catch (OperationCanceledException)
        {
            exitCode = 130;
            envelope = JsonEnvelope.Failure(ErrorInfo.Canceled("Operation was canceled."));
        }
        catch (Exception ex)
        {
            exitCode = 1;
            envelope = JsonEnvelope.Failure(ErrorInfo.Internal(ex.GetType().Name, ex.Message));
        }

        await _stdout.WriteLineAsync(JsonSerializer.Serialize(envelope, compact ? CompactJsonOptions : JsonOptions)).ConfigureAwait(false);
        return exitCode;
    }

    private async Task<int> WriteVersionAsync()
    {
        await _stdout.WriteLineAsync(VersionText).ConfigureAwait(false);
        return 0;
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

/// <summary>
/// Represents the top-level JSON stdout envelope for command results and failures. A response carries
/// either <c>data</c> (success) or <c>errors</c> (failure); the absence of <c>errors</c> is an implicit
/// success, so no constant frame fields are emitted.
/// </summary>
public sealed class JsonEnvelope
{
    public JsonEnvelope(object? data, IReadOnlyList<ErrorInfo>? errors)
    {
        Data = data;
        Errors = errors;
    }

    [JsonPropertyName("data")]
    public object? Data { get; }

    [JsonPropertyName("errors")]
    public IReadOnlyList<ErrorInfo>? Errors { get; }

    public static JsonEnvelope ForSuccess(object data)
    {
        return new JsonEnvelope(data, null);
    }

    public static JsonEnvelope Failure(params ErrorInfo[] errors)
    {
        return new JsonEnvelope(null, errors);
    }
}

/// <summary>
/// Represents one error entry in a failed JSON envelope.
/// </summary>
public sealed class ErrorInfo
{
    public ErrorInfo(string code, string message, string? detail = null)
    {
        Code = code;
        Message = message;
        Detail = detail;
    }

    [JsonPropertyName("code")]
    public string Code { get; }

    [JsonPropertyName("message")]
    public string Message { get; }

    [JsonPropertyName("detail")]
    public string? Detail { get; }

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

/// <summary>
/// Represents the <c>help</c> command payload built from the built-in command table.
/// </summary>
public sealed class HelpResult
{
    public HelpResult(string name, string description, IReadOnlyList<CommandHelp> commands)
    {
        Name = name;
        Description = description;
        Commands = commands;
    }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("description")]
    public string Description { get; }

    [JsonPropertyName("commands")]
    public IReadOnlyList<CommandHelp> Commands { get; }

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

/// <summary>
/// Represents one built-in command entry inside the <c>help</c> payload.
/// </summary>
public sealed class CommandHelp
{
    public CommandHelp(string name, string description, IReadOnlyList<string> usage, IReadOnlyList<OptionHelp> options)
    {
        Name = name;
        Description = description;
        Usage = usage;
        Options = options;
    }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("description")]
    public string Description { get; }

    [JsonPropertyName("usage")]
    public IReadOnlyList<string> Usage { get; }

    [JsonPropertyName("options")]
    public IReadOnlyList<OptionHelp> Options { get; }

    public static CommandHelp FromBuiltin(BuiltinCommand command)
    {
        return new CommandHelp(
            command.Name,
            command.Description,
            command.Usage,
            command.Options.Select(OptionHelp.FromSpec).ToArray());
    }
}

/// <summary>
/// Represents one built-in option entry inside the <c>help</c> payload.
/// </summary>
public sealed class OptionHelp
{
    public OptionHelp(string name, string? shortName, string kind, string? valueName, string description, bool required)
    {
        Name = name;
        ShortName = shortName;
        Kind = kind;
        ValueName = valueName;
        Description = description;
        Required = required;
    }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("shortName")]
    public string? ShortName { get; }

    [JsonPropertyName("kind")]
    public string Kind { get; }

    [JsonPropertyName("valueName")]
    public string? ValueName { get; }

    [JsonPropertyName("description")]
    public string Description { get; }

    [JsonPropertyName("required")]
    public bool Required { get; }

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
