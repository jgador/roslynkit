namespace RoslynKit.Tests;

/// <summary>
/// Emits short probe messages through xUnit output channels so runner visibility can be validated quickly.
/// </summary>
public sealed class XunitOutputProbeTests
{
    private readonly ITestOutputHelper _output;

    public XunitOutputProbeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Explicit = true)]
    public void OutputChannels_AreVisible_ForExplicitRuns()
    {
        _output.WriteLine("PROBE ITestOutputHelper: if showLiveOutput is working, this line should appear.");

        if (TestContext.Current is { } context)
        {
            context.SendDiagnosticMessage("PROBE DiagnosticMessage: if diagnosticMessages is working, this line should appear.");
        }
    }
}
