using System.Diagnostics;

namespace RoslynKit.Tests;

/// <summary>
/// Creates an isolated standard Git repository for process integration tests.
/// </summary>
internal sealed class RepositoryProcessTestArea : IAsyncDisposable
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProcessTerminationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(200);
    private bool _disposed;

    private RepositoryProcessTestArea(string rootPath, string targetPath, string sourcePath)
    {
        RootPath = rootPath;
        TargetPath = targetPath;
        SourcePath = sourcePath;
    }

    public string RootPath { get; }

    public string TargetPath { get; }

    public string SourcePath { get; }

    public static async Task<RepositoryProcessTestArea> CreateAsync(CancellationToken cancellationToken)
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "roslynkit-tests",
            "repository-process",
            Guid.NewGuid().ToString("N"));
        var projectDirectory = Path.Combine(rootPath, "src");
        var targetPath = Path.Combine(projectDirectory, "App.csproj");
        var sourcePath = Path.Combine(projectDirectory, "Program.cs");
        var area = new RepositoryProcessTestArea(rootPath, targetPath, sourcePath);

        try
        {
            Directory.CreateDirectory(projectDirectory);
            File.Copy(TestPaths.RepoFile("global.json"), Path.Combine(rootPath, "global.json"));
            await File.WriteAllTextAsync(
                targetPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """,
                cancellationToken);
            await area.WriteSourceAsync(
                "Console.WriteLine(\"Repository process test fixture\");\n",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(rootPath, ".gitignore"),
                "bin/\nobj/\n.dotnet-cli/\n.roslynkit/\n",
                cancellationToken);

            await area.RunGitAsync(cancellationToken, "init");
            await area.RunGitAsync(cancellationToken, "config", "user.name", "RoslynKit Tests");
            await area.RunGitAsync(cancellationToken, "config", "user.email", "roslynkit-tests@example.invalid");
            await area.RunGitAsync(cancellationToken, "config", "core.autocrlf", "false");
            await area.RunGitAsync(cancellationToken, "add", ".");
            await area.RunGitAsync(cancellationToken, "commit", "-m", "Initial commit");
            return area;
        }
        catch
        {
            await area.DisposeAsync();
            throw;
        }
    }

    public Task WriteSourceAsync(string source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return File.WriteAllTextAsync(SourcePath, source, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!Directory.Exists(RootPath))
            {
                return;
            }

            try
            {
                ResetAttributes(RootPath);
                Directory.Delete(RootPath, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(DeleteRetryDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                await Task.Delay(DeleteRetryDelay);
            }
        }
    }

    private async Task RunGitAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(arguments),
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start process 'git'.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(ProcessTerminationTimeout);
            throw;
        }

        await Task.WhenAll(standardOutput, standardError).WaitAsync(ProcessTerminationTimeout);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.{Environment.NewLine}"
                + $"stdout: {standardOutput.Result}{Environment.NewLine}"
                + $"stderr: {standardError.Result}");
        }
    }

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = RootPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void ResetAttributes(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(entry, FileAttributes.Normal);
        }

        File.SetAttributes(path, FileAttributes.Normal);
    }
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public void EnsureSuccess(string command)
    {
        if (ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{command} failed with exit code {ExitCode}.{Environment.NewLine}"
                + $"stdout: {StandardOutput}{Environment.NewLine}"
                + $"stderr: {StandardError}");
        }
    }
}
