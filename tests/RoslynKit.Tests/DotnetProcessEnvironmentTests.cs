using System.Diagnostics;
using System.Text.Json;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies that repository children ignore inherited SDK assembly bindings while preserving repository configuration.
/// </summary>
public sealed class DotnetProcessEnvironmentTests
{
    [Fact]
    public void ClearInheritedSdkBindings_PreservesRepositoryAndDotnetHostSettings()
    {
        var startInfo = new ProcessStartInfo("dotnet");
        startInfo.Environment["MSBUILD_EXE_PATH"] = "other-sdk/MSBuild.dll";
        startInfo.Environment["MSBuildExtensionsPath"] = "other-sdk";
        startInfo.Environment["msbuildsdkspath"] = "other-sdk/Sdks";
        startInfo.Environment["DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR"] = "other-sdk/Sdks";
        startInfo.Environment["DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER"] = "10.0.201";
        startInfo.Environment["DOTNET_ROOT"] = "selected-dotnet-host";
        startInfo.Environment["DOTNET_HOST_PATH"] = "selected-dotnet-host/dotnet";
        startInfo.Environment["NUGET_PACKAGES"] = "repository-packages";
        startInfo.Environment["RestoreSources"] = "repository-package-source";
        startInfo.Environment["DirectoryBuildPropsPath"] = "repository.props";
        startInfo.Environment["ROSLYNKIT_DOTNET_SDK_PATH"] = "selected-worker-sdk";

        DotnetProcessEnvironment.ClearInheritedSdkBindings(startInfo);

        Assert.False(startInfo.Environment.ContainsKey("MSBUILD_EXE_PATH"));
        Assert.False(startInfo.Environment.ContainsKey("MSBuildExtensionsPath"));
        Assert.False(startInfo.Environment.ContainsKey("msbuildsdkspath"));
        Assert.False(startInfo.Environment.ContainsKey("DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR"));
        Assert.False(startInfo.Environment.ContainsKey("DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER"));
        Assert.Equal("selected-dotnet-host", startInfo.Environment["DOTNET_ROOT"]);
        Assert.Equal("selected-dotnet-host/dotnet", startInfo.Environment["DOTNET_HOST_PATH"]);
        Assert.Equal("repository-packages", startInfo.Environment["NUGET_PACKAGES"]);
        Assert.Equal("repository-package-source", startInfo.Environment["RestoreSources"]);
        Assert.Equal("repository.props", startInfo.Environment["DirectoryBuildPropsPath"]);
        Assert.Equal("selected-worker-sdk", startInfo.Environment["ROSLYNKIT_DOTNET_SDK_PATH"]);
    }

    [Theory]
    [InlineData("10.0.201", "10.0.301")]
    [InlineData("10.0.301", "10.0.201")]
    public async Task RunAsync_EvaluatesAndRestoresPinnedSdkDespiteInheritedDifferentSdk(string selectedVersion, string inheritedVersion)
    {
        var runner = new WorkspaceProcessRunner();
        var installed = await runner.RunAsync("dotnet", TestPaths.RepositoryRoot(), ["--list-sdks"],
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.SkipUnless(installed.StandardOutput.Contains(selectedVersion + " [", StringComparison.Ordinal)
            && installed.StandardOutput.Contains(inheritedVersion + " [", StringComparison.Ordinal),
            "This SDK binding regression requires installed SDKs 10.0.201 and 10.0.301.");
        await using var selectedRepository = await PreparationTestArea.CreateAsync();
        await using var inheritedRepository = await PreparationTestArea.CreateAsync();
        await selectedRepository.WriteAsync("global.json", JsonSerializer.Serialize(new { sdk = new { version = selectedVersion, rollForward = "disable" } }));
        await inheritedRepository.WriteAsync("global.json", JsonSerializer.Serialize(new { sdk = new { version = inheritedVersion, rollForward = "disable" } }));
        var selected = await DotnetSdkResolver.ResolveAsync(selectedRepository.RootPath, TestContext.Current.CancellationToken);
        var inherited = await DotnetSdkResolver.ResolveAsync(inheritedRepository.RootPath, TestContext.Current.CancellationToken);

        var evaluation = ContaminatedStartInfo("msbuild", selectedRepository.ProjectPath, "-nologo",
            "-getProperty:NETCoreSdkVersion,RoslynKitSdkIsolationMarker");
        var evaluated = await runner.RunAsync(evaluation, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(evaluated.ExitCode == 0, evaluated.StandardOutput + evaluated.StandardError);
        using (var properties = JsonDocument.Parse(evaluated.StandardOutput))
        {
            Assert.Equal(selectedVersion, properties.RootElement.GetProperty("Properties").GetProperty("NETCoreSdkVersion").GetString());
            Assert.Equal("preserved", properties.RootElement.GetProperty("Properties").GetProperty("RoslynKitSdkIsolationMarker").GetString());
        }

        var restore = ContaminatedStartInfo("restore", selectedRepository.ProjectPath,
            "--force", "--nologo", "--verbosity", "quiet", "--disable-build-servers");
        var restored = await runner.RunAsync(restore, TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);

        Assert.True(restored.ExitCode == 0, restored.StandardOutput + restored.StandardError);
        Assert.True(File.Exists(Path.Combine(selectedRepository.RootPath, "obj", "project.assets.json")));

        ProcessStartInfo ContaminatedStartInfo(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo(selected.DotnetPath) { WorkingDirectory = selectedRepository.RootPath };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment["MSBUILD_EXE_PATH"] = Path.Combine(inherited.SdkDirectory, "MSBuild.dll");
            startInfo.Environment["MSBuildExtensionsPath"] = inherited.SdkDirectory;
            startInfo.Environment["MSBuildSDKsPath"] = Path.Combine(inherited.SdkDirectory, "Sdks");
            startInfo.Environment["RoslynKitSdkIsolationMarker"] = "preserved";
            return startInfo;
        }
    }
}
