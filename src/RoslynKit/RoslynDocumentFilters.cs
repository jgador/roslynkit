using Microsoft.CodeAnalysis;

namespace RoslynKit;

/// <summary>
/// Filters Roslyn documents and symbol locations to the source files RoslynKit should report.
/// </summary>
public static class RoslynDocumentFilters
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static bool IsGenerated(Document document)
    {
        if (document.FilePath is null)
        {
            return true;
        }

        var fullPath = Path.GetFullPath(document.FilePath);
        var fileName = Path.GetFileName(fullPath);

        return fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("obj", PathComparer)
            || fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase);
    }

    public static HashSet<string> GetProjectSourcePaths(Project project)
    {
        return project.Documents
            .Where(document => document.FilePath is not null && !IsGenerated(document))
            .Select(document => Path.GetFullPath(document.FilePath!))
            .ToHashSet(PathComparer);
    }

    public static HashSet<string> GetSolutionSourcePaths(Solution solution)
    {
        return solution.Projects
            .SelectMany(GetProjectSourcePaths)
            .ToHashSet(PathComparer);
    }

    public static bool IsDeclaredInProject(ISymbol symbol, ISet<string> projectSourcePaths)
    {
        return symbol.Locations.Any(location => LocationMatchesAnyPath(location, projectSourcePaths));
    }

    public static bool IsDeclaredInDocument(ISymbol symbol, string? documentPath)
    {
        return documentPath is not null && symbol.Locations.Any(location => LocationMatchesPath(location, documentPath));
    }

    public static bool LocationMatchesPath(Location location, string path)
    {
        return location.IsInSource && PathComparer.Equals(Path.GetFullPath(location.GetLineSpan().Path), Path.GetFullPath(path));
    }

    public static bool LocationMatchesAnyPath(Location location, ISet<string> paths)
    {
        return location.IsInSource && paths.Contains(Path.GetFullPath(location.GetLineSpan().Path));
    }
}
