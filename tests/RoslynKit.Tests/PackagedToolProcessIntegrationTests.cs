using System.Diagnostics;
using System.Security;
using System.Text.RegularExpressions;

namespace RoslynKit.Tests;

/// <summary>
/// Exercises workspace and search commands through a tool installed from the current package output.
/// </summary>
[Collection(DaemonProcessIntegrationCollection.Name)]
public sealed class PackagedToolProcessIntegrationTests
{
    private const string PackageId = "roslynkit";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProcessTerminationTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PackagedTool_WorkspaceAndSearchCommandsUseInstalledPackage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var area = await DaemonProcessTestArea.CreateAsync(cancellationToken);
        var indexPath = Path.Combine(area.RootPath, "artifacts", "packaged-search", "roslynkit.db");
        var packagingPath = Path.Combine(area.RootPath, ".dotnet-cli", "packaged-tool");
        var packageSourcePath = Path.Combine(packagingPath, "packages");
        var toolPath = Path.Combine(packagingPath, "tool");
        var nugetConfigPath = Path.Combine(packagingPath, "NuGet.Config");
        var typeScriptPath = Path.Combine(area.RootPath, "typescript");
        var typeScriptConfigPath = Path.Combine(typeScriptPath, "tsconfig.json");
        var typeScriptSourcePath = Path.Combine(typeScriptPath, "formatter.ts");
        var typeScriptIndexPath = Path.Combine(area.RootPath, "artifacts", "packaged-typescript-search", "roslynkit.db");

        await File.AppendAllTextAsync(
            Path.Combine(area.RootPath, ".gitignore"),
            "artifacts/\n",
            cancellationToken);
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

        Directory.CreateDirectory(typeScriptPath);
        await File.WriteAllTextAsync(
            typeScriptConfigPath,
            """
            {
              "compilerOptions": {
                "module": "nodenext",
                "moduleResolution": "nodenext",
                "noEmit": true,
                "strict": true,
                "target": "es2024"
              },
              "include": ["*.ts"]
            }
            """,
            cancellationToken);
        await File.WriteAllTextAsync(
            typeScriptSourcePath,
            """
            /** Formats a packaged-tool fixture value. */
            export class PackagedTypeScriptFormatter {
              format(value: string): string {
                return value.toUpperCase();
              }
            }

            export const packagedFormatter = new PackagedTypeScriptFormatter();
            """,
            cancellationToken);
        CopyDirectory(
            TestPaths.RepoFile("src", "RoslynKit", "TypeScriptBridge", "node_modules", "@typescript"),
            Path.Combine(area.RootPath, "node_modules", "@typescript"));

        var workspace = await RunProcessAsync(
            executablePath,
            area.RootPath,
            ["workspace", "--target", area.TargetPath],
            cancellationToken);
        workspace.EnsureSuccess("installed roslynkit workspace");
        Assert.StartsWith("command: workspace", workspace.StandardOutput, StringComparison.Ordinal);

        var index = await RunProcessAsync(
            executablePath,
            area.RootPath,
            ["index", "--target", area.TargetPath, "--index-path", indexPath],
            cancellationToken);
        index.EnsureSuccess("installed roslynkit index");
        Assert.StartsWith("command: index", index.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("index-state: fresh", index.StandardOutput, StringComparison.Ordinal);
        Assert.True(File.Exists(indexPath), $"Expected search index at '{indexPath}'.");

        var search = await RunProcessAsync(
            executablePath,
            area.RootPath,
            ["search", "--target", area.TargetPath, "--index-path", indexPath, "--query", "packaged search fixture"],
            cancellationToken);
        search.EnsureSuccess("installed roslynkit search");
        Assert.StartsWith("command: search", search.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("index-state: fresh", search.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("PackagedToolFixture.PackagedSearchFixture", search.StandardOutput, StringComparison.Ordinal);

        var typeScriptWorkspace = await RunProcessAsync(
            executablePath,
            area.RootPath,
            ["workspace", "--target", typeScriptConfigPath],
            cancellationToken);
        typeScriptWorkspace.EnsureSuccess("installed roslynkit TypeScript workspace");
        Assert.Contains("formatter.ts", typeScriptWorkspace.StandardOutput, StringComparison.Ordinal);

        var typeScriptIndex = await RunProcessAsync(
            executablePath,
            area.RootPath,
            ["index", "--target", typeScriptConfigPath, "--index-path", typeScriptIndexPath],
            cancellationToken);
        typeScriptIndex.EnsureSuccess("installed roslynkit TypeScript index");
        Assert.Contains("index-state: fresh", typeScriptIndex.StandardOutput, StringComparison.Ordinal);

        var typeScriptSearch = await RunProcessAsync(
            executablePath,
            area.RootPath,
            ["search", "--target", typeScriptConfigPath, "--index-path", typeScriptIndexPath, "--query", "packaged typescript formatter"],
            cancellationToken);
        typeScriptSearch.EnsureSuccess("installed roslynkit TypeScript search");
        var selector = Regex.Match(
            typeScriptSearch.StandardOutput,
            " id: `(?<selector>ts:[^`]+)`",
            RegexOptions.CultureInvariant).Groups["selector"].Value;
        Assert.StartsWith("ts:", selector, StringComparison.Ordinal);

        var typeScriptSource = await RunProcessAsync(
            executablePath,
            area.RootPath,
            ["symbol-source", "--target", typeScriptConfigPath, "--symbol", selector],
            cancellationToken);
        typeScriptSource.EnsureSuccess("installed roslynkit TypeScript symbol-source");
        Assert.Contains("export class PackagedTypeScriptFormatter", typeScriptSource.StandardOutput, StringComparison.Ordinal);

        var status = await RunProcessAsync(
            executablePath,
            area.RootPath,
            ["daemon", "status", "--target", area.TargetPath],
            cancellationToken);
        var runningStatus = DaemonProcessTestArea.ParseDaemonStatus(status);
        Assert.Equal("running", runningStatus.State);
        Assert.NotNull(runningStatus.ProcessId);

        await area.GetDaemonStatusAsync(cancellationToken);

        var typeScriptStop = await RunProcessAsync(
            executablePath,
            area.RootPath,
            ["daemon", "stop", "--target", typeScriptConfigPath],
            cancellationToken);
        typeScriptStop.EnsureSuccess("installed roslynkit TypeScript daemon stop");

        var stop = await RunProcessAsync(
            executablePath,
            area.RootPath,
            ["daemon", "stop", "--target", area.TargetPath],
            cancellationToken);
        stop.EnsureSuccess("installed roslynkit daemon stop");
        Assert.Contains("state: stopping", stop.StandardOutput, StringComparison.Ordinal);
        await area.WaitForNotRunningStatusAsync(cancellationToken);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, file));
            File.Copy(file, destination, overwrite: true);
        }
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
