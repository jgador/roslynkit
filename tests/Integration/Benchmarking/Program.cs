using RoslynKit.Benchmarking;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += Cancel;
try
{
    return await BenchmarkApplication.CreateDefault().RunAsync(args, cancellation.Token);
}
catch (BenchmarkException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("error: benchmark canceled");
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"error: {exception.GetType().Name}: {exception.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= Cancel;
}

void Cancel(object? sender, ConsoleCancelEventArgs eventArgs)
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
}
