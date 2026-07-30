namespace RoslynKit;

/// <summary>
/// Produces absolute path identities while resolving existing symbolic-link and reparse-point segments.
/// </summary>
internal static class PathCanonicalizer
{
    public static string ResolveExistingPath(string path, string? baseDirectory = null)
    {
        var fullPath = baseDirectory is null
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, baseDirectory);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        var resolvedPath = root;
        foreach (var segment in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(resolvedPath, segment);
            FileSystemInfo fileSystemInfo = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : new FileInfo(candidate);
            var linkTarget = fileSystemInfo.ResolveLinkTarget(returnFinalTarget: false);
            resolvedPath = linkTarget is null
                ? candidate
                : Path.GetFullPath(linkTarget.FullName);
        }

        return Path.GetFullPath(resolvedPath);
    }
}
