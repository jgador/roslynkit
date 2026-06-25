using System.Diagnostics;
using System.Text.Json;

namespace RoslynKit.Tests;

internal static class TestPaths
{
    public static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var solutionPath = Path.Combine(directory.FullName, "RoslynKit.slnx");
            if (File.Exists(solutionPath))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the RoslynKit repository root from the test output directory.");
    }

    public static string SolutionPath()
    {
        return Path.Combine(RepositoryRoot(), "RoslynKit.slnx");
    }

    public static string FixtureProjectPath()
    {
        return Path.Combine(RepositoryRoot(), "tests", "FixtureWorkspace", "App", "App.csproj");
    }

    public static string WrapperScriptPath()
    {
        return Path.Combine(RepositoryRoot(), ".agents", "skills", "roslynkit-csharp", "scripts", "invoke-roslynkit-csharp.ps1");
    }

    public static string RepoFile(params string[] relativeSegments)
    {
        return Path.Combine([RepositoryRoot(), .. relativeSegments]);
    }

    public static (int line, int column) FindLineAndColumn(string filePath, string marker, int occurrence = 1, int columnOffset = 0)
    {
        var content = File.ReadAllText(filePath);
        var index = -1;
        for (var matchIndex = 0; matchIndex < occurrence; matchIndex++)
        {
            index = content.IndexOf(marker, index + 1, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new InvalidOperationException($"Marker '{marker}' (occurrence {occurrence}) was not found in '{filePath}'.");
            }
        }

        var line = 1;
        var lineStartIndex = 0;
        for (var cursor = 0; cursor < index; cursor++)
        {
            if (content[cursor] == '\n')
            {
                line++;
                lineStartIndex = cursor + 1;
            }
        }

        var column = index - lineStartIndex + 1 + columnOffset;
        return (line, column);
    }

    public static async Task<T> ExecuteCommandAsync<T>(params string[] args)
    {
        var command = CliParser.Parse(args);
        var result = await RoslynCommandExecutor.ExecuteAsync(command, TestContext.Current.CancellationToken);
        return Assert.IsType<T>(result);
    }

    public static async Task<JsonDocument> ExecuteWrapperEnvelopeAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add(WrapperScriptPath());

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start pwsh for wrapper execution.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"Wrapper execution failed with exit code {process.ExitCode}.{Environment.NewLine}STDOUT:{Environment.NewLine}{stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{stderr}");
        }

        return JsonDocument.Parse(stdout);
    }
}
