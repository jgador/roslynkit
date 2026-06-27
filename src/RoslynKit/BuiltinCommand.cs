namespace RoslynKit;

/// <summary>
/// Describes one built-in command definition used for parser binding and help output.
/// </summary>
public sealed record BuiltinCommand(
    string Name,
    string Description,
    IReadOnlyList<string> Usage,
    IReadOnlyList<OptionSpec> Options);

/// <summary>
/// Describes one built-in option definition used for parser binding and help output.
/// </summary>
public sealed record OptionSpec(
    char? ShortName,
    string LongName,
    OptionKind Kind,
    string? ValueName,
    string Description,
    bool Required = false,
    int? MinimumValue = null)
{
    public static OptionSpec Flag(char? shortName, string longName, string description)
    {
        return new OptionSpec(shortName, longName, OptionKind.Flag, null, description);
    }

    public static OptionSpec String(char? shortName, string longName, string valueName, string description, bool required = false)
    {
        return new OptionSpec(shortName, longName, OptionKind.String, valueName, description, required);
    }

    public static OptionSpec Integer(char? shortName, string longName, string valueName, string description, bool required = false, int minimumValue = 1)
    {
        return new OptionSpec(shortName, longName, OptionKind.Integer, valueName, description, required, minimumValue);
    }
}

public enum OptionKind
{
    Flag,
    String,
    Integer,
}
