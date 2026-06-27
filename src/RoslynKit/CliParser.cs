namespace RoslynKit;

/// <summary>
/// Binds RoslynKit command-line tokens to built-in command metadata, option values, and command-specific usage validation.
/// </summary>
public static class CliParser
{
    /// <summary>
    /// Converts raw command-line tokens into a parsed RoslynKit command or help request.
    /// </summary>
    public static ParsedCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || IsHelpToken(args[0]))
        {
            return ParsedCommand.Help();
        }

        args = RewriteVersionCommand(args);

        if (args[0] == "help")
        {
            return ParseHelp(args);
        }

        var builtin = BuiltinCommandRegistry.GetBuiltin(args[0]);
        if (builtin is null)
        {
            throw new CliUsageException("unknown", $"Unknown command '{args[0]}'. Use 'roslynkit help' for command metadata.");
        }

        if (args.Skip(1).Any(IsHelpToken))
        {
            return ParsedCommand.Help(builtin);
        }

        var options = ParseOptions(builtin, args, firstOptionIndex: 1);
        ValidateRequiredOptions(builtin, options);
        ValidateCommandOptions(builtin.Name, options);

        return new ParsedCommand(builtin.Name, builtin, options, HelpSubject: null);
    }

    private static IReadOnlyList<string> RewriteVersionCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "--version")
        {
            return args;
        }

        var rewritten = args.ToArray();
        rewritten[0] = "version";
        return rewritten;
    }

    private static ParsedCommand ParseHelp(IReadOnlyList<string> args)
    {
        if (args.Count == 2 && IsHelpToken(args[1]))
        {
            return ParsedCommand.Help();
        }

        return args.Count switch
        {
            1 => ParsedCommand.Help(),
            2 when BuiltinCommandRegistry.GetBuiltin(args[1]) is { } subject => ParsedCommand.Help(subject),
            2 => throw new CliUsageException("help", $"Unknown command '{args[1]}'."),
            _ => throw new CliUsageException("help", "Usage: roslynkit help [<command>]"),
        };
    }

    private static Dictionary<string, string> ParseOptions(BuiltinCommand builtin, IReadOnlyList<string> args, int firstOptionIndex)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = firstOptionIndex; index < args.Count; index++)
        {
            var token = args[index];
            if (IsHelpToken(token))
            {
                break;
            }

            if (token == "--")
            {
                throw new CliUsageException(builtin.Name, "Unexpected positional arguments after '--'.");
            }

            if (!token.StartsWith("-", StringComparison.Ordinal))
            {
                throw new CliUsageException(builtin.Name, $"Unexpected positional argument '{token}'. Options must use --name value syntax.");
            }

            var option = ParseOptionName(builtin, token, out var inlineValue, out var negated);
            if (parsed.ContainsKey(option.LongName))
            {
                throw new CliUsageException(builtin.Name, $"Option '--{option.LongName}' was specified more than once.");
            }

            if (option.Kind == OptionKind.Flag)
            {
                if (inlineValue is not null)
                {
                    throw new CliUsageException(builtin.Name, $"Option '--{option.LongName}' does not take a value.");
                }

                parsed.Add(option.LongName, negated ? "false" : "true");
                continue;
            }

            if (negated)
            {
                throw new CliUsageException(builtin.Name, $"Option '--{option.LongName}' cannot be negated.");
            }

            var value = inlineValue;
            if (value is null)
            {
                if (index + 1 >= args.Count || args[index + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    throw new CliUsageException(builtin.Name, $"Option '--{option.LongName}' requires a value.");
                }

                value = args[++index];
            }

            ValidateOptionValue(builtin, option, value);
            parsed.Add(option.LongName, value);
        }

        return parsed;
    }

    private static OptionSpec ParseOptionName(BuiltinCommand builtin, string token, out string? inlineValue, out bool negated)
    {
        inlineValue = null;
        negated = false;

        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            var optionText = token[2..];
            var equalsIndex = optionText.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex >= 0)
            {
                inlineValue = optionText[(equalsIndex + 1)..];
                optionText = optionText[..equalsIndex];
            }

            if (optionText.StartsWith("no-", StringComparison.Ordinal))
            {
                negated = true;
                optionText = optionText[3..];
            }

            if (string.IsNullOrWhiteSpace(optionText))
            {
                throw new CliUsageException(builtin.Name, "Option names cannot be empty.");
            }

            return FindLongOption(builtin, optionText);
        }

        if (token.Length != 2)
        {
            throw new CliUsageException(builtin.Name, $"Unknown short option '{token}'.");
        }

        return FindShortOption(builtin, token[1]);
    }

    private static OptionSpec FindLongOption(BuiltinCommand builtin, string name)
    {
        var matches = builtin.Options.Where(option => option.LongName.StartsWith(name, StringComparison.Ordinal)).ToArray();
        return matches.Length switch
        {
            0 => throw new CliUsageException(builtin.Name, $"Unknown option '--{name}' for command '{builtin.Name}'."),
            1 => matches[0],
            _ => throw new CliUsageException(builtin.Name, $"Option '--{name}' is ambiguous for command '{builtin.Name}'."),
        };
    }

    private static OptionSpec FindShortOption(BuiltinCommand builtin, char shortName)
    {
        foreach (var option in builtin.Options)
        {
            if (option.ShortName == shortName)
            {
                return option;
            }
        }

        throw new CliUsageException(builtin.Name, $"Unknown short option '-{shortName}' for command '{builtin.Name}'.");
    }

    private static void ValidateOptionValue(BuiltinCommand builtin, OptionSpec option, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CliUsageException(builtin.Name, $"Option '--{option.LongName}' requires a non-empty value.");
        }

        if (option.Kind != OptionKind.Integer)
        {
            return;
        }

        var minimumValue = option.MinimumValue ?? 1;
        if (!int.TryParse(value, out var parsed) || parsed < minimumValue)
        {
            throw new CliUsageException(builtin.Name, $"Option '--{option.LongName}' must be an integer greater than or equal to {minimumValue}.");
        }
    }

    private static void ValidateRequiredOptions(BuiltinCommand builtin, IReadOnlyDictionary<string, string> options)
    {
        foreach (var option in builtin.Options)
        {
            if (option.Required && !options.ContainsKey(option.LongName))
            {
                throw new CliUsageException(builtin.Name, $"Missing required option '--{option.LongName}'.");
            }
        }
    }

    private static void ValidateCommandOptions(string commandName, IReadOnlyDictionary<string, string> options)
    {
        switch (commandName)
        {
            case "document-text":
                ValidateDocumentSelector(commandName, options);
                ValidateDocumentTextRangeOptions(commandName, options);
                break;

            case "document-symbols":
            case "definition":
            case "type-definition":
            case "references":
            case "implementations":
            case "quick-info":
            case "signature-help":
                ValidateDocumentSelector(commandName, options);
                break;
        }
    }

    private static void ValidateDocumentSelector(string commandName, IReadOnlyDictionary<string, string> options)
    {
        var hasFile = options.ContainsKey("file");
        var hasDocumentKey = options.ContainsKey("document-key");

        if (hasFile == hasDocumentKey)
        {
            throw new CliUsageException(commandName, "Exactly one of '--file' or '--document-key' is required.");
        }
    }

    private static void ValidateDocumentTextRangeOptions(string commandName, IReadOnlyDictionary<string, string> options)
    {
        if (options.ContainsKey("start-column") && !options.ContainsKey("start-line"))
        {
            throw new CliUsageException(commandName, "Option '--start-column' requires '--start-line'.");
        }

        if (options.ContainsKey("end-column") && !options.ContainsKey("end-line"))
        {
            throw new CliUsageException(commandName, "Option '--end-column' requires '--end-line'.");
        }
    }

    private static bool IsHelpToken(string token)
    {
        return token is "-h" or "--help" or "/?";
    }
}

/// <summary>
/// Represents a parsed RoslynKit invocation with bound command metadata and validated option accessors.
/// </summary>
public sealed record ParsedCommand(
    string Name,
    BuiltinCommand? Builtin,
    IReadOnlyDictionary<string, string> Options,
    BuiltinCommand? HelpSubject)
{
    public bool IsHelp => Name == "help";

    public static ParsedCommand Help(BuiltinCommand? subject = null)
    {
        return new ParsedCommand("help", null, new Dictionary<string, string>(StringComparer.Ordinal), subject);
    }

    public string Required(string name)
    {
        if (!Options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new CliUsageException(Name, $"Missing required option '--{name}'.");
        }

        return value;
    }

    public string? Optional(string name)
    {
        return Options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    public bool Flag(string name)
    {
        return Options.TryGetValue(name, out var value) && bool.TryParse(value, out var parsed) && parsed;
    }

    public int? OptionalInt(string name, int minimumValue)
    {
        if (!Options.TryGetValue(name, out var value))
        {
            return null;
        }

        if (!int.TryParse(value, out var parsed) || parsed < minimumValue)
        {
            throw new CliUsageException(Name, $"Option '--{name}' must be an integer greater than or equal to {minimumValue}.");
        }

        return parsed;
    }

    public int OptionalInt(string name, int defaultValue, int minimumValue)
    {
        return OptionalInt(name, minimumValue) ?? defaultValue;
    }
}

/// <summary>
/// Represents a user-facing CLI usage failure that should be returned as a usage envelope.
/// </summary>
public sealed class CliUsageException : Exception
{
    public CliUsageException(string commandName, string message)
        : base(message)
    {
        CommandName = commandName;
    }

    public string CommandName { get; }
}
