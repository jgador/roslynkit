using System.Text.RegularExpressions;

namespace FixtureApp;

public interface IMessageSource
{
    string GetMessage(string name);
}

public sealed partial class GeneratedMessageSource : IMessageSource
{
    [GeneratedRegex("hello", RegexOptions.IgnoreCase)]
    private static partial Regex HelloRegex();

    public string GetMessage(string name)
    {
        return $"{name}:{HelloRegex().IsMatch("hello")}";
    }
}

public sealed class Consumer
{
    private readonly IMessageSource _source = new GeneratedMessageSource();

    public string Run()
    {
        var source = _source;
        return source.GetMessage("world");
    }
}
