using System.Security.Cryptography;
using System.Text;

namespace RoslynKit;

/// <summary>
/// Bounds retained repository processes and evicts only least-recently-used workers with no foreground or background use.
/// </summary>
internal sealed class McpWorkspacePool : IAsyncDisposable
{
    private readonly int _capacity;
    private readonly Func<McpQuery, CancellationToken, Task<IRepositoryWorker>> _startWorker;
    private readonly Dictionary<string, Entry> _entries = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource _changed = NewSignal();
    private long _clock;
    private bool _disposed;

    public McpWorkspacePool(int capacity, TextWriter diagnostics)
        : this(capacity, (query, token) => RepositoryWorkerClient.StartAsync(query, diagnostics, token))
    {
    }

    internal McpWorkspacePool(int capacity, Func<McpQuery, CancellationToken, Task<IRepositoryWorker>> startWorker)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _startWorker = startWorker;
    }

    public async Task<CliProcessResult> ExecuteAsync(McpQuery query, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var entry = await AcquireAsync(query, linked.Token).ConfigureAwait(false);
        try
        {
            return await entry.Worker.ExecuteAsync(query.Args, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                entry.ActiveRequests--;
                entry.LastUsed = ++_clock;
                if (entry.ActiveRequests == 0 && !entry.Worker.IsAlive)
                {
                    await entry.Worker.DisposeAsync().ConfigureAwait(false);
                    var failedKey = _entries.FirstOrDefault(pair => ReferenceEquals(pair.Value, entry)).Key;
                    if (failedKey is not null)
                    {
                        _entries.Remove(failedKey);
                    }
                }

                SignalChanged();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        Entry[] entries;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = _entries.Values.ToArray();
            _entries.Clear();
            SignalChanged();
        }
        finally
        {
            _gate.Release();
        }

        await Task.WhenAll(entries.Select(entry => entry.Worker.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    private async Task<Entry> AcquireAsync(McpQuery query, CancellationToken cancellationToken)
    {
        var key = $"{query.RepositoryRoot}\0{query.TargetPath}\0{query.AutomaticRestore}";
        var sdkInputs = SdkInputsFingerprint(query.RepositoryRoot);
        while (true)
        {
            Task changed;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_entries.TryGetValue(key, out var existing))
                {
                    if (existing.SdkInputs == sdkInputs && existing.Worker.IsAlive)
                    {
                        existing.ActiveRequests++;
                        return existing;
                    }

                    if (existing.ActiveRequests == 0)
                    {
                        // A changed SDK selection requires a fresh process; cooperative disposal drains any old indexing work.
                        await existing.Worker.DisposeAsync().ConfigureAwait(false);
                        _entries.Remove(key);
                    }
                }

                if (!_entries.ContainsKey(key))
                {
                    if (_entries.Count == _capacity)
                    {
                        foreach (var candidate in _entries.OrderBy(pair => pair.Value.LastUsed).ToArray())
                        {
                            if (candidate.Value.ActiveRequests != 0)
                            {
                                continue;
                            }

                            bool retired;
                            try
                            {
                                retired = !candidate.Value.Worker.IsAlive
                                    || await candidate.Value.Worker.TryRetireAsync(CancellationToken.None).ConfigureAwait(false);
                            }
                            catch
                            {
                                await RetireAsync(candidate.Key, candidate.Value).ConfigureAwait(false);
                                throw;
                            }

                            if (retired)
                            {
                                await RetireAsync(candidate.Key, candidate.Value).ConfigureAwait(false);
                                break;
                            }
                        }
                    }

                    if (_entries.Count < _capacity)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var worker = await _startWorker(query, cancellationToken).ConfigureAwait(false);
                        var created = new Entry(worker, sdkInputs) { ActiveRequests = 1, LastUsed = ++_clock };
                        _entries.Add(key, created);
                        return created;
                    }
                }

                changed = _changed.Task;
            }
            finally
            {
                _gate.Release();
            }

            // Background indexing can become idle without completing a foreground request.
            await Task.WhenAny(changed, Task.Delay(100, cancellationToken)).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string SdkInputsFingerprint(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "global.json");
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            if (File.Exists(path))
            {
                hash.AppendData(File.ReadAllBytes(path));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task RetireAsync(string key, Entry entry)
    {
        try
        {
            await entry.Worker.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _entries.Remove(key);
        }
    }

    private void SignalChanged()
    {
        var changed = _changed;
        _changed = NewSignal();
        changed.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Tracks pool admission independently of a worker's background index lifetime.
    /// </summary>
    private sealed class Entry(IRepositoryWorker worker, string sdkInputs)
    {
        public IRepositoryWorker Worker { get; } = worker;

        public string SdkInputs { get; } = sdkInputs;

        public int ActiveRequests { get; set; }

        public long LastUsed { get; set; }
    }
}
