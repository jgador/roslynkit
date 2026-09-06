using System.Text.Json;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies installed SDK selection using independent repository global.json files.
/// </summary>
public sealed class DotnetSdkResolverTests
{
    [Fact]
    public async Task ResolveAsync_SelectsDifferentInstalledSdksForTwoRepositoryRoots()
    {
        var list = await new WorkspaceProcessRunner().RunAsync(
            "dotnet", TestPaths.RepositoryRoot(), ["--list-sdks"], TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.SkipUnless(list.StandardOutput.Contains("10.0.201 [", StringComparison.Ordinal)
            && list.StandardOutput.Contains("10.0.301 [", StringComparison.Ordinal), "The SDK isolation fixture requires installed SDKs 10.0.201 and 10.0.301.");
        await using var first = await PreparationTestArea.CreateAsync();
        await using var second = await PreparationTestArea.CreateAsync();
        await first.WriteAsync("global.json", JsonSerializer.Serialize(new { sdk = new { version = "10.0.201", rollForward = "disable" } }));
        await second.WriteAsync("global.json", JsonSerializer.Serialize(new { sdk = new { version = "10.0.301", rollForward = "disable" } }));

        var results = await Task.WhenAll(
            DotnetSdkResolver.ResolveAsync(first.RootPath, TestContext.Current.CancellationToken),
            DotnetSdkResolver.ResolveAsync(second.RootPath, TestContext.Current.CancellationToken));

        Assert.Equal("10.0.201", results[0].Version);
        Assert.Equal("10.0.301", results[1].Version);
        Assert.NotEqual(results[0].SdkDirectory, results[1].SdkDirectory);
        Assert.True(File.Exists(Path.Combine(results[0].SdkDirectory, "MSBuild.dll")));
        Assert.True(File.Exists(Path.Combine(results[1].SdkDirectory, "MSBuild.dll")));
    }

    [Fact]
    public async Task ResolveAsync_ReportsUnavailablePinnedSdkWithoutInstalling()
    {
        await using var area = await PreparationTestArea.CreateAsync();
        await area.WriteAsync("global.json", JsonSerializer.Serialize(new { sdk = new { version = "99.0.999", rollForward = "disable" } }));

        var exception = await Assert.ThrowsAsync<WorkspacePreparationException>(
            () => DotnetSdkResolver.ResolveAsync(area.RootPath, TestContext.Current.CancellationToken));

        Assert.Contains("No compatible installed .NET SDK", exception.Message, StringComparison.Ordinal);
        Assert.Contains("global.json", exception.Message, StringComparison.Ordinal);
    }
}
