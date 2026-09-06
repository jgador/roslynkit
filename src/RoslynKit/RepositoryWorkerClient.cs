using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace RoslynKit;

/// <summary>
/// Allows the pool to retain, query, and cooperatively stop one isolated repository worker.
/// </summary>
internal interface IRepositoryWorker : IAsyncDisposable
{
    bool IsAlive => true;

    Task<CliProcessResult> ExecuteAsync(string[] args, CancellationToken cancellationToken);

    Task<bool> TryRetireAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Owns a private child process and correlates requests without releasing canceled requests before cleanup completes.
/// </summary>
internal sealed class RepositoryWorkerClient : IRepositoryWorker
{
    private readonly Process _process;
    private readonly TextWriter _diagnostics;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<CliProcessResult>> _pending = new();
    private readonly object _pendingGate = new();
    private readonly Task _readTask;
    private readonly Task _errorTask;
    private long _nextId;
    private int _disposed;
    private Exception? _failure;

    private RepositoryWorkerClient(Process process, TextWriter diagnostics)
    {
        _process = process;
        _diagnostics = diagnostics;
        _readTask = ReadRepliesAsync();
        _errorTask = DrainErrorsAsync();
    }

    public bool IsAlive => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _failure) is null && !_process.HasExited;

    public static async Task<IRepositoryWorker> StartAsync(McpQuery query, TextWriter diagnostics, CancellationToken cancellationToken)
    {
        var sdk = await DotnetSdkResolver.ResolveAsync(query.RepositoryRoot, cancellationToken).ConfigureAwait(false);
        var startInfo = new ProcessStartInfo(sdk.DotnetPath)
        {
            WorkingDirectory = query.RepositoryRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add("--repository-worker");
        startInfo.ArgumentList.Add(query.RepositoryRoot);
        if (query.TargetPath is not null)
        {
            startInfo.ArgumentList.Add("--target");
            startInfo.ArgumentList.Add(query.TargetPath);
        }

        if (!query.AutomaticRestore)
        {
            startInfo.ArgumentList.Add("--no-restore");
        }

        DotnetProcessEnvironment.ClearInheritedSdkBindings(startInfo);
        startInfo.Environment["ROSLYNKIT_DOTNET_SDK_PATH"] = sdk.SdkDirectory;
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Cannot start the repository worker.");
        return new RepositoryWorkerClient(process, diagnostics);
    }

    public async Task<CliProcessResult> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<CliProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_failure is { } failure)
            {
                throw new IOException("The repository worker is no longer connected.", failure);
            }

            _pending[id] = completion;
        }
        try
        {
            await SendAsync(new RepositoryWorkerMessage(id, "query", args), cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.Register(() => _ = SendCancellationAsync(id));
            var result = await completion.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (IOException exception)
        {
            FailPendingRequests(exception);
            throw;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<bool> TryRetireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var retirement = ExecuteAsync(["--worker-retire"], CancellationToken.None);
        try
        {
            var result = await retirement.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new IOException($"The repository worker could not complete retirement. {result.Stdout.Trim()}");
            }

            return result.Stdout.Trim() switch
            {
                "true" => true,
                "false" => false,
                _ => throw new IOException("The repository worker returned an invalid retirement acknowledgment."),
            };
        }
        catch (TimeoutException exception)
        {
            var failure = new IOException("The repository worker did not acknowledge retirement within 30 seconds.", exception);
            FailPendingRequests(failure);
            await DisposeAsync().ConfigureAwait(false);
            try
            {
                await retirement.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            throw failure;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            try
            {
                await SendAsync(new RepositoryWorkerMessage(0, "stop"), shutdown.Token, interruptWrite: true).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }

            _process.StandardInput.BaseStream.Close();
            await _process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
            await Task.WhenAll(_readTask, _errorTask).WaitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }

            FailPendingRequests(new IOException("The repository worker exceeded its cooperative shutdown deadline."));
            await _readTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        _process.Dispose();
    }

    private async Task SendCancellationAsync(long id)
    {
        try
        {
            await SendAsync(new RepositoryWorkerMessage(id, "cancel"), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private async Task SendAsync(RepositoryWorkerMessage message, CancellationToken cancellationToken, bool interruptWrite = false)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var writeToken = interruptWrite ? cancellationToken : CancellationToken.None;
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), writeToken).ConfigureAwait(false);
            // Once admitted to the writer, finish the frame; cancellation then waits for the worker's acknowledgment.
            await _process.StandardInput.FlushAsync(writeToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadRepliesAsync()
    {
        Exception failure = new IOException("The repository worker exited before completing its request.");
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var reply = JsonSerializer.Deserialize<RepositoryWorkerReply>(line)
                    ?? throw new InvalidDataException("The repository worker returned an empty response.");
                if (_pending.TryGetValue(reply.Id, out var completion))
                {
                    completion.TrySetResult(reply.Result);
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            FailPendingRequests(failure);
        }
    }

    private void FailPendingRequests(Exception failure)
    {
        lock (_pendingGate)
        {
            Volatile.Write(ref _failure, failure);
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(failure);
            }
        }
    }

    private async Task DrainErrorsAsync()
    {
        while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            await _diagnostics.WriteLineAsync(line).ConfigureAwait(false);
        }
    }
}
