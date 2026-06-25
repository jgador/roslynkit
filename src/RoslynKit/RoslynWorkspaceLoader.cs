using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynKit;

/// <summary>
/// Loads Roslyn workspaces from solution or project targets and exposes the resolved workspace state.
/// </summary>
public sealed class RoslynWorkspaceLoader : IDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly IReadOnlyDictionary<ProjectId, string?> _projectTargetFrameworks;

    private RoslynWorkspaceLoader(
        MSBuildWorkspace workspace,
        Solution solution,
        string targetPath,
        string targetKind,
        string rootPath,
        IReadOnlyDictionary<ProjectId, string?> projectTargetFrameworks,
        IReadOnlyList<WorkspaceLoadDiagnostic> workspaceDiagnostics)
    {
        Workspace = workspace;
        Solution = solution;
        TargetPath = targetPath;
        TargetKind = targetKind;
        RootPath = rootPath;
        _projectTargetFrameworks = projectTargetFrameworks;
        WorkspaceDiagnostics = workspaceDiagnostics;
    }

    public MSBuildWorkspace Workspace { get; }

    public Solution Solution { get; }

    public string TargetPath { get; }

    public string TargetKind { get; }

    public string RootPath { get; }

    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }

    /// <summary>
    /// Opens a solution or project target with MSBuildWorkspace and captures any workspace load diagnostics.
    /// </summary>
    public static async Task<RoslynWorkspaceLoader> LoadAsync(string targetPath, CancellationToken cancellationToken)
    {
        RegisterMSBuild();

        var fullTargetPath = Path.GetFullPath(targetPath);
        if (!File.Exists(fullTargetPath))
        {
            throw new CliUsageException("unknown", $"Target file '{fullTargetPath}' does not exist.");
        }

        var diagnostics = new List<WorkspaceLoadDiagnostic>();
        var progressEvents = new ConcurrentQueue<ProjectLoadProgress>();
        var progress = new Progress<ProjectLoadProgress>(entry => progressEvents.Enqueue(entry));
        var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
        {
            ["DesignTimeBuild"] = "true",
            ["BuildProjectReferences"] = "false",
            ["SkipCompilerExecution"] = "true",
        });

        workspace.RegisterWorkspaceFailedHandler(args =>
        {
            diagnostics.Add(new WorkspaceLoadDiagnostic(args.Diagnostic.Kind.ToString(), args.Diagnostic.Message));
        });

        var extension = Path.GetExtension(fullTargetPath);
        if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var solution = await workspace.OpenSolutionAsync(fullTargetPath, progress, cancellationToken).ConfigureAwait(false);
            return new RoslynWorkspaceLoader(
                workspace,
                solution,
                fullTargetPath,
                extension[1..],
                ResolveRootPath(fullTargetPath),
                ResolveProjectTargetFrameworks(solution, progressEvents.ToArray()),
                diagnostics);
        }

        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var project = await workspace.OpenProjectAsync(fullTargetPath, progress, cancellationToken).ConfigureAwait(false);
            return new RoslynWorkspaceLoader(
                workspace,
                project.Solution,
                fullTargetPath,
                "csproj",
                ResolveRootPath(fullTargetPath),
                ResolveProjectTargetFrameworks(project.Solution, progressEvents.ToArray()),
                diagnostics);
        }

        workspace.Dispose();
        throw new CliUsageException("unknown", "Target must be a .sln, .slnx, or .csproj file.");
    }

    public string? GetTargetFramework(Project project)
    {
        return _projectTargetFrameworks.TryGetValue(project.Id, out var targetFramework)
            ? targetFramework
            : null;
    }

    public async Task<IReadOnlyList<WorkspaceDocumentContext>> EnumerateDocumentsAsync(DocumentEnumerationOptions options, CancellationToken cancellationToken)
    {
        var documents = new List<WorkspaceDocumentContext>();

        foreach (var project in Solution.Projects
                     .OrderBy(project => project.Name, StringComparer.Ordinal)
                     .ThenBy(project => project.FilePath, StringComparer.Ordinal))
        {
            foreach (var document in project.Documents
                         .OrderBy(document => document.FilePath, StringComparer.Ordinal)
                         .ThenBy(document => document.Name, StringComparer.Ordinal))
            {
                if (!RoslynDocumentFilters.ShouldIncludeWorkspaceDocument(document, DocumentKindNames.Source, RootPath, options))
                {
                    continue;
                }

                documents.Add(await WorkspaceDocumentContext.CreateAsync(this, document, DocumentKindNames.Source, cancellationToken).ConfigureAwait(false));
            }

            if (options.IncludeGenerated)
            {
                foreach (var document in (await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
                             .OrderBy(document => document.FilePath, StringComparer.Ordinal)
                             .ThenBy(document => document.Name, StringComparer.Ordinal))
                {
                    if (!RoslynDocumentFilters.ShouldIncludeWorkspaceDocument(document, DocumentKindNames.SourceGenerated, RootPath, options))
                    {
                        continue;
                    }

                    documents.Add(await WorkspaceDocumentContext.CreateAsync(this, document, DocumentKindNames.SourceGenerated, cancellationToken).ConfigureAwait(false));
                }
            }

            if (options.IncludeAdditional)
            {
                foreach (var document in project.AdditionalDocuments
                             .OrderBy(document => document.FilePath, StringComparer.Ordinal)
                             .ThenBy(document => document.Name, StringComparer.Ordinal))
                {
                    if (!RoslynDocumentFilters.ShouldIncludeWorkspaceDocument(document, DocumentKindNames.Additional, RootPath, options))
                    {
                        continue;
                    }

                    documents.Add(await WorkspaceDocumentContext.CreateAsync(this, document, DocumentKindNames.Additional, cancellationToken).ConfigureAwait(false));
                }
            }

            if (options.IncludeAnalyzerConfig)
            {
                foreach (var document in project.AnalyzerConfigDocuments
                             .OrderBy(document => document.FilePath, StringComparer.Ordinal)
                             .ThenBy(document => document.Name, StringComparer.Ordinal))
                {
                    if (!RoslynDocumentFilters.ShouldIncludeWorkspaceDocument(document, DocumentKindNames.AnalyzerConfig, RootPath, options))
                    {
                        continue;
                    }

                    documents.Add(await WorkspaceDocumentContext.CreateAsync(this, document, DocumentKindNames.AnalyzerConfig, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        return documents
            .OrderBy(document => document.Descriptor.ProjectName, StringComparer.Ordinal)
            .ThenBy(document => document.Descriptor.TargetFramework, StringComparer.Ordinal)
            .ThenBy(document => document.Descriptor.DocumentKind, StringComparer.Ordinal)
            .ThenBy(document => document.Descriptor.Path, StringComparer.Ordinal)
            .ThenBy(document => document.Descriptor.Name, StringComparer.Ordinal)
            .ThenBy(document => document.Descriptor.DocumentKey, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<WorkspaceDocumentContext> FindTextDocumentAsync(
        string? filePath,
        string? documentKey,
        string commandName,
        CancellationToken cancellationToken)
    {
        var documents = await EnumerateDocumentsAsync(
            new DocumentEnumerationOptions(
                IncludeGenerated: true,
                IncludeAdditional: true,
                IncludeAnalyzerConfig: true,
                RepositoryRelevantOnly: false),
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(documentKey))
        {
            var match = documents.SingleOrDefault(document => string.Equals(document.Descriptor.DocumentKey, documentKey, StringComparison.Ordinal));
            return match
                ?? throw new CliUsageException(commandName, $"Document key '{documentKey}' is not part of the loaded target.");
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new CliUsageException(commandName, "Exactly one of '--file' or '--document-key' is required.");
        }

        var fullFilePath = Path.GetFullPath(filePath);
        var matches = documents
            .Where(document => document.Descriptor.Path is not null && PathComparer.Equals(document.Descriptor.Path, fullFilePath))
            .ToArray();

        return matches.Length switch
        {
            0 => throw new CliUsageException(commandName, $"File '{fullFilePath}' is not part of the loaded target."),
            1 => matches[0],
            _ => throw new CliUsageException(commandName, $"File '{fullFilePath}' appears in multiple project contexts. Use '--document-key' from 'workspace' to choose the exact document."),
        };
    }

    public void Dispose()
    {
        Workspace.Dispose();
    }

    private static IReadOnlyDictionary<ProjectId, string?> ResolveProjectTargetFrameworks(Solution solution, IReadOnlyList<ProjectLoadProgress> progressEvents)
    {
        var frameworksByProjectPath = progressEvents
            .Where(entry => !string.IsNullOrWhiteSpace(entry.FilePath))
            .GroupBy(entry => Path.GetFullPath(entry.FilePath!), PathComparer)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(entry => string.IsNullOrWhiteSpace(entry.TargetFramework) ? null : entry.TargetFramework)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                PathComparer);

        var result = new Dictionary<ProjectId, string?>();
        foreach (var project in solution.Projects)
        {
            if (string.IsNullOrWhiteSpace(project.FilePath))
            {
                result[project.Id] = null;
                continue;
            }

            var projectPath = Path.GetFullPath(project.FilePath);
            if (!frameworksByProjectPath.TryGetValue(projectPath, out var targetFrameworks))
            {
                result[project.Id] = null;
                continue;
            }

            var nonEmptyTargetFrameworks = targetFrameworks
                .Where(targetFramework => !string.IsNullOrWhiteSpace(targetFramework))
                .Cast<string>()
                .ToArray();

            result[project.Id] = nonEmptyTargetFrameworks.Length switch
            {
                0 => null,
                1 => nonEmptyTargetFrameworks[0],
                _ => nonEmptyTargetFrameworks.FirstOrDefault(targetFramework =>
                    project.Name.EndsWith($"({targetFramework})", StringComparison.Ordinal)),
            };
        }

        return result;
    }

    private static string ResolveRootPath(string targetPath)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory;
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

/// <summary>
/// Captures a resolved Roslyn text document together with its JSON descriptor.
/// </summary>
public sealed class WorkspaceDocumentContext
{
    private WorkspaceDocumentContext(TextDocument textDocument, DocumentDescriptor descriptor)
    {
        TextDocument = textDocument;
        Descriptor = descriptor;
    }

    public TextDocument TextDocument { get; }

    public DocumentDescriptor Descriptor { get; }

    public Project Project => TextDocument.Project;

    public Document? Document => TextDocument as Document;

    public string DocumentKind => Descriptor.DocumentKind;

    public static async Task<WorkspaceDocumentContext> CreateAsync(
        RoslynWorkspaceLoader loader,
        TextDocument textDocument,
        string documentKind,
        CancellationToken cancellationToken)
    {
        var normalizedPath = RoslynDocumentFilters.NormalizePath(textDocument.FilePath);
        var targetFramework = loader.GetTargetFramework(textDocument.Project);
        var documentKey = await CreateDocumentKeyAsync(loader, textDocument, documentKind, targetFramework, normalizedPath, cancellationToken).ConfigureAwait(false);
        var descriptor = new DocumentDescriptor(
            documentKey,
            textDocument.Project.Name,
            RoslynDocumentFilters.NormalizePath(textDocument.Project.FilePath),
            targetFramework,
            documentKind,
            textDocument.Name,
            normalizedPath);

        return new WorkspaceDocumentContext(textDocument, descriptor);
    }

    private static async Task<string> CreateDocumentKeyAsync(
        RoslynWorkspaceLoader loader,
        TextDocument textDocument,
        string documentKind,
        string? targetFramework,
        string? normalizedPath,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine(loader.TargetPath);
        builder.AppendLine(textDocument.Project.FilePath ?? string.Empty);
        builder.AppendLine(textDocument.Project.Name);
        builder.AppendLine(targetFramework ?? string.Empty);
        builder.AppendLine(documentKind);
        builder.AppendLine(textDocument.Name);
        builder.AppendLine(normalizedPath ?? string.Empty);

        if (normalizedPath is null)
        {
            var text = await textDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            builder.AppendLine(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))));
        }

        return $"doc_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))}";
    }
}
