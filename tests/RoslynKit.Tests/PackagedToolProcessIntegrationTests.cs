using System.Diagnostics;
using System.Security;

namespace RoslynKit.Tests;

/// <summary>
/// Exercises workspace and search commands through a tool installed from the current package output.
/// </summary>
public sealed class PackagedToolProcessIntegrationTests
{
    private const string PackageId = "roslynkit";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProcessTerminationTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PackagedTool_WorkspaceAndSearchCommandsUseInstalledPackage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var area = await RepositoryProcessTestArea.CreateAsync(cancellationToken);
        var packagingPath = Path.Combine(area.RootPath, ".dotnet-cli", "packaged-tool");
        var packageSourcePath = Path.Combine(packagingPath, "packages");
        var toolPath = Path.Combine(packagingPath, "tool");
        var nugetConfigPath = Path.Combine(packagingPath, "NuGet.Config");

        await area.WriteSourceAsync(
            """
            namespace PackagedToolFixture;

            /// <summary>
            /// Identifies the declaration indexed by the packaged search smoke test.
            /// </summary>
            public sealed class PackagedSearchFixture
            {
            }
            """,
            cancellationToken);

        Directory.CreateDirectory(packagingPath);
        Directory.CreateDirectory(packageSourcePath);
        var pack = await RunProcessAsync(
            "dotnet",
            area.RootPath,
            [
                "pack",
                TestPaths.RepoFile("src", "RoslynKit", "RoslynKit.csproj"),
                "--configuration",
                BuildConfiguration,
                "--no-build",
                "--no-restore",
                "--nologo",
                "--output",
                packageSourcePath,
            ],
            cancellationToken);
        pack.EnsureSuccess("dotnet pack --no-build");

        var packagePath = Directory.EnumerateFiles(packageSourcePath, $"{PackageId}.*.nupkg")
            .Single();
        var packageVersion = Path.GetFileNameWithoutExtension(packagePath)[(PackageId.Length + 1)..];
        await File.WriteAllTextAsync(
            nugetConfigPath,
            $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{{SecurityElement.Escape(packageSourcePath)}}" />
              </packageSources>
            </configuration>
            """,
            cancellationToken);

        var install = await RunProcessAsync(
            "dotnet",
            area.RootPath,
            [
                "tool",
                "install",
                PackageId,
                "--tool-path",
                toolPath,
                "--configfile",
                nugetConfigPath,
                "--version",
                packageVersion,
                "--ignore-failed-sources",
            ],
            cancellationToken);
        install.EnsureSuccess("dotnet tool install roslynkit");

        var executablePath = Path.Combine(
            toolPath,
            OperatingSystem.IsWindows() ? "roslynkit.exe" : "roslynkit");
        Assert.True(File.Exists(executablePath), $"Expected installed tool at '{executablePath}'.");

        var workspace = await RunProcessAsync(
            executablePath,
            area.RootPath,
            ["workspace"],
            cancellationToken);
        workspace.EnsureSuccess("installed roslynkit workspace");
        Assert.StartsWith("command: workspace", workspace.StandardOutput, StringComparison.Ordinal);

        var index = await RunProcessAsync(
            executablePath,
            area.RootPath,
            ["index"],
            cancellationToken);
        index.EnsureSuccess("installed roslynkit index");
        Assert.StartsWith("command: index", index.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("index-state: fresh", index.StandardOutput, StringComparison.Ordinal);
        var indexPath = Path.Combine(area.RootPath, ".roslynkit", "roslynkit.db");
        Assert.True(File.Exists(indexPath), $"Expected search index at '{indexPath}'.");

        var search = await RunProcessAsync(
            executablePath,
            area.RootPath,
            ["search", "--query", "packaged search fixture"],
            cancellationToken);
        search.EnsureSuccess("installed roslynkit search");
        Assert.StartsWith("command: search", search.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("index-state: fresh", search.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("PackagedToolFixture.PackagedSearchFixture", search.StandardOutput, StringComparison.Ordinal);

        var definition = await RunProcessAsync(
            executablePath,
            area.RootPath,
            ["definition", "--symbol", "T:PackagedToolFixture.PackagedSearchFixture"],
            cancellationToken);
        definition.EnsureSuccess("installed roslynkit definition");
        Assert.Contains(
            "name: `PackagedToolFixture.PackagedSearchFixture`",
            definition.StandardOutput,
            StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(fileName, workingDirectory, arguments),
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start process '{fileName}'.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessAsync(process);
            throw;
        }

        await Task.WhenAll(standardOutput, standardError).WaitAsync(ProcessTerminationTimeout);
        return new ProcessResult(process.ExitCode, standardOutput.Result, standardError.Result);
    }

    private static ProcessStartInfo CreateStartInfo(
        string fileName,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_CLI_HOME"] = Path.Combine(workingDirectory, ".dotnet-cli");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        return startInfo;
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(ProcessTerminationTimeout);
        }
        catch (InvalidOperationException)
        {
            // The process exited while cancellation was being handled.
        }
    }

    private static string BuildConfiguration
    {
        get
        {
#if DEBUG
            return "Debug";
#else
            return "Release";
#endif
        }
    }
}
