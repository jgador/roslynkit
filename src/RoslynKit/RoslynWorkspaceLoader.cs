using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynKit;

/// <summary>
/// Loads an MSBuild workspace from a solution or project target and exposes the resolved documents and diagnostics for commands.
/// </summary>
public sealed class RoslynWorkspaceLoader : IDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly object MSBuildRegistrationLock = new();

    private readonly IReadOnlyDictionary<ProjectId, string?> _projectTargetFrameworks;

    private RoslynWorkspaceLoader(
        Workspace workspace,
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

    /// <summary>
    /// Active Roslyn workspace used by semantic APIs after target load.
    /// </summary>
    public Workspace Workspace { get; }

    /// <summary>
    /// Loaded solution graph, whether the original target was a solution or a project.
    /// </summary>
    public Solution Solution { get; }

    /// <summary>
    /// Absolute path to the solution or project target supplied on the command line.
    /// </summary>
    public string TargetPath { get; }

    /// <summary>
    /// Target file kind reported in workspace output, such as <c>slnx</c> or <c>csproj</c>.
    /// </summary>
    public string TargetKind { get; }

    /// <summary>
    /// Repository or target-root path used to filter workspace-visible documents.
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// Workspace load diagnostics captured from MSBuild and Roslyn while opening the target.
    /// </summary>
    public IReadOnlyList<WorkspaceLoadDiagnostic> WorkspaceDiagnostics { get; }

    internal GitWorktreeFingerprint? LoadedWorktreeFingerprint { get; private set; }

    /// <summary>
    /// Opens the requested solution or project target with <c>MSBuildWorkspace</c> and records workspace load diagnostics.
    /// </summary>
    public static async Task<RoslynWorkspaceLoader> LoadAsync(string targetPath, CancellationToken cancellationToken)
    {
        return await LoadAsync(targetPath, filePath: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens an explicit target or discovers every C# project in the containing repository.
    /// </summary>
    public static async Task<RoslynWorkspaceLoader> LoadAsync(
        string? targetPath,
        string? filePath,
        CancellationToken cancellationToken)
    {
        RegisterMSBuild();

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            var repository = RepositoryContextResolver.Resolve(filePath);
            return await LoadRepositoryAsync(repository.RootPath, cancellationToken).ConfigureAwait(false);
        }

        var fullTargetPath = Path.GetFullPath(targetPath);
        if (Directory.Exists(fullTargetPath))
        {
            var repository = RepositoryContextResolver.Resolve(fullTargetPath);
            return await LoadRepositoryAsync(repository.RootPath, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(fullTargetPath))
        {
            throw new CliUsageException("unknown", $"Target file '{fullTargetPath}' does not exist.");
        }

        var diagnostics = new List<WorkspaceLoadDiagnostic>();
        var progressEvents = new ConcurrentQueue<ProjectLoadProgress>();
        var progress = new Progress<ProjectLoadProgress>(entry => progressEvents.Enqueue(entry));
        var workspace = CreateMSBuildWorkspace(diagnostics);

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

        if (extension.Equals(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var projectPaths = await ReadSolutionFilterProjectsAsync(
                    fullTargetPath,
                    cancellationToken).ConfigureAwait(false);
                await OpenProjectsAsync(
                    workspace,
                    projectPaths,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                var solution = workspace.CurrentSolution;
                return new RoslynWorkspaceLoader(
                    workspace,
                    solution,
                    fullTargetPath,
                    "slnf",
                    ResolveRootPath(fullTargetPath),
                    ResolveProjectTargetFrameworks(solution, progressEvents.ToArray()),
                    diagnostics);
            }
            catch
            {
                workspace.Dispose();
                throw;
            }
        }

        workspace.Dispose();
        throw new CliUsageException("unknown", "Target must be a .sln, .slnx, .slnf, or .csproj file.");
    }

    /// <summary>
    /// Builds one in-process syntax workspace from repository C# files without evaluating MSBuild projects.
    /// </summary>
    public static async Task<RoslynWorkspaceLoader> LoadTextOnlyAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        var fullTargetPath = Path.GetFullPath(targetPath);
        var isRepository = Directory.Exists(fullTargetPath);
        if (!isRepository && !File.Exists(fullTargetPath))
        {
            throw new CliUsageException("unknown", $"Target file '{fullTargetPath}' does not exist.");
        }

        var extension = Path.GetExtension(fullTargetPath);
        if (!isRepository
            && !extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".slnf", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException("unknown", "Target must be a repository directory or a .sln, .slnx, .slnf, or .csproj file.");
        }

        var rootPath = isRepository
            ? RepositoryContextResolver.Resolve(fullTargetPath).RootPath
            : ResolveRootPath(fullTargetPath);
        var syntheticProjectPath = isRepository
            ? (await RepositoryProjectDiscovery.DiscoverAsync(rootPath, cancellationToken).ConfigureAwait(false))[0]
            : fullTargetPath;
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId(debugName: "RoslynKit text-only search");
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            $"{Path.GetFileNameWithoutExtension(fullTargetPath)} (text-only)",
            "RoslynKit.TextOnlySearch",
            LanguageNames.CSharp,
            filePath: syntheticProjectPath,
            outputFilePath: null,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Parse),
            metadataReferences: GetTrustedPlatformReferences());
        var solution = workspace.CurrentSolution.AddProject(projectInfo);

        foreach (var filePath in EnumerateTextOnlySourceFiles(rootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId, debugName: filePath),
                Path.GetFileName(filePath),
                SourceText.From(source, Encoding.UTF8),
                filePath: filePath);
        }

        if (!workspace.TryApplyChanges(solution))
        {
            workspace.Dispose();
            throw new InvalidOperationException("Could not create the in-process text-only search workspace.");
        }

        return new RoslynWorkspaceLoader(
            workspace,
            workspace.CurrentSolution,
            fullTargetPath,
            isRepository ? "repository-text-only" : $"{extension[1..]}-text-only",
            rootPath,
            new Dictionary<ProjectId, string?> { [projectId] = null },
            []);
    }

    private static async Task<RoslynWorkspaceLoader> LoadRepositoryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var projectPaths = await RepositoryProjectDiscovery.DiscoverAsync(
            repositoryRoot,
            cancellationToken).ConfigureAwait(false);
        var diagnostics = new List<WorkspaceLoadDiagnostic>();
        var progressEvents = new ConcurrentQueue<ProjectLoadProgress>();
        var progress = new Progress<ProjectLoadProgress>(entry => progressEvents.Enqueue(entry));
        var workspace = CreateMSBuildWorkspace(diagnostics);

        try
        {
            await OpenProjectsAsync(
                workspace,
                projectPaths,
                progress,
                cancellationToken).ConfigureAwait(false);
            var solution = workspace.CurrentSolution;
            return new RoslynWorkspaceLoader(
                workspace,
                solution,
                repositoryRoot,
                "repository",
                repositoryRoot,
                ResolveProjectTargetFrameworks(solution, progressEvents.ToArray()),
                diagnostics);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static MSBuildWorkspace CreateMSBuildWorkspace(
        ICollection<WorkspaceLoadDiagnostic> diagnostics)
    {
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
        return workspace;
    }

    private static async Task OpenProjectsAsync(
        MSBuildWorkspace workspace,
        IReadOnlyList<string> projectPaths,
        IProgress<ProjectLoadProgress> progress,
        CancellationToken cancellationToken)
    {
        foreach (var projectPath in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (workspace.CurrentSolution.Projects.Any(project =>
                    !string.IsNullOrWhiteSpace(project.FilePath)
                    && PathComparer.Equals(Path.GetFullPath(project.FilePath), projectPath)))
            {
                continue;
            }

            _ = await workspace.OpenProjectAsync(
                projectPath,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadSolutionFilterProjectsAsync(
        string solutionFilterPath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(solutionFilterPath);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("solution", out var solution)
            || !solution.TryGetProperty("path", out var solutionPathElement)
            || solutionPathElement.ValueKind != JsonValueKind.String
            || !solution.TryGetProperty("projects", out var projects)
            || projects.ValueKind != JsonValueKind.Array)
        {
            throw new CliUsageException(
                "unknown",
                $"Solution filter '{solutionFilterPath}' does not contain a valid solution path and project list.");
        }

        var filterDirectory = Path.GetDirectoryName(solutionFilterPath)!;
        var solutionPath = ResolvePortableRelativePath(
            filterDirectory,
            solutionPathElement.GetString()!);
        var solutionDirectory = Path.GetDirectoryName(solutionPath)!;
        var projectPaths = projects
            .EnumerateArray()
            .Where(static project => project.ValueKind == JsonValueKind.String)
            .Select(project => ResolvePortableRelativePath(solutionDirectory, project.GetString()!))
            .Distinct(PathComparer)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (projectPaths.Length == 0 || projectPaths.Any(path => !File.Exists(path)))
        {
            throw new CliUsageException(
                "unknown",
                $"Solution filter '{solutionFilterPath}' references no existing C# projects.");
        }

        return projectPaths;
    }

    private static string ResolvePortableRelativePath(string baseDirectory, string path)
    {
        var portablePath = path
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(portablePath, baseDirectory);
    }

    internal void SetLoadedWorktreeFingerprint(GitWorktreeFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        LoadedWorktreeFingerprint = fingerprint;
    }

    /// <summary>
    /// Resolves the target framework label associated with a loaded project context.
    /// </summary>
    public string? GetTargetFramework(Project project)
    {
        return _projectTargetFrameworks.TryGetValue(project.Id, out var targetFramework)
            ? targetFramework
            : null;
    }

    /// <summary>
    /// Renders a loaded target path relative to the root when it is inside that root.
    /// </summary>
    public string? FormatPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        return IsPathUnderRoot(fullPath)
            ? Path.GetRelativePath(RootPath, fullPath)
            : fullPath;
    }

    /// <summary>
    /// Enumerates command-addressable source, generated, additional, and analyzer-config documents after workspace load.
    /// </summary>
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

    /// <summary>
    /// Resolves one text document from a path selector and optional project, TFM, or document-kind context.
    /// </summary>
    public async Task<WorkspaceDocumentContext> FindTextDocumentAsync(
        string? filePath,
        string? projectPath,
        string? targetFramework,
        string? documentKind,
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

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new CliUsageException(commandName, "Missing required option '--file'.");
        }

        var fullFilePath = Path.GetFullPath(filePath);
        var fileMatches = documents
            .Where(document => document.Descriptor.Path is not null && PathComparer.Equals(document.Descriptor.Path, fullFilePath))
            .ToArray();

        var matches = fileMatches;
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            var fullProjectPath = Path.GetFullPath(projectPath);
            matches = matches
                .Where(document => document.Descriptor.ProjectPath is not null && PathComparer.Equals(document.Descriptor.ProjectPath, fullProjectPath))
                .ToArray();
        }

        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            matches = matches
                .Where(document => string.Equals(document.Descriptor.TargetFramework, targetFramework, StringComparison.Ordinal))
                .ToArray();
        }

        if (!string.IsNullOrWhiteSpace(documentKind))
        {
            matches = matches
                .Where(document => string.Equals(document.Descriptor.DocumentKind, documentKind, StringComparison.Ordinal))
                .ToArray();
        }

        return matches.Length switch
        {
            0 when fileMatches.Length == 0 => throw new CliUsageException(commandName, $"File '{fullFilePath}' is not part of the loaded target."),
            0 => throw new CliUsageException(commandName, $"File '{fullFilePath}' has no document context matching the supplied --project, --tfm, or --document-kind options.", CreateDocumentContextHint(fileMatches)),
            1 => matches[0],
            _ => throw new CliUsageException(commandName, $"File '{fullFilePath}' appears in multiple document contexts.", CreateDocumentContextHint(matches)),
        };
    }

    /// <summary>
    /// Releases the underlying MSBuild workspace and its loaded solution state.
    /// </summary>
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

    private static IEnumerable<string> EnumerateTextOnlySourceFiles(string rootPath)
    {
        var excludedSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".vs",
            "artifacts",
            "bin",
            "node_modules",
            "obj",
            "TestResults",
        };
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
        };

        return Directory.EnumerateFiles(rootPath, "*.cs", options)
            .Where(filePath => !Path.GetRelativePath(rootPath, filePath)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(excludedSegments.Contains))
            .Order(StringComparer.Ordinal);
    }

    private static IReadOnlyList<MetadataReference> GetTrustedPlatformReferences()
    {
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        return string.IsNullOrWhiteSpace(trustedPlatformAssemblies)
            ? []
            : trustedPlatformAssemblies
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Order(StringComparer.Ordinal)
                .Select(path => MetadataReference.CreateFromFile(path))
                .ToArray();
    }

    private string CreateDocumentContextHint(IReadOnlyList<WorkspaceDocumentContext> matches)
    {
        var candidates = matches
            .OrderBy(document => FormatPath(document.Descriptor.ProjectPath), StringComparer.Ordinal)
            .ThenBy(document => document.Descriptor.TargetFramework, StringComparer.Ordinal)
            .ThenBy(document => document.Descriptor.DocumentKind, StringComparer.Ordinal)
            .ThenBy(document => FormatPath(document.Descriptor.Path), StringComparer.Ordinal)
            .Select(document =>
                $"project '{FormatPath(document.Descriptor.ProjectPath) ?? document.Descriptor.ProjectName}' tfm '{document.Descriptor.TargetFramework ?? "-"}' kind '{document.Descriptor.DocumentKind}' path '{FormatPath(document.Descriptor.Path) ?? "-"}'")
            .ToArray();

        return "Retry with --project, --tfm, or --document-kind. Matches: " + string.Join("; ", candidates);
    }

    private bool IsPathUnderRoot(string fullPath)
    {
        var root = Path.GetFullPath(RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (PathComparer.Equals(path, root))
        {
            return true;
        }

        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static void RegisterMSBuild()
    {
        // Registration must be serialized: with concurrent in-process callers (such as parallel tests),
        // an unguarded check-then-register race lets the loser call RegisterDefaults after MSBuild
        // assemblies are already loaded, which throws.
        lock (MSBuildRegistrationLock)
        {
            if (MSBuildLocator.IsRegistered)
            {
                return;
            }

            MSBuildLocator.RegisterDefaults();
        }
    }
}

/// <summary>
/// Pairs a resolved Roslyn text document with the document descriptor RoslynKit returns in command payloads.
/// </summary>
public sealed class WorkspaceDocumentContext
{
    private WorkspaceDocumentContext(TextDocument textDocument, DocumentDescriptor descriptor)
    {
        TextDocument = textDocument;
        Descriptor = descriptor;
    }

    /// <summary>
    /// Roslyn text document used for source, generated, additional, or analyzer-config reads.
    /// </summary>
    public TextDocument TextDocument { get; }

    /// <summary>
    /// Stable command-output descriptor for the resolved Roslyn text document.
    /// </summary>
    public DocumentDescriptor Descriptor { get; }

    /// <summary>
    /// Project context that owns the resolved Roslyn text document.
    /// </summary>
    public Project Project => TextDocument.Project;

    /// <summary>
    /// Semantic C# document when the resolved text document supports semantic operations.
    /// </summary>
    public Document? Document => TextDocument as Document;

    /// <summary>
    /// RoslynKit document-kind name used to route document and semantic commands.
    /// </summary>
    public string DocumentKind => Descriptor.DocumentKind;

    /// <summary>
    /// Builds the stable document descriptor RoslynKit returns so later commands can select the same document by path or key.
    /// </summary>
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
            normalizedPath,
            loader.FormatPath(textDocument.Project.FilePath),
            loader.FormatPath(normalizedPath));

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
