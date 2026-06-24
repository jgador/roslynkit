using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynKit;

public sealed class RoslynWorkspaceLoader : IDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private RoslynWorkspaceLoader(
        MSBuildWorkspace workspace,
        Solution solution,
        string targetPath,
        string targetKind,
        IReadOnlyList<WorkspaceDiagnosticDto> workspaceDiagnostics)
    {
        Workspace = workspace;
        Solution = solution;
        TargetPath = targetPath;
        TargetKind = targetKind;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    public MSBuildWorkspace Workspace { get; }

    public Solution Solution { get; }

    public string TargetPath { get; }

    public string TargetKind { get; }

    public IReadOnlyList<WorkspaceDiagnosticDto> WorkspaceDiagnostics { get; }

    public static async Task<RoslynWorkspaceLoader> LoadAsync(string targetPath, CancellationToken cancellationToken)
    {
        RegisterMSBuild();

        var fullTargetPath = Path.GetFullPath(targetPath);
        if (!File.Exists(fullTargetPath))
        {
            throw new CliUsageException("unknown", $"Target file '{fullTargetPath}' does not exist.");
        }

        var diagnostics = new List<WorkspaceDiagnosticDto>();
        var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
        {
            ["DesignTimeBuild"] = "true",
            ["BuildProjectReferences"] = "false",
            ["SkipCompilerExecution"] = "true",
        });

        workspace.RegisterWorkspaceFailedHandler(args =>
        {
            diagnostics.Add(new WorkspaceDiagnosticDto(args.Diagnostic.Kind.ToString(), args.Diagnostic.Message));
        });

        var extension = Path.GetExtension(fullTargetPath);
        if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var solution = await workspace.OpenSolutionAsync(fullTargetPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new RoslynWorkspaceLoader(workspace, solution, fullTargetPath, extension[1..], diagnostics);
        }

        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var project = await workspace.OpenProjectAsync(fullTargetPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new RoslynWorkspaceLoader(workspace, project.Solution, fullTargetPath, "csproj", diagnostics);
        }

        workspace.Dispose();
        throw new CliUsageException("unknown", "Target must be a .sln, .slnx, or .csproj file.");
    }

    public Document FindDocument(string filePath)
    {
        var fullFilePath = Path.GetFullPath(filePath);
        var matches = Solution.Projects
            .SelectMany(project => project.Documents)
            .Where(document => document.FilePath is not null && PathComparer.Equals(Path.GetFullPath(document.FilePath), fullFilePath))
            .OrderBy(document => document.Project.Name, StringComparer.Ordinal)
            .ThenBy(document => document.Name, StringComparer.Ordinal)
            .ToArray();

        return matches.Length switch
        {
            0 => throw new CliUsageException("unknown", $"File '{fullFilePath}' is not part of the loaded target."),
            1 => matches[0],
            _ => throw new CliUsageException("unknown", $"File '{fullFilePath}' appears in multiple projects. Load a narrower project target."),
        };
    }

    public void Dispose()
    {
        Workspace.Dispose();
    }

    private static void RegisterMSBuild()
    {
        if (MSBuildLocator.IsRegistered)
        {
            return;
        }

        MSBuildLocator.RegisterDefaults();
    }
}
