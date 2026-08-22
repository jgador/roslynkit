namespace RoslynKit.Benchmarking;

/// <summary>
/// Reports a deterministic benchmark configuration or execution failure.
/// </summary>
internal sealed class BenchmarkException : Exception
{
    public BenchmarkException(string message)
        : base(message)
    {
    }

    public BenchmarkException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
