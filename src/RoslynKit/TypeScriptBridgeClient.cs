using System.Diagnostics;
using System.Text.Json;

namespace RoslynKit;

/// <summary>
/// Maintains one JSON-lines Node bridge and correlates commands with its native-preview snapshot.
/// </summary>
internal sealed class TypeScriptBridgeClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Process _process;
    private readonly StreamWriter _standardInput;
    private readonly StreamReader _standardOutput;
    private readonly Task<string> _standardError;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private int _nextRequestId;
    private int _disposed;

    private TypeScriptBridgeClient(Process process)
    {
        _process = process;
        _standardInput = process.StandardInput;
        _standardOutput = process.StandardOutput;
        _standardError = process.StandardError.ReadToEndAsync();
    }

    public static async Task<TypeScriptBridgeClient> StartAsync(
        string configPath,
        CancellationToken cancellationToken)
    {
        var canonicalConfig = PathCanonicalizer.ResolveExistingPath(configPath);
        var runtime = await TypeScriptRuntimeResolver.ResolveAsync(
            canonicalConfig,
            cancellationToken).ConfigureAwait(false);
        var startInfo = new ProcessStartInfo(runtime.NodePath)
        {
            WorkingDirectory = Path.GetDirectoryName(canonicalConfig)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(runtime.BridgePath);
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(canonicalConfig);
        startInfo.ArgumentList.Add("--native-preview-root");
        startInfo.ArgumentList.Add(runtime.NativePreviewRoot);
        startInfo.Environment["ROSLYNKIT_INVOCATION_DIRECTORY"] = Environment.CurrentDirectory;

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Node.js did not start the RoslynKit TypeScript bridge.");
        }

        var client = new TypeScriptBridgeClient(process);
        try
        {
            var initialization = await client.SendAsync(
                "debug-state",
                new Dictionary<string, string>(StringComparer.Ordinal),
                cancellationToken).ConfigureAwait(false);
            initialization.EnsureSuccessful();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task<TypeScriptBridgeResponse> ExecuteAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        return SendAsync(command.Name, command.Options, cancellationToken);
    }

    public Task<TypeScriptBridgeResponse> RefreshAsync(CancellationToken cancellationToken)
    {
        return SendAsync(
            "refresh",
            new Dictionary<string, string>(StringComparer.Ordinal),
            cancellationToken);
    }

    public async Task<TypeScriptCorpus> BuildCorpusAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            "corpus",
            new Dictionary<string, string>(StringComparer.Ordinal),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessful();
        if (response.Stdout.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The TypeScript bridge returned an invalid search corpus payload.");
        }

        return response.Stdout.Deserialize<TypeScriptCorpus>(JsonOptions)
            ?? throw new InvalidOperationException("The TypeScript bridge returned an empty search corpus payload.");
    }

    internal async Task<TypeScriptBridgeState> CaptureStateAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            "debug-state",
            new Dictionary<string, string>(StringComparer.Ordinal),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessful();
        return response.State
            ?? throw new InvalidOperationException("The TypeScript bridge did not return lifecycle state.");
    }

    private async Task<TypeScriptBridgeResponse> SendAsync(
        string command,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var requestId = Interlocked.Increment(ref _nextRequestId);
            var request = new TypeScriptBridgeRequest(requestId, command, options);
            cancellationToken.ThrowIfCancellationRequested();
            await _standardInput.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions)).ConfigureAwait(false);
            await _standardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            // Once a request is on the wire, consume its response before observing cancellation so
            // the maintained stream cannot hand a stale response to the next daemon command.
            var line = await _standardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
            if (line is null)
            {
                var error = await ReadProcessFailureAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"The TypeScript bridge exited before replying to '{command}'.{error}");
            }

            var response = JsonSerializer.Deserialize<TypeScriptBridgeResponse>(line, JsonOptions)
                ?? throw new InvalidOperationException("The TypeScript bridge returned an empty response.");
            if (response.Id != requestId)
            {
                throw new InvalidOperationException(
                    $"The TypeScript bridge response ID {response.Id} did not match request ID {requestId}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return response;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<string> ReadProcessFailureAsync()
    {
        if (!_process.HasExited)
        {
            return string.Empty;
        }

        var error = (await _standardError.ConfigureAwait(false)).Trim();
        return error.Length == 0
            ? $" Exit code: {_process.ExitCode}."
            : $" Exit code: {_process.ExitCode}. {error}";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _standardInput.Dispose();
            if (!_process.WaitForExit(2_000))
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2_000);
            }
        }
        catch
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }
        }
        finally
        {
            _standardOutput.Dispose();
            _process.Dispose();
            _requestGate.Dispose();
        }
    }
}

internal sealed record TypeScriptBridgeRequest(
    int Id,
    string Command,
    IReadOnlyDictionary<string, string> Options);

internal sealed record TypeScriptBridgeResponse(
    int Id,
    int ExitCode,
    JsonElement Stdout,
    string Stderr,
    TypeScriptBridgeState? State)
{
    public CliProcessResult ToProcessResult()
    {
        var stdout = Stdout.ValueKind == JsonValueKind.String
            ? Stdout.GetString() ?? string.Empty
            : Stdout.GetRawText();
        return new CliProcessResult(ExitCode, stdout, Stderr);
    }

    public void EnsureSuccessful()
    {
        if (ExitCode == 0)
        {
            return;
        }

        var result = ToProcessResult();
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(result.Stdout)
                ? "The TypeScript bridge command failed."
                : result.Stdout.ReplaceLineEndings(" ").Trim());
    }
}

internal sealed record TypeScriptBridgeState(
    int BridgeProcessId,
    int? NativeProcessId,
    string ApiInstanceId,
    int? SnapshotId,
    int RefreshCount,
    int CommandCount,
    string ConfigPath);

internal sealed record TypeScriptCorpus(
    string TargetPath,
    string Fingerprint,
    IReadOnlyList<TypeScriptCorpusRecord> Records,
    TypeScriptBridgeState State);

internal sealed record TypeScriptCorpusRecord(
    string SymbolKey,
    string ProjectPath,
    string ProjectName,
    string Kind,
    string Name,
    string DisplayName,
    string Selector,
    string Path,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string? Documentation,
    string? Signature,
    string? Comments,
    string? Body,
    string NameTokens,
    string ContainingTokens,
    string DetailsTokens,
    string PathTokens,
    string BodyTokens);
