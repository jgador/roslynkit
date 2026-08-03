namespace RoslynKit.Tests;

public sealed class DaemonBootstrapLeaseTests
{
    [Fact]
    public void TryAcquire_AllowsOneStarterAndReleasesForNextClient()
    {
        var endpointName = $"roslynkit-test-{Guid.NewGuid():N}";

        using (var first = DaemonBootstrapLease.TryAcquire(endpointName))
        {
            Assert.NotNull(first);
            Assert.Null(DaemonBootstrapLease.TryAcquire(endpointName));
        }

        using var replacement = DaemonBootstrapLease.TryAcquire(endpointName);
        Assert.NotNull(replacement);
    }

    [Fact]
    public void CreateMutexName_UsesSeparateGlobalWindowsNamespace()
    {
        var name = DaemonBootstrapLease.CreateMutexName("endpoint");

        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(@"Global\roslynkit-daemon-bootstrap-", name, StringComparison.Ordinal);
        }
        else
        {
            Assert.StartsWith("roslynkit-daemon-bootstrap-", name, StringComparison.Ordinal);
            Assert.DoesNotContain('\\', name);
        }

        Assert.NotEqual(DaemonLifetimeLease.CreateMutexName("endpoint"), name);
    }
}
