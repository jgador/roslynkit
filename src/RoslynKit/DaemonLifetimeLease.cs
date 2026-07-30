namespace RoslynKit;

/// <summary>
/// Holds one cross-process mutex on a dedicated owner thread for the lifetime of a daemon endpoint.
/// </summary>
internal sealed class DaemonLifetimeLease : IDisposable
{
    private const string MutexPrefix = "roslynkit-daemon-lease-";

    private readonly ManualResetEventSlim _initialized = new(initialState: false);
    private readonly ManualResetEventSlim _release = new(initialState: false);
    private readonly Thread _ownerThread;
    private Exception? _initializationFailure;
    private bool _acquired;
    private int _disposed;

    private DaemonLifetimeLease(string endpointName)
    {
        _ownerThread = new Thread(() => OwnMutex(CreateMutexName(endpointName)))
        {
            IsBackground = true,
            Name = "RoslynKit daemon lifetime lease",
        };
        _ownerThread.Start();
        _initialized.Wait();
    }

    public static DaemonLifetimeLease? TryAcquire(string endpointName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        var lease = new DaemonLifetimeLease(endpointName);
        if (lease._initializationFailure is not null)
        {
            var failure = lease._initializationFailure;
            lease.Dispose();
            throw new InvalidOperationException("The daemon lifetime lease could not be acquired.", failure);
        }

        if (lease._acquired)
        {
            return lease;
        }

        lease.Dispose();
        return null;
    }

    internal static string CreateMutexName(string endpointName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        var name = MutexPrefix + endpointName;
        return OperatingSystem.IsWindows() ? @"Global\" + name : name;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _release.Set();
        _ownerThread.Join();
        _release.Dispose();
        _initialized.Dispose();
    }

    private void OwnMutex(string mutexName)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName);
            try
            {
                _acquired = mutex.WaitOne(millisecondsTimeout: 0);
            }
            catch (AbandonedMutexException)
            {
                _acquired = true;
            }
        }
        catch (Exception exception)
        {
            _initializationFailure = exception;
        }
        finally
        {
            _initialized.Set();
        }

        if (!_acquired || mutex is null)
        {
            mutex?.Dispose();
            return;
        }

        try
        {
            _release.Wait();
        }
        finally
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
