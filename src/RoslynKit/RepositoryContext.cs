namespace RoslynKit;

/// <summary>
/// Identifies the standard Git repository and implicit RoslynKit cache paths for one command.
/// </summary>
internal sealed record RepositoryContext(
    string RootPath,
    string GitDirectoryPath,
    string CacheDirectoryPath,
    string DatabasePath);

/// <summary>
/// Resolves the nearest standard Git repository without accepting linked-worktree or submodule indirection files.
/// </summary>
internal static class RepositoryContextResolver
{
    public static RepositoryContext ResolveExplicitRoot(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Path.IsPathFullyQualified(repositoryRoot))
        {
            throw new RepositoryContextException("The repository root must be an explicit fully qualified directory path.");
        }

        if (!Directory.Exists(repositoryRoot))
        {
            throw new RepositoryContextException($"Repository root '{repositoryRoot}' does not exist or is not a directory.");
        }

        var rootPath = ResolveRootLinks(repositoryRoot);
        var gitPath = Path.Combine(rootPath, ".git");
        if (File.Exists(gitPath))
        {
            throw new RepositoryContextException(
                $"Repository '{rootPath}' uses a .git indirection file. Linked worktrees and submodules are not supported.");
        }

        if (!Directory.Exists(gitPath) || (File.GetAttributes(gitPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new RepositoryContextException($"'{rootPath}' must be the actual Git repository root with an ordinary .git directory.");
        }

        ProcessCommandResult gitResult;
        try
        {
            gitResult = new WorkspaceProcessRunner().RunAsync(
                "git", rootPath, ["rev-parse", "--show-toplevel"], TimeSpan.FromSeconds(10), cancellationToken)
                .GetAwaiter().GetResult();
        }
        catch (WorkspacePreparationException exception)
        {
            throw new RepositoryContextException($"Git is required to validate the repository root. {exception.Message}");
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (gitResult.ExitCode != 0
            || !ResolveRootLinks(gitResult.StandardOutput.TrimEnd('\r', '\n')).Equals(rootPath, comparison))
        {
            throw new RepositoryContextException(
                $"'{rootPath}' is not a valid Git repository root. {DotnetSdkResolver.DescribeFailure(gitResult)}");
        }

        var cachePath = Path.Combine(rootPath, ".roslynkit");
        return new RepositoryContext(rootPath, gitPath, cachePath, Path.Combine(cachePath, "roslynkit.db"));
    }

    private static string ResolveRootLinks(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath)!;
        var resolvedPath = pathRoot;
        foreach (var segment in fullPath[pathRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(resolvedPath, segment));
            resolvedPath = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
        }

        return Path.TrimEndingDirectorySeparator(resolvedPath);
    }

    public static RepositoryContext Resolve(
        string? anchorPath = null,
        string? baseDirectory = null)
    {
        var fullBaseDirectory = Path.GetFullPath(baseDirectory ?? Directory.GetCurrentDirectory());
        var fullAnchorPath = string.IsNullOrWhiteSpace(anchorPath)
            ? fullBaseDirectory
            : Path.GetFullPath(anchorPath, fullBaseDirectory);
        if (!File.Exists(fullAnchorPath) && !Directory.Exists(fullAnchorPath))
        {
            throw new RepositoryContextException(
                $"Repository anchor '{fullAnchorPath}' does not exist.");
        }

        var canonicalAnchor = PathCanonicalizer.ResolveExistingPath(fullAnchorPath);
        var current = new DirectoryInfo(
            Directory.Exists(canonicalAnchor)
                ? canonicalAnchor
                : Path.GetDirectoryName(canonicalAnchor)!);

        while (current is not null)
        {
            var gitPath = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(gitPath))
            {
                var rootPath = Path.TrimEndingDirectorySeparator(
                    PathCanonicalizer.ResolveExistingPath(current.FullName));
                var gitDirectoryPath = PathCanonicalizer.ResolveExistingPath(gitPath);
                var cacheDirectoryPath = Path.Combine(rootPath, ".roslynkit");
                return new RepositoryContext(
                    rootPath,
                    gitDirectoryPath,
                    cacheDirectoryPath,
                    Path.Combine(cacheDirectoryPath, "roslynkit.db"));
            }

            if (File.Exists(gitPath))
            {
                throw new RepositoryContextException(
                    $"Repository '{current.FullName}' uses a .git indirection file. Linked worktrees and submodules are not supported yet.");
            }

            current = current.Parent;
        }

        throw new RepositoryContextException(
            $"Could not locate a standard Git repository from '{canonicalAnchor}'. RoslynKit requires a repository with a .git directory.");
    }
}

/// <summary>
/// Reports an unsupported or missing repository layout during implicit workspace resolution.
/// </summary>
internal sealed class RepositoryContextException(string message) : Exception(message);
