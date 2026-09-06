using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace RoslynKit;

/// <summary>
/// Carries private requests and cancellation over the standard streams inherited from a single owning server.
/// </summary>
internal sealed record RepositoryWorkerMessage(long Id, string Kind, string[]? Args = null);

/// <summary>
/// Carries an existing CLI result back to the owning server without writing command output to the transport.
/// </summary>
internal sealed record RepositoryWorkerReply(long Id, CliProcessResult Result);

/// <summary>
/// Dispatches private worker messages concurrently and drains canceled requests before releasing workspace ownership.
/// </summary>
internal static class RepositoryWorkerProtocol
{
    public static async Task RunAsync(
        Stream input,
        Stream output,
        Func<string[], CancellationToken, Task<CliProcessResult>> execute,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var writeGate = new SemaphoreSlim(1, 1);
        var requests = new ConcurrentDictionary<long, ActiveRequest>();
        try
        {
            while (await reader.ReadLineAsync(lifetime.Token).AsTask().WaitAsync(lifetime.Token).ConfigureAwait(false) is { } line)
            {
                var message = JsonSerializer.Deserialize<RepositoryWorkerMessage>(line)
                    ?? throw new InvalidDataException("The worker received an empty message.");
                if (message.Kind == "stop")
                {
                    break;
                }

                if (message.Kind == "cancel")
                {
                    if (requests.TryGetValue(message.Id, out var active))
                    {
                        active.Cancel();
                    }

                    continue;
                }

                if (message.Kind != "query" || message.Args is null)
                {
                    throw new InvalidDataException("The worker received an invalid message.");
                }

                var request = new ActiveRequest(lifetime.Token);
                if (!requests.TryAdd(message.Id, request))
                {
                    request.Dispose();
                    throw new InvalidDataException("The worker received a duplicate request identifier.");
                }

                request.Execution = Task.Run(() => ExecuteAsync(message, request), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(requests.Values.Select(request => request.Execution)).ConfigureAwait(false);
        }

        async Task ExecuteAsync(RepositoryWorkerMessage message, ActiveRequest request)
        {
            try
            {
                CliProcessResult result;
                try
                {
                    result = await execute(message.Args!, request.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    result = CliProcessResult.FromException(exception);
                }

                await writeGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new RepositoryWorkerReply(message.Id, result))).ConfigureAwait(false);
                }
                finally
                {
                    writeGate.Release();
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                requests.TryRemove(message.Id, out _);
                request.Dispose();
            }
        }
    }

    /// <summary>
    /// Keeps request cancellation safe when completion races a cancellation message.
    /// </summary>
    private sealed class ActiveRequest(CancellationToken lifetime) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        private readonly object _gate = new();
        private bool _disposed;

        public CancellationToken Token => _cancellation.Token;

        public Task Execution { get; set; } = Task.CompletedTask;

        public void Cancel()
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _cancellation.Cancel();
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _cancellation.Dispose();
            }
        }
    }
}
