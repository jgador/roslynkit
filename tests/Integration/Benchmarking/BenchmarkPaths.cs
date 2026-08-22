namespace RoslynKit.Benchmarking;

/// <summary>
/// Resolves repository, apphost, and run-root paths without shell commands.
/// </summary>
internal static class BenchmarkPaths
{
    public static string FindRepositoryRoot(string startingDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startingDirectory));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RoslynKit.slnx"))
                && File.Exists(Path.Combine(directory.FullName, "tests", "Integration", "Benchmarking", "cases.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new BenchmarkException("Could not locate the RoslynKit repository root.");
    }

    public static string ResolveAppHost(string repositoryRoot, string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(Path.IsPathRooted(overridePath)
                ? overridePath
                : Path.Combine(repositoryRoot, overridePath));
        }

        var executableName = OperatingSystem.IsWindows() ? "RoslynKit.exe" : "RoslynKit";
        return Path.Combine(repositoryRoot, "artifacts", "bin", "RoslynKit", "release", executableName);
    }

    public static void ValidateAppHost(string path)
    {
        if (!File.Exists(path))
        {
            throw new BenchmarkException($"RoslynKit apphost was not found: '{path}'.");
        }

        var expectedName = OperatingSystem.IsWindows() ? "RoslynKit.exe" : "RoslynKit";
        if (!string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase))
        {
            throw new BenchmarkException($"--roslynkit-path must identify an apphost named '{expectedName}'.");
        }
    }

    public static string ResolveExistingRunRoot(string repositoryRoot, string value)
    {
        var artifactsRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "artifacts"));
        var allowedParent = Path.Combine(artifactsRoot, "benchmark");
        var candidate = Path.GetFullPath(Path.IsPathRooted(value)
            ? value
            : Path.Combine(repositoryRoot, value));
        if (!string.Equals(Directory.GetParent(candidate)?.FullName, allowedParent, PathComparison)
            || !Directory.Exists(candidate))
        {
            throw new BenchmarkException("The run root must identify one existing run below artifacts/benchmark containing run.json.");
        }

        EnsureDirectoryIsNotReparsePoint(artifactsRoot);
        EnsureDirectoryIsNotReparsePoint(allowedParent);
        EnsureDirectoryIsNotReparsePoint(candidate);
        if (!File.Exists(Path.Combine(candidate, BenchmarkRunStore.FileName)))
        {
            throw new BenchmarkException("The run root must identify one existing run below artifacts/benchmark containing run.json.");
        }

        return candidate;
    }

    public static string CreateRunRoot(string repositoryRoot, DateTimeOffset timestamp)
    {
        var artifactsRoot = Path.Combine(repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactsRoot);
        EnsureDirectoryIsNotReparsePoint(artifactsRoot);
        var parent = Path.Combine(artifactsRoot, "benchmark");
        Directory.CreateDirectory(parent);
        EnsureDirectoryIsNotReparsePoint(parent);
        var baseName = timestamp.UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        for (var suffix = 0; ; suffix++)
        {
            var name = suffix == 0 ? baseName : $"{baseName}-{suffix}";
            var candidate = Path.Combine(parent, name);
            if (!Directory.Exists(candidate))
            {
                Directory.CreateDirectory(candidate);
                EnsureDirectoryIsNotReparsePoint(candidate);
                return candidate;
            }

            EnsureDirectoryIsNotReparsePoint(candidate);
        }
    }

    /// <summary>
    /// Creates one run-local artifact directory after rejecting symbolic links and reparse points.
    /// </summary>
    public static void EnsureArtifactDirectory(string runRoot, string directoryName)
    {
        EnsureDirectoryIsNotReparsePoint(runRoot);
        var path = Path.Combine(runRoot, directoryName);
        Directory.CreateDirectory(path);
        EnsureDirectoryIsNotReparsePoint(path);
    }

    private static void EnsureDirectoryIsNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new BenchmarkException("Benchmark run directories must not be symbolic links or reparse points.");
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
