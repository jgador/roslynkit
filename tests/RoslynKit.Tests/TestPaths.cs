namespace RoslynKit.Tests;

/// <summary>
/// Provides repository, fixture, source-position, and command-execution helpers shared by RoslynKit tests.
/// </summary>
internal static class TestPaths
{
    /// <summary>
    /// Locates the repository root by walking upward from the test output directory to the solution file.
    /// </summary>
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

    /// <summary>
    /// Returns the absolute path to the RoslynKit solution used by integration-style command tests.
    /// </summary>
    public static string SolutionPath()
    {
        return Path.Combine(RepositoryRoot(), "RoslynKit.slnx");
    }

    /// <summary>
    /// Returns the fixture project path used for semantic command tests against known source shapes.
    /// </summary>
    public static string FixtureProjectPath()
    {
        return Path.Combine(RepositoryRoot(), "tests", "FixtureWorkspace", "App", "App.csproj");
    }

    /// <summary>
    /// Returns the isolated fixture project used for intent-driven semantic-navigation tests.
    /// </summary>
    public static string IntentNavigationProjectPath()
    {
        return Path.Combine(RepositoryRoot(), "tests", "FixtureWorkspace", "IntentNavigation", "IntentNavigation.csproj");
    }

    /// <summary>
    /// Builds an absolute repository path from relative path segments.
    /// </summary>
    public static string RepoFile(params string[] relativeSegments)
    {
        return Path.Combine([RepositoryRoot(), .. relativeSegments]);
    }

    /// <summary>
    /// Finds a one-based source position for a marker in a file, with optional occurrence and column offset controls.
    /// </summary>
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

    /// <summary>
    /// Parses and executes a RoslynKit command in-process, returning the strongly typed command payload.
    /// </summary>
    public static async Task<T> ExecuteCommandAsync<T>(params string[] args)
    {
        var command = CliParser.Parse(args);
        var result = await RoslynCommandExecutor.ExecuteAsync(command, TestContext.Current.CancellationToken);
        return Assert.IsType<T>(result);
    }
}
