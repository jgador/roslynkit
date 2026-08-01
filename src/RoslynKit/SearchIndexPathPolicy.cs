namespace RoslynKit;

/// <summary>
/// Validates the repository-local, Git-ignored storage boundary for a search index.
/// </summary>
internal sealed class SearchIndexPathPolicy
{
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(5);
    private readonly Func<string, string, IReadOnlyList<string>, CancellationToken, Task<ProcessCommandResult>> _runProcessAsync;
    private readonly TimeSpan _deadline;

    public SearchIndexPathPolicy()
        : this(ProcessCommandRunner.RunAsync, DefaultDeadline)
    {
    }

    internal SearchIndexPathPolicy(
        Func<string, string, IReadOnlyList<string>, CancellationToken, Task<ProcessCommandResult>> runProcessAsync,
        TimeSpan deadline)
    {
        ArgumentNullException.ThrowIfNull(runProcessAsync);
        if (deadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline), "The path-validation deadline must be positive.");
        }

        _runProcessAsync = runProcessAsync;
        _deadline = deadline;
    }

    /// <summary>
    /// Resolves an index path from the invoking process and verifies that Git ignores it.
    /// </summary>
    public async Task<SearchIndexPathResolution> ResolveAsync(
        string targetPath,
        string indexPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var deadline = new CancellationTokenSource(_deadline);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);

        try
        {
            var canonicalTarget = ResolveTargetPath(targetPath);
            var targetDirectory = Directory.Exists(canonicalTarget)
                ? canonicalTarget
                : Path.GetDirectoryName(canonicalTarget)!;
            var repositoryRoot = await ResolveRepositoryRootAsync(
                targetDirectory,
                linkedCancellation.Token).ConfigureAwait(false);
            var databasePath = ResolveDatabasePath(indexPath);

            if (Directory.Exists(databasePath))
            {
                return SearchIndexPathResolution.Failed(
                    SearchIndexPathFailureKind.InvalidIndexPath,
                    $"The '--index-path' value '{indexPath}' names a directory. Pass a database file path inside '{repositoryRoot}'.");
            }

            if (!IsInsideRepository(repositoryRoot, databasePath))
            {
                return SearchIndexPathResolution.Failed(
                    SearchIndexPathFailureKind.OutsideRepository,
                    $"The '--index-path' value '{indexPath}' resolves outside the target repository '{repositoryRoot}'. Pass a path inside the repository, such as 'artifacts/roslynkit.db'.");
            }

            var relativeDatabasePath = ToGitPath(Path.GetRelativePath(repositoryRoot, databasePath));
            foreach (var requiredPath in GetRequiredIgnoredPaths(relativeDatabasePath))
            {
                var tracked = await IsTrackedAsync(
                    repositoryRoot,
                    requiredPath,
                    linkedCancellation.Token).ConfigureAwait(false);
                if (tracked.IsTracked)
                {
                    return SearchIndexPathResolution.Failed(
                        SearchIndexPathFailureKind.TrackedPath,
                        $"Git already tracks '{requiredPath}'. Choose a new Git-ignored search database path; RoslynKit will not write index data over tracked files.");
                }

                if (tracked.FailureKind is not null)
                {
                    return SearchIndexPathResolution.Failed(
                        tracked.FailureKind.Value,
                        tracked.Diagnostic!);
                }

                var ignored = await IsIgnoredAsync(
                    repositoryRoot,
                    requiredPath,
                    linkedCancellation.Token).ConfigureAwait(false);
                if (!ignored.IsIgnored)
                {
                    return SearchIndexPathResolution.Failed(
                        ignored.FailureKind ?? SearchIndexPathFailureKind.NotIgnored,
                        ignored.Diagnostic ??
                        $"Git does not ignore '{requiredPath}'. Add an ignore rule for the search database and its SQLite sidecar files before running this command.");
                }
            }

            return SearchIndexPathResolution.Successful(
                new SearchIndexPath(
                    repositoryRoot,
                    canonicalTarget,
                    databasePath,
                    relativeDatabasePath));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return SearchIndexPathResolution.Failed(
                SearchIndexPathFailureKind.TimedOut,
                "Search index path validation exceeded its 5-second deadline. Retry after the repository and Git are responsive.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SearchIndexPathException exception)
        {
            return SearchIndexPathResolution.Failed(exception.FailureKind, exception.Message);
        }
        catch (Exception exception)
        {
            return SearchIndexPathResolution.Failed(
                SearchIndexPathFailureKind.GitFailure,
                $"Search index path validation failed: {exception.Message}");
        }
    }

    private static string ResolveTargetPath(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new SearchIndexPathException(
                SearchIndexPathFailureKind.InvalidTarget,
                "The '--target' value is required to locate the repository for the search index.");
        }

        var fullTarget = Path.GetFullPath(targetPath);
        if (!File.Exists(fullTarget) && !Directory.Exists(fullTarget))
        {
            throw new SearchIndexPathException(
                SearchIndexPathFailureKind.InvalidTarget,
                $"The '--target' path '{targetPath}' does not exist. Pass an existing solution, project, or repository directory.");
        }

        return PathCanonicalizer.ResolveExistingPath(fullTarget);
    }

    private static string ResolveDatabasePath(string indexPath)
    {
        if (string.IsNullOrWhiteSpace(indexPath))
        {
            throw new SearchIndexPathException(
                SearchIndexPathFailureKind.InvalidIndexPath,
                "The '--index-path' value is required. Pass a Git-ignored database file path, such as 'artifacts/roslynkit.db'.");
        }

        var fullDatabasePath = Path.GetFullPath(indexPath);
        return ResolvePathWithExistingAncestor(fullDatabasePath);
    }

    private async Task<string> ResolveRepositoryRootAsync(
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        var result = await _runProcessAsync(
            "git",
            targetDirectory,
            ["-C", targetDirectory, "rev-parse", "--show-toplevel"],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail = result.StandardError.Trim();
            var suffix = detail.Length == 0 ? string.Empty : $": {detail}";
            throw new SearchIndexPathException(
                SearchIndexPathFailureKind.GitFailure,
                $"Could not locate a Git worktree for '--target' '{targetDirectory}' (git exit code {result.ExitCode}){suffix}. Search indexes require a Git repository so their storage can be verified as ignored.");
        }

        var lines = result.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1 || !Directory.Exists(lines[0]))
        {
            throw new SearchIndexPathException(
                SearchIndexPathFailureKind.GitFailure,
                "Git returned an invalid worktree root while validating '--index-path'.");
        }

        return Path.TrimEndingDirectorySeparator(PathCanonicalizer.ResolveExistingPath(lines[0]));
    }

    private async Task<SearchIndexIgnoreCheck> IsIgnoredAsync(
        string repositoryRoot,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var result = await _runProcessAsync(
            "git",
            repositoryRoot,
            ["-C", repositoryRoot, "check-ignore", "--quiet", "--no-index", "--", relativePath],
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 => SearchIndexIgnoreCheck.Ignored(),
            1 => SearchIndexIgnoreCheck.NotIgnored(
                $"Git does not ignore '{relativePath}'. Add an ignore rule for the search database and its SQLite sidecar files before running this command."),
            _ => SearchIndexIgnoreCheck.Failed(
                $"Git could not check whether '{relativePath}' is ignored (exit code {result.ExitCode}){FormatGitError(result.StandardError)}."),
        };
    }

    private async Task<SearchIndexTrackedCheck> IsTrackedAsync(
        string repositoryRoot,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var result = await _runProcessAsync(
            "git",
            repositoryRoot,
            ["-C", repositoryRoot, "ls-files", "--error-unmatch", "--", relativePath],
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 => SearchIndexTrackedCheck.Tracked(),
            1 => SearchIndexTrackedCheck.NotTracked(),
            _ => SearchIndexTrackedCheck.Failed(
                $"Git could not check whether '{relativePath}' is tracked (exit code {result.ExitCode}){FormatGitError(result.StandardError)}."),
        };
    }

    private static IEnumerable<string> GetRequiredIgnoredPaths(string relativeDatabasePath)
    {
        yield return relativeDatabasePath;
        yield return relativeDatabasePath + "-wal";
        yield return relativeDatabasePath + "-shm";
    }

    private static string ResolvePathWithExistingAncestor(string fullPath)
    {
        var missingSegments = new Stack<string>();
        var candidate = fullPath;
        while (!Directory.Exists(candidate) && !File.Exists(candidate))
        {
            var parent = Path.GetDirectoryName(candidate);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, candidate, StringComparison.Ordinal))
            {
                return Path.GetFullPath(fullPath);
            }

            missingSegments.Push(Path.GetFileName(candidate));
            candidate = parent;
        }

        var resolved = PathCanonicalizer.ResolveExistingPath(candidate);
        while (missingSegments.Count > 0)
        {
            resolved = Path.Combine(resolved, missingSegments.Pop());
        }

        return Path.GetFullPath(resolved);
    }

    private static bool IsInsideRepository(string repositoryRoot, string path)
    {
        var relativePath = Path.GetRelativePath(repositoryRoot, path);
        return !Path.IsPathRooted(relativePath)
            && !string.Equals(relativePath, "..", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string ToGitPath(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string FormatGitError(string standardError)
    {
        var detail = standardError.Trim();
        return detail.Length == 0 ? string.Empty : $": {detail}";
    }

    /// <summary>
    /// Describes an expected validation failure without losing its user-facing classification.
    /// </summary>
    private sealed class SearchIndexPathException(
        SearchIndexPathFailureKind failureKind,
        string message) : Exception(message)
    {
        public SearchIndexPathFailureKind FailureKind { get; } = failureKind;
    }
}

/// <summary>
/// Identifies the repository-local SQLite database accepted for a search target.
/// </summary>
internal sealed record SearchIndexPath(
    string RepositoryRoot,
    string TargetPath,
    string DatabasePath,
    string RelativeDatabasePath);

internal enum SearchIndexPathFailureKind
{
    InvalidTarget,
    InvalidIndexPath,
    OutsideRepository,
    NotIgnored,
    TrackedPath,
    GitFailure,
    TimedOut,
}

/// <summary>
/// Carries either a validated search index location or an actionable validation failure.
/// </summary>
internal sealed record SearchIndexPathResolution(
    SearchIndexPath? Path,
    SearchIndexPathFailureKind? FailureKind,
    string? Diagnostic)
{
    public bool IsSuccessful => Path is not null;

    public static SearchIndexPathResolution Successful(SearchIndexPath path)
    {
        return new SearchIndexPathResolution(path, null, null);
    }

    public static SearchIndexPathResolution Failed(
        SearchIndexPathFailureKind failureKind,
        string diagnostic)
    {
        return new SearchIndexPathResolution(null, failureKind, diagnostic);
    }
}

/// <summary>
/// Represents the result of checking one database-related path against Git ignore rules.
/// </summary>
internal sealed record SearchIndexIgnoreCheck(
    bool IsIgnored,
    SearchIndexPathFailureKind? FailureKind,
    string? Diagnostic)
{
    public static SearchIndexIgnoreCheck Ignored()
    {
        return new SearchIndexIgnoreCheck(true, null, null);
    }

    public static SearchIndexIgnoreCheck NotIgnored(string diagnostic)
    {
        return new SearchIndexIgnoreCheck(false, SearchIndexPathFailureKind.NotIgnored, diagnostic);
    }

    public static SearchIndexIgnoreCheck Failed(string diagnostic)
    {
        return new SearchIndexIgnoreCheck(false, SearchIndexPathFailureKind.GitFailure, diagnostic);
    }
}

/// <summary>
/// Represents the result of checking one database-related path against the Git index.
/// </summary>
internal sealed record SearchIndexTrackedCheck(
    bool IsTracked,
    SearchIndexPathFailureKind? FailureKind,
    string? Diagnostic)
{
    public static SearchIndexTrackedCheck Tracked()
    {
        return new SearchIndexTrackedCheck(true, null, null);
    }

    public static SearchIndexTrackedCheck NotTracked()
    {
        return new SearchIndexTrackedCheck(false, null, null);
    }

    public static SearchIndexTrackedCheck Failed(string diagnostic)
    {
        return new SearchIndexTrackedCheck(false, SearchIndexPathFailureKind.GitFailure, diagnostic);
    }
}
