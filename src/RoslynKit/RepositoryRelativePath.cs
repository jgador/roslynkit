namespace RoslynKit;

/// <summary>
/// Represents a canonical physical path stored relative to the current Git worktree.
/// </summary>
internal readonly record struct RepositoryRelativePath
{
    private RepositoryRelativePath(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Gets the slash-separated path value persisted in the search index.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Converts one physical path under the repository root into its canonical persisted form.
    /// </summary>
    public static RepositoryRelativePath FromPhysicalPath(
        string repositoryRoot,
        string? physicalPath,
        string pathDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathDescription);
        if (string.IsNullOrWhiteSpace(physicalPath))
        {
            throw new ArgumentException(
                $"{pathDescription} has no physical path. Search indexing requires repository-local physical paths.",
                nameof(physicalPath));
        }

        var fullRoot = Path.GetFullPath(repositoryRoot);
        var fullPath = Path.GetFullPath(physicalPath);
        if (!File.Exists(fullPath))
        {
            throw new ArgumentException(
                $"{pathDescription} path '{fullPath}' does not exist. Search indexing requires physical repository files.",
                nameof(physicalPath));
        }

        var relativePath = Path.GetRelativePath(fullRoot, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        try
        {
            return FromStoredValue(relativePath, pathDescription);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                $"{pathDescription} path '{fullPath}' is outside repository root '{fullRoot}'. Search indexing requires repository-local paths.",
                nameof(physicalPath),
                exception);
        }
    }

    /// <summary>
    /// Validates one canonical persisted path value.
    /// </summary>
    public static RepositoryRelativePath FromStoredValue(string value, string pathDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathDescription);

        if (Path.IsPathRooted(value)
            || value.Contains('\\')
            || value.Contains(':')
            || value.StartsWith("./", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal)
            || value.Split('/').Any(static segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException(
                $"{pathDescription} must be a non-rooted repository-relative path with '/' separators and no escaping segments.",
                nameof(value));
        }

        return new RepositoryRelativePath(value);
    }

    /// <summary>
    /// Resolves this persisted path against the current repository root for public command output.
    /// </summary>
    public string Resolve(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        return Path.GetFullPath(Value.Replace('/', Path.DirectorySeparatorChar), repositoryRoot);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Value;
    }
}
