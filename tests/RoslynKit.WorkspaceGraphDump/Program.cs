using Microsoft.CodeAnalysis;
using RoslynKit;

namespace RoslynKit.WorkspaceGraphDump;

/// <summary>
/// Dumps the loaded Roslyn workspace project graph and each project's documents.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Loads the requested target and writes workspace diagnostics plus the project dependency graph.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var targetPath = ResolveTargetPath(args);

            using var loaded = await RoslynWorkspaceLoader.LoadAsync(targetPath, CancellationToken.None).ConfigureAwait(false);

            WriteDiagnostics(loaded.WorkspaceDiagnostics);
            WriteProjectGraph(loaded);

            return 0;
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Usage: dotnet run --project ./tests/RoslynKit.WorkspaceGraphDump [<solution.slnx|solution.sln|project.csproj>]");
            return 1;
        }
    }

    private static string ResolveTargetPath(IReadOnlyList<string> args)
    {
        return args.Count switch
        {
            0 => FindDefaultSolutionPath(),
            1 => Path.GetFullPath(args[0]),
            _ => throw new CliUsageException("unknown", "Expected zero or one positional argument."),
        };
    }

    private static string FindDefaultSolutionPath()
    {
        const string defaultSolutionName = "RoslynKit.slnx";

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidatePath = Path.Combine(directory.FullName, defaultSolutionName);
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        throw new CliUsageException("unknown", $"Could not locate '{defaultSolutionName}' from the application base directory.");
    }

    private static void WriteDiagnostics(IReadOnlyList<WorkspaceLoadDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            Console.Error.WriteLine($"[workspace:{diagnostic.Kind}] {diagnostic.Message}");
        }
    }

    private static void WriteProjectGraph(RoslynWorkspaceLoader loaded)
    {
        var solution = loaded.Solution;
        var graph = solution.GetProjectDependencyGraph();

        Console.WriteLine($"Target: {loaded.TargetPath}");
        Console.WriteLine($"Projects: {solution.Projects.Count()}");
        Console.WriteLine();

        Console.WriteLine("Topological project order:");
        foreach (var projectId in graph.GetTopologicallySortedProjects())
        {
            var project = solution.GetProject(projectId)!;
            Console.WriteLine($"- {project.Name}");
        }

        Console.WriteLine();
        Console.WriteLine("Project graph:");

        foreach (var projectId in graph.GetTopologicallySortedProjects())
        {
            var project = solution.GetProject(projectId)!;
            var directDependencies = graph
                .GetProjectsThatThisProjectDirectlyDependsOn(projectId)
                .Select(id => solution.GetProject(id)!)
                .OrderBy(project => project.Name, StringComparer.Ordinal)
                .ToArray();

            var directDependents = graph
                .GetProjectsThatDirectlyDependOnThisProject(projectId)
                .Select(id => solution.GetProject(id)!)
                .OrderBy(project => project.Name, StringComparer.Ordinal)
                .ToArray();

            Console.WriteLine($"Project: {project.Name}");
            Console.WriteLine($"  File: {project.FilePath}");
            Console.WriteLine("  Direct dependencies:");
            WriteProjectNames(directDependencies);
            Console.WriteLine("  Direct dependents:");
            WriteProjectNames(directDependents);
            Console.WriteLine("  Documents:");
            WriteDocumentPaths(project);
            Console.WriteLine();
        }
    }

    private static void WriteProjectNames(IReadOnlyList<Project> projects)
    {
        if (projects.Count == 0)
        {
            Console.WriteLine("    (none)");
            return;
        }

        foreach (var project in projects)
        {
            Console.WriteLine($"    - {project.Name}");
        }
    }

    private static void WriteDocumentPaths(Project project)
    {
        foreach (var document in project.Documents.OrderBy(document => document.FilePath ?? document.Name, StringComparer.Ordinal))
        {
            Console.WriteLine($"    - {document.FilePath ?? document.Name}");
        }
    }
}
