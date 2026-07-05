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
    /// <summary>
    /// Creates metadata for a boolean switch option with no value token.
    /// </summary>
    public static OptionSpec Flag(char? shortName, string longName, string description)
    {
        return new OptionSpec(shortName, longName, OptionKind.Flag, null, description);
    }

    /// <summary>
    /// Creates metadata for a string-valued option used by parser binding and help output.
    /// </summary>
    public static OptionSpec String(char? shortName, string longName, string valueName, string description, bool required = false)
    {
        return new OptionSpec(shortName, longName, OptionKind.String, valueName, description, required);
    }

    /// <summary>
    /// Creates metadata for an integer option with the minimum value enforced by parser validation.
    /// </summary>
    public static OptionSpec Integer(char? shortName, string longName, string valueName, string description, bool required = false, int minimumValue = 1)
    {
        return new OptionSpec(shortName, longName, OptionKind.Integer, valueName, description, required, minimumValue);
    }
}

/// <summary>
/// Classifies how the parser should bind and validate one command option.
/// </summary>
public enum OptionKind
{
    /// <summary>
    /// Option presence maps directly to a boolean value and does not consume a following token.
    /// </summary>
    Flag,

    /// <summary>
    /// Option consumes a non-empty string value.
    /// </summary>
    String,

    /// <summary>
    /// Option consumes an integer value subject to command metadata constraints.
    /// </summary>
    Integer,
}
