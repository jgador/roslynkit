using Microsoft.CodeAnalysis;

namespace RoslynKit;

/// <summary>
/// Classifies Roslyn documents and symbol locations so commands report only the intended semantic and workspace-visible files.
/// </summary>
public static class RoslynDocumentFilters
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Maps Roslyn text document runtime types to RoslynKit document-kind names.
    /// </summary>
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

    /// <summary>
    /// Detects generated source documents by Roslyn document type or generated-file path conventions.
    /// </summary>
    public static bool IsGenerated(Document document)
    {
        return document is SourceGeneratedDocument || IsGeneratedSourcePath(document.FilePath);
    }

    /// <summary>
    /// Determines whether a resolved document can participate in semantic C# commands.
    /// </summary>
    public static bool IsSemanticDocument(Document document, string documentKind)
    {
        return document.Project.Language == LanguageNames.CSharp
            && documentKind is DocumentKindNames.Source or DocumentKindNames.SourceGenerated;
    }

    /// <summary>
    /// Applies workspace enumeration flags and repository-root filtering to one Roslyn text document.
    /// </summary>
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

    /// <summary>
    /// Detects generated source paths that should be hidden unless generated documents are requested.
    /// </summary>
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

    /// <summary>
    /// Collects normalized non-generated source paths declared directly by one project.
    /// </summary>
    public static HashSet<string> GetProjectSourcePaths(Project project)
    {
        return project.Documents
            .Where(document => document.FilePath is not null && !IsGenerated(document))
            .Select(document => NormalizePath(document.FilePath!)!)
            .ToHashSet(PathComparer);
    }

    /// <summary>
    /// Collects normalized non-generated source paths declared by every project in a solution.
    /// </summary>
    public static HashSet<string> GetSolutionSourcePaths(Solution solution)
    {
        return solution.Projects
            .SelectMany(GetProjectSourcePaths)
            .ToHashSet(PathComparer);
    }

    /// <summary>
    /// Checks whether any source declaration for a symbol belongs to the supplied project source set.
    /// </summary>
    public static bool IsDeclaredInProject(ISymbol symbol, ISet<string> projectSourcePaths)
    {
        return symbol.Locations.Any(location => LocationMatchesAnyPath(location, projectSourcePaths));
    }

    /// <summary>
    /// Compares one Roslyn source location against a normalized path using platform path casing rules.
    /// </summary>
    public static bool LocationMatchesPath(Location location, string path)
    {
        var locationPath = GetLocationPath(location);
        var comparisonPath = NormalizePath(path);
        return location.IsInSource
            && locationPath is not null
            && comparisonPath is not null
            && PathComparer.Equals(locationPath, comparisonPath);
    }

    /// <summary>
    /// Checks whether one Roslyn source location is contained in a normalized path set.
    /// </summary>
    public static bool LocationMatchesAnyPath(Location location, ISet<string> paths)
    {
        var locationPath = GetLocationPath(location);
        return location.IsInSource
            && locationPath is not null
            && paths.Contains(locationPath);
    }

    /// <summary>
    /// Converts a non-empty path to the absolute path form used for document and symbol comparisons.
    /// </summary>
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
    /// <summary>
    /// Includes source-generated and generated source documents in workspace enumeration.
    /// </summary>
    bool IncludeGenerated,

    /// <summary>
    /// Includes additional documents when workspace output should expose non-source inputs.
    /// </summary>
    bool IncludeAdditional,

    /// <summary>
    /// Includes analyzer config documents such as <c>.editorconfig</c>.
    /// </summary>
    bool IncludeAnalyzerConfig,

    /// <summary>
    /// Restricts workspace output to files rooted under the loaded repository target.
    /// </summary>
    bool RepositoryRelevantOnly);
