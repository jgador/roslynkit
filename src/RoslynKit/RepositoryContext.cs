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
