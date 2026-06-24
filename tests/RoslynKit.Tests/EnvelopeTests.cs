using System.Text.Json;

namespace RoslynKit.Tests;

public sealed class EnvelopeTests
{
    [Fact]
    public async Task RunAsync_WritesJsonEnvelope_ForHelp()
    {
        using var writer = new StringWriter();
        var exitCode = await new CliApplication(writer).RunAsync(["help"]);

        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;

        Assert.Equal(0, exitCode);
        Assert.Equal("roslynkit", root.GetProperty("tool").GetString());
        Assert.Equal("help", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
    }
}
