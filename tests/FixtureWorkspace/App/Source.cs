using System.Text.RegularExpressions;

namespace FixtureApp;

/// <summary>
/// Fixture interface used by semantic navigation tests for implementations, references, and type resolution.
/// </summary>
public interface IMessageSource
{
    /// <summary>
    /// Produces a deterministic message so tests can locate interface and implementation symbols.
    /// </summary>
    string GetMessage(string name);
}

/// <summary>
/// Fixture implementation that combines a source-declared type with a source-generated regex member.
/// </summary>
public sealed partial class GeneratedMessageSource : IMessageSource
{
    [GeneratedRegex("hello", RegexOptions.IgnoreCase)]
    private static partial Regex HelloRegex();

    /// <summary>
    /// Implements the fixture message contract with a source-generated regex call in the method body.
    /// </summary>
    public string GetMessage(string name)
    {
        return $"{name}:{HelloRegex().IsMatch("hello")}";
    }
}

/// <summary>
/// Fixture caller type used to exercise references, definitions, and type-definition across local variables.
/// </summary>
public sealed class Consumer
{
    private readonly IMessageSource _source = new GeneratedMessageSource();

    /// <summary>
    /// Calls the fixture message source through an interface-typed local variable.
    /// </summary>
    public string Run()
    {
        var source = _source;
        return source.GetMessage("world");
    }
}
