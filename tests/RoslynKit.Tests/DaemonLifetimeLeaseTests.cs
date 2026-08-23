namespace RoslynKit.Tests;

public sealed class DaemonLifetimeLeaseTests
{
    [Fact]
    public void CreateMutexName_UsesGlobalWindowsNamespace()
    {
        var name = DaemonLifetimeLease.CreateMutexName("roslynkit-test");

        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(@"Global\", name, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain('\\', name);
        }
    }

    [Fact]
    public void TryAcquire_AllowsOneOwnerAndReleasesForNextServer()
    {
        var endpointName = $"roslynkit-test-{Guid.NewGuid():N}";
        using var first = DaemonLifetimeLease.TryAcquire(endpointName);

        using var competing = DaemonLifetimeLease.TryAcquire(endpointName);

        Assert.NotNull(first);
        Assert.Null(competing);
        first.Dispose();
        using var replacement = DaemonLifetimeLease.TryAcquire(endpointName);
        Assert.NotNull(replacement);
    }
}
