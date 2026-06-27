using Microsoft.CodeAnalysis;

namespace RoslynKit;

/// <summary>
/// Classifies Roslyn documents and symbol locations so commands report only the intended semantic and workspace-visible files.
/// </summary>
public static class RoslynDocumentFilters
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string GetDocumentKind(TextDocument document)
    {
        return document switch
        {
            SourceGeneratedDocument => DocumentKindNames.SourceGenerated,
            AnalyzerConfigDocument => DocumentKindNames.AnalyzerConfig,
            AdditionalDocument => DocumentKindNames.Additional,
            Document => DocumentKindNames.Source,
            _ => throw new InvalidOperationException($"Unsupported Roslyn text document type '{document.GetType().FullName}'."),
        };
    }

    public static bool IsGenerated(Document document)
    {
        return document is SourceGeneratedDocument || IsGeneratedSourcePath(document.FilePath);
    }

    public static bool IsSemanticDocument(Document document, string documentKind)
    {
        return document.Project.Language == LanguageNames.CSharp
            && documentKind is DocumentKindNames.Source or DocumentKindNames.SourceGenerated;
    }

    public static bool ShouldIncludeWorkspaceDocument(TextDocument document, string documentKind, string rootPath, DocumentEnumerationOptions options)
    {
        return documentKind switch
        {
            DocumentKindNames.Source => ShouldIncludeSourceDocument(document, rootPath, options),
            DocumentKindNames.SourceGenerated => options.IncludeGenerated,
            DocumentKindNames.Additional => options.IncludeAdditional && IsIncludedByRoot(document.FilePath, rootPath, options.RepositoryRelevantOnly),
            DocumentKindNames.AnalyzerConfig => options.IncludeAnalyzerConfig && IsIncludedByRoot(document.FilePath, rootPath, options.RepositoryRelevantOnly),
            _ => false,
        };
    }

    public static bool IsGeneratedSourcePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var fullPath = NormalizePath(filePath);
        if (fullPath is null)
        {
            return false;
        }

        var fileName = Path.GetFileName(fullPath);
        return ContainsPathSegment(fullPath, "obj")
            || fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase);
    }

    public static HashSet<string> GetProjectSourcePaths(Project project)
    {
        return project.Documents
            .Where(document => document.FilePath is not null && !IsGenerated(document))
            .Select(document => NormalizePath(document.FilePath!)!)
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

    public static bool LocationMatchesPath(Location location, string path)
    {
        var locationPath = GetLocationPath(location);
        var comparisonPath = NormalizePath(path);
        return location.IsInSource
            && locationPath is not null
            && comparisonPath is not null
            && PathComparer.Equals(locationPath, comparisonPath);
    }

    public static bool LocationMatchesAnyPath(Location location, ISet<string> paths)
    {
        var locationPath = GetLocationPath(location);
        return location.IsInSource
            && locationPath is not null
            && paths.Contains(locationPath);
    }

    public static string? NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path);
    }

    private static bool ShouldIncludeSourceDocument(TextDocument document, string rootPath, DocumentEnumerationOptions options)
    {
        if (!IsIncludedByRoot(document.FilePath, rootPath, options.RepositoryRelevantOnly))
        {
            return false;
        }

        return options.IncludeGenerated || !IsGeneratedSourcePath(document.FilePath);
    }

    private static bool IsIncludedByRoot(string? filePath, string rootPath, bool repositoryRelevantOnly)
    {
        if (!repositoryRelevantOnly)
        {
            return true;
        }

        var normalizedPath = NormalizePath(filePath);
        if (normalizedPath is null)
        {
            return false;
        }

        if (IsPackageCachePath(normalizedPath))
        {
            return false;
        }

        var normalizedRoot = NormalizePath(rootPath);
        if (normalizedRoot is null)
        {
            return false;
        }

        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return PathComparer.Equals(normalizedPath, normalizedRoot)
            || normalizedPath.StartsWith(rootWithSeparator, comparison);
    }

    private static bool IsPackageCachePath(string fullPath)
    {
        return ContainsPathSegment(fullPath, ".nuget")
            || ContainsPathSegment(fullPath, "NuGetFallbackFolder")
            || ContainsPathSegment(fullPath, "packs");
    }

    private static bool ContainsPathSegment(string fullPath, string segment)
    {
        return fullPath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(segment, PathComparer);
    }

    private static string? GetLocationPath(Location location)
    {
        var spanPath = location.GetLineSpan().Path;
        return NormalizePath(!string.IsNullOrWhiteSpace(spanPath) ? spanPath : location.SourceTree?.FilePath);
    }
}

/// <summary>
/// Controls which workspace document kinds RoslynKit enumerates.
/// </summary>
public readonly record struct DocumentEnumerationOptions(
    bool IncludeGenerated,
    bool IncludeAdditional,
    bool IncludeAnalyzerConfig,
    bool RepositoryRelevantOnly);
