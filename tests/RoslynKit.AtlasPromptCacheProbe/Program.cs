using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynKit.AtlasPromptCacheProbe;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] args)
    {
        ProbeEnvelope envelope;
        var exitCode = 0;

        try
        {
            var options = AtlasPromptProbeOptions.Parse(args);
            var result = await AtlasPromptProbeRunner.RunAsync(options, CancellationToken.None).ConfigureAwait(false);
            envelope = ProbeEnvelope.ForSuccess("probe", result);
        }
        catch (AtlasPromptProbeUsageException ex)
        {
            exitCode = 2;
            envelope = ProbeEnvelope.Failure("probe", ProbeErrorInfo.Usage(ex.Message));
        }
        catch (OperationCanceledException)
        {
            exitCode = 130;
            envelope = ProbeEnvelope.Failure("probe", ProbeErrorInfo.Canceled("Operation was canceled."));
        }
        catch (Exception ex)
        {
            exitCode = 1;
            envelope = ProbeEnvelope.Failure("probe", ProbeErrorInfo.Internal(ex.GetType().Name, ex.Message));
        }

        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(envelope, JsonOptions)).ConfigureAwait(false);
        return exitCode;
    }
}
