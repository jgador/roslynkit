using System.Runtime.InteropServices;

namespace RoslynKit;

/// <summary>
/// Converts process lifetime signals into cooperative cancellation for command execution.
/// </summary>
internal sealed class ProcessLifetimeCancellation : IDisposable
{
    private readonly CancellationTokenSource _cancellationSource = new();
    private readonly PosixSignalRegistration? _hangupRegistration;
    private readonly PosixSignalRegistration? _interruptRegistration;
    private readonly PosixSignalRegistration? _terminateRegistration;
    private int _disposed;

    public ProcessLifetimeCancellation()
    {
        Console.CancelKeyPress += HandleConsoleCancelKeyPress;
        if (!OperatingSystem.IsWindows())
        {
            _hangupRegistration = PosixSignalRegistration.Create(
                PosixSignal.SIGHUP,
                HandlePosixSignal);
            _interruptRegistration = PosixSignalRegistration.Create(
                PosixSignal.SIGINT,
                HandlePosixSignal);
            _terminateRegistration = PosixSignalRegistration.Create(
                PosixSignal.SIGTERM,
                HandlePosixSignal);
        }
    }

    public CancellationToken Token => _cancellationSource.Token;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Console.CancelKeyPress -= HandleConsoleCancelKeyPress;
        _hangupRegistration?.Dispose();
        _interruptRegistration?.Dispose();
        _terminateRegistration?.Dispose();
        _cancellationSource.Dispose();
    }

    private void HandleConsoleCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        RequestCancellation();
    }

    private void HandlePosixSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        RequestCancellation();
    }

    private void RequestCancellation()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _cancellationSource.Cancel();
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
    }
}
