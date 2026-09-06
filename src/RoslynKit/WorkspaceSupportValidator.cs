using Microsoft.CodeAnalysis;

namespace RoslynKit;

/// <summary>
/// Enforces the supported host, language, SDK-style project, and single-framework workspace boundary.
/// </summary>
internal static class WorkspaceSupportValidator
{
    public static void ValidatePlatform()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            throw new WorkspacePreparationException("RoslynKit supports Windows and Linux hosts.");
        }
    }

    internal static void ValidateProject(string projectPath, IReadOnlyDictionary<string, string> properties)
    {
        if (!Path.GetExtension(projectPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspacePreparationException($"Project '{projectPath}' is not a supported C# project.");
        }

        if (!string.IsNullOrWhiteSpace(Get("TargetFrameworks")))
        {
            throw new WorkspacePreparationException(
                $"Project '{projectPath}' declares TargetFrameworks. Multi-target projects are unsupported; declare one TargetFramework.");
        }

        var identifier = Get("TargetFrameworkIdentifier");
        if (identifier.Equals(".NETFramework", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspacePreparationException($"Project '{projectPath}' targets legacy .NET Framework, which is unsupported.");
        }

        if (!Get("UsingMicrosoftNETSdk").Equals("true", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(Get("TargetFramework"))
            || (!identifier.Equals(".NETCoreApp", StringComparison.OrdinalIgnoreCase)
                && !identifier.Equals(".NETStandard", StringComparison.OrdinalIgnoreCase)))
        {
            throw new WorkspacePreparationException(
                $"Project '{projectPath}' must be an SDK-style C# project with one .NET or .NET Standard TargetFramework.");
        }

        string Get(string name) => properties.TryGetValue(name, out var value) ? value : string.Empty;
    }

    public static void ValidateLoaded(Solution solution)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var projectPaths = new HashSet<string>(comparer);
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
            {
                throw new WorkspacePreparationException($"Project '{project.Name}' uses unsupported language '{project.Language}'.");
            }

            if (project.FilePath is not null && !projectPaths.Add(PathCanonicalizer.ResolveExistingPath(project.FilePath)))
            {
                throw new WorkspacePreparationException(
                    $"Project '{project.FilePath}' loaded more than one framework context. Multi-target projects are unsupported.");
            }
        }
    }
}
