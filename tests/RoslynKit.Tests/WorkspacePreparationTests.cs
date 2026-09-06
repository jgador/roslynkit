using System.Text.Json;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies dependency preparation independently of Roslyn workspace materialization.
/// </summary>
public sealed class WorkspacePreparationTests
{
    [Fact]
    public async Task PrepareAsync_RestoresMissingCustomAssetsOnceAndIgnoresSourceEdits()
    {
        await using var area = await PreparationTestArea.CreateAsync();
        var runner = new PreparationProcessDouble(area);
        var preparation = new WorkspacePreparationService(runner);

        var first = await preparation.PrepareAsync(area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken);
        await area.WriteAsync("Program.cs", "class ChangedSource { }\n");
        var second = await preparation.PrepareAsync(area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken);

        Assert.True(first.Restored);
        Assert.False(second.Restored);
        Assert.Equal(1, runner.RestoreCount);
        Assert.Equal(area.AssetsPath, Assert.Single(first.AssetsPaths));
        Assert.Contains(area.ImportPath, first.BuildInputPaths);
    }

    [Fact]
    public async Task PrepareAsync_RestoresWhenEvaluatedImportOrNuGetConfigChanges()
    {
        await using var area = await PreparationTestArea.CreateAsync();
        var runner = new PreparationProcessDouble(area);
        var preparation = new WorkspacePreparationService(runner);
        await preparation.PrepareAsync(area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken);

        await area.WriteAsync("Shared.props", "<Project><PropertyGroup><DefineConstants>CHANGED</DefineConstants></PropertyGroup></Project>");
        File.SetLastWriteTimeUtc(area.ImportPath, DateTime.UtcNow.AddDays(-2));
        await preparation.PrepareAsync(area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken);
        await area.WriteAsync("NuGet.Config", "<configuration><packageSources><clear /></packageSources></configuration>\n");
        await preparation.PrepareAsync(area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken);

        Assert.Equal(3, runner.RestoreCount);
    }

    [Fact]
    public async Task PrepareAsync_DoesNotRestoreForNewerGeneratedNuGetImports()
    {
        await using var area = await PreparationTestArea.CreateAsync();
        var runner = new PreparationProcessDouble(area);
        var generatedImport = await area.WriteAsync("obj/App.csproj.nuget.g.props", "<Project />");
        runner.Imports.Add(generatedImport);
        await area.WriteAssetsAsync();
        File.SetLastWriteTimeUtc(generatedImport, DateTime.UtcNow.AddMinutes(5));

        var result = await new WorkspacePreparationService(runner).PrepareAsync(
            area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken);

        Assert.False(result.Restored);
        Assert.Equal(0, runner.RestoreCount);
        Assert.Contains(generatedImport, result.BuildInputPaths);
    }

    [Fact]
    public async Task PrepareAsync_DoesNotRepeatSuccessfulRestoreForFutureInputTimestamps()
    {
        await using var area = await PreparationTestArea.CreateAsync();
        File.SetLastWriteTimeUtc(area.ImportPath, DateTime.UtcNow.AddDays(1));
        var runner = new PreparationProcessDouble(area);
        var preparation = new WorkspacePreparationService(runner);

        await preparation.PrepareAsync(area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken);
        var second = await preparation.PrepareAsync(area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken);

        Assert.False(second.Restored);
        Assert.Equal(1, runner.RestoreCount);
    }

    [Fact]
    public async Task PrepareAsync_RetainsRestoreFailureUntilInputsChange()
    {
        await using var area = await PreparationTestArea.CreateAsync();
        var runner = new PreparationProcessDouble(area) { RestoreExitCode = 1 };
        var preparation = new WorkspacePreparationService(runner);

        var first = await Assert.ThrowsAsync<WorkspacePreparationException>(
            () => preparation.PrepareAsync(area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken));
        var second = await Assert.ThrowsAsync<WorkspacePreparationException>(
            () => preparation.PrepareAsync(area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken));

        Assert.Contains("Dependency restore failed", first.Message, StringComparison.Ordinal);
        Assert.Contains("previous restore failed", second.Message, StringComparison.Ordinal);
        Assert.Equal(1, runner.RestoreCount);

        await area.WriteAsync("Shared.props", "<Project><!--changed dependency input--></Project>");
        runner.RestoreExitCode = 0;
        await preparation.PrepareAsync(area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken);
        Assert.Equal(2, runner.RestoreCount);
    }

    [Fact]
    public async Task PrepareAsync_DoesNotRetryFailedRestoreWhenThatAttemptIntroducesImports()
    {
        await using var area = await PreparationTestArea.CreateAsync();
        var runner = new PreparationProcessDouble(area) { RestoreExitCode = 1 };
        runner.OnRestore = async () =>
        {
            var addedImport = await area.WriteAsync("PartialRestore.props", "<Project />");
            runner.Imports.Add(addedImport);
        };
        var preparation = new WorkspacePreparationService(runner);

        await Assert.ThrowsAsync<WorkspacePreparationException>(
            () => preparation.PrepareAsync(area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WorkspacePreparationException>(
            () => preparation.PrepareAsync(area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken));

        Assert.Equal(1, runner.RestoreCount);
    }

    [Fact]
    public async Task PrepareAsync_RecognizesAssetsUsingNormalizedPlatformFrameworkName()
    {
        await using var area = await PreparationTestArea.CreateAsync();
        var runner = new PreparationProcessDouble(area) { TargetFramework = "net10.0-windows" };
        await area.WriteAsync("custom/dependency-assets.json", """
            { "project": { "frameworks": { "net10.0-windows7.0": { "targetAlias": "net10.0-windows" } } } }
            """);

        var result = await new WorkspacePreparationService(runner).PrepareAsync(
            area.RootPath, area.ProjectPath, false, TestContext.Current.CancellationToken);

        Assert.False(result.Restored);
        Assert.Equal(0, runner.RestoreCount);
    }

    [Fact]
    public async Task PrepareAsync_DisabledRestoreReportsMissingAssetsWithoutStartingRestore()
    {
        await using var area = await PreparationTestArea.CreateAsync();
        var runner = new PreparationProcessDouble(area);

        var exception = await Assert.ThrowsAsync<WorkspacePreparationException>(
            () => new WorkspacePreparationService(runner).PrepareAsync(
                area.RootPath, area.ProjectPath, false, TestContext.Current.CancellationToken));

        Assert.Contains(area.AssetsPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("automatic restore is disabled", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner.RestoreCount);
    }

    [Fact]
    public async Task PrepareAsync_DisabledRestoreAllowsPresentStaleAssets()
    {
        await using var area = await PreparationTestArea.CreateAsync();
        var runner = new PreparationProcessDouble(area);
        await area.WriteAssetsAsync();
        File.SetLastWriteTimeUtc(area.AssetsPath, DateTime.UtcNow.AddDays(-2));

        var result = await new WorkspacePreparationService(runner).PrepareAsync(
            area.RootPath, area.ProjectPath, false, TestContext.Current.CancellationToken);

        Assert.False(result.Restored);
        Assert.Equal(0, runner.RestoreCount);
    }

    [Theory]
    [InlineData("net10.0;net9.0", ".NETCoreApp", "TargetFrameworks")]
    [InlineData("net10.0", ".NETCoreApp", "TargetFrameworks")]
    [InlineData("", ".NETFramework", "legacy .NET Framework")]
    public async Task PrepareAsync_RejectsUnsupportedEvaluatedFrameworkBeforeRestore(string targetFrameworks, string identifier, string message)
    {
        await using var area = await PreparationTestArea.CreateAsync();
        var runner = new PreparationProcessDouble(area) { TargetFrameworks = targetFrameworks, FrameworkIdentifier = identifier };

        var exception = await Assert.ThrowsAsync<WorkspacePreparationException>(
            () => new WorkspacePreparationService(runner).PrepareAsync(
                area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken));

        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner.RestoreCount);
    }

    [Fact]
    public async Task PrepareAsync_AcceptsSdkStyleNetStandardLibrary()
    {
        await using var area = await PreparationTestArea.CreateAsync();
        var runner = new PreparationProcessDouble(area) { TargetFramework = "netstandard2.1", FrameworkIdentifier = ".NETStandard" };

        var result = await new WorkspacePreparationService(runner).PrepareAsync(
            area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken);

        Assert.True(result.Restored);
    }

    [Fact]
    public async Task PrepareAsync_DetectsImportedMultiTargetSettingUsingInstalledSdk()
    {
        await using var area = await PreparationTestArea.CreateAsync();
        await area.WriteAsync("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"Shared.props\" /></Project>");
        await area.WriteAsync("Shared.props", "<Project><PropertyGroup><TargetFrameworks>net10.0;net9.0</TargetFrameworks></PropertyGroup></Project>");

        var exception = await Assert.ThrowsAsync<WorkspacePreparationException>(
            () => new WorkspacePreparationService(new WorkspaceProcessRunner()).PrepareAsync(
                area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken));

        Assert.Contains("TargetFrameworks", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(area.AssetsPath));
    }

    [Fact]
    public async Task PrepareAsync_TracksRealImportedPropsAndRestoresCustomAssetsPath()
    {
        await using var area = await PreparationTestArea.CreateAsync();
        await area.WriteAsync("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"Shared.props\" /></Project>");
        await area.WriteAsync("Directory.Build.props", "<Project><PropertyGroup><BaseIntermediateOutputPath>custom/</BaseIntermediateOutputPath></PropertyGroup></Project>");
        await area.WriteAsync("Shared.props", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><ProjectAssetsFile>custom/project.assets.json</ProjectAssetsFile></PropertyGroup></Project>");
        var runner = new RecordingProcessRunner();
        var preparation = new WorkspacePreparationService(runner);

        var first = await preparation.PrepareAsync(area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken);
        await area.WriteAsync("Example.cs", "public class Example { }\n");
        var second = await preparation.PrepareAsync(area.RootPath, area.ProjectPath, true, TestContext.Current.CancellationToken);

        Assert.True(first.Restored);
        Assert.False(second.Restored);
        Assert.Contains(area.ImportPath, first.BuildInputPaths);
        Assert.Equal(Path.Combine(area.RootPath, "custom", "project.assets.json"), Assert.Single(first.AssetsPaths));
        Assert.Equal(1, runner.RestoreCount);
    }

    /// <summary>
    /// Provides SDK evaluation and restoration responses while recording only the requested subprocess work.
    /// </summary>
    private sealed class PreparationProcessDouble(PreparationTestArea area) : IWorkspaceProcessRunner
    {
        public string TargetFramework { get; set; } = "net10.0";
        public string TargetFrameworks { get; set; } = string.Empty;
        public string FrameworkIdentifier { get; set; } = ".NETCoreApp";
        public int RestoreExitCode { get; set; }
        public int RestoreCount { get; private set; }
        public Func<Task>? OnRestore { get; set; }
        public List<string> Imports { get; } = [area.ImportPath];

        public async Task<ProcessCommandResult> RunAsync(string fileName, string workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Assert.Equal(area.RootPath, workingDirectory);
            if (arguments[0] == "--version")
            {
                return new(0, "10.0.301\n", string.Empty);
            }

            if (arguments[0] == "--list-sdks")
            {
                return new(0, $"10.0.301 [{area.SdkRoot}]\n", string.Empty);
            }

            if (arguments[0] == "restore")
            {
                RestoreCount++;
                if (OnRestore is not null)
                {
                    await OnRestore();
                }

                if (RestoreExitCode == 0)
                {
                    await area.WriteAssetsAsync(TargetFramework);
                }

                return new(RestoreExitCode, string.Empty, RestoreExitCode == 0 ? string.Empty : "NU1301: Test package source is unavailable.");
            }

            Assert.Equal("msbuild", arguments[0]);
            if (arguments.Contains("-preprocess"))
            {
                var comments = Imports.Select(path => $"<!--\n====\n{path}\n====\n-->");
                return new(0, "<Project>" + string.Join('\n', comments) + "</Project>", string.Empty);
            }

            return new(0, JsonSerializer.Serialize(new
            {
                Properties = new
                {
                    TargetFramework,
                    TargetFrameworks,
                    TargetFrameworkIdentifier = FrameworkIdentifier,
                    UsingMicrosoftNETSdk = "true",
                    ProjectAssetsFile = area.AssetsPath,
                    MSBuildAllProjects = area.ProjectPath,
                    MSBuildProjectExtensionsPath = Path.Combine(area.RootPath, "obj"),
                    RestoreConfigFile = string.Empty,
                },
                Items = new { ProjectReference = Array.Empty<object>() },
            }), string.Empty);
        }
    }

    /// <summary>
    /// Counts actual restores while retaining production process execution and cancellation behavior.
    /// </summary>
    private sealed class RecordingProcessRunner : IWorkspaceProcessRunner
    {
        private readonly WorkspaceProcessRunner _inner = new();
        public int RestoreCount { get; private set; }

        public Task<ProcessCommandResult> RunAsync(string fileName, string workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (arguments[0] == "restore")
            {
                RestoreCount++;
            }

            return _inner.RunAsync(fileName, workingDirectory, arguments, timeout, cancellationToken);
        }
    }
}

/// <summary>
/// Creates bounded Git and dependency fixtures under the ignored preparation test artifacts directory.
/// </summary>
internal sealed class PreparationTestArea : IAsyncDisposable
{
    private PreparationTestArea(string rootPath)
    {
        RootPath = rootPath;
    }

    public string RootPath { get; }
    public string ProjectPath => Path.Combine(RootPath, "App.csproj");
    public string ImportPath => Path.Combine(RootPath, "Shared.props");
    public string AssetsPath => Path.Combine(RootPath, "custom", "dependency-assets.json");
    public string SdkRoot => Path.Combine(RootPath, "fake-sdk");

    public static async Task<PreparationTestArea> CreateAsync()
    {
        var root = TestPaths.RepoFile("artifacts", "workspace-preparation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var area = new PreparationTestArea(root);
        try
        {
            var git = await new WorkspaceProcessRunner().RunAsync("git", root, ["init", "--quiet"], TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(0, git.ExitCode);
            await area.WriteAsync("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await area.WriteAsync("Directory.Build.props", "<Project />");
            await area.WriteAsync("Directory.Build.targets", "<Project />");
            await area.WriteAsync("NuGet.Config", "<configuration><packageSources><clear /></packageSources></configuration>");
            await area.WriteAsync("Shared.props", "<Project />");
            await area.WriteAsync("fake-sdk/10.0.301/MSBuild.dll", "test SDK marker");
            return area;
        }
        catch
        {
            await area.DisposeAsync();
            throw;
        }
    }

    public async Task<string> WriteAsync(string relativePath, string contents)
    {
        var path = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents, TestContext.Current.CancellationToken);
        return path;
    }

    public Task WriteAssetsAsync(string targetFramework = "net10.0")
    {
        var contents = JsonSerializer.Serialize(new
        {
            project = new { frameworks = new Dictionary<string, object> { [targetFramework] = new { } } },
        });
        return WriteAsync("custom/dependency-assets.json", contents);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(RootPath))
        {
            foreach (var path in Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(RootPath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
