using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RoslynKit;

/// <summary>
/// Carries the selected SDK and evaluated dependency inputs into workspace loading and reconciliation.
/// </summary>
internal sealed record WorkspacePreparationResult(
    DotnetSdkResolution Sdk,
    IReadOnlyList<string> ProjectPaths,
    IReadOnlyList<string> BuildInputPaths,
    IReadOnlyList<string> AssetsPaths,
    bool Restored);

/// <summary>
/// Prepares a supported repository before initial or structural workspace loading.
/// </summary>
internal static class WorkspacePreparation
{
    private static readonly WorkspacePreparationService Shared = new(new WorkspaceProcessRunner());

    public static Task<WorkspacePreparationResult> PrepareAsync(
        string repositoryRoot,
        string? targetPath,
        bool automaticRestore,
        CancellationToken cancellationToken)
    {
        return Shared.PrepareAsync(repositoryRoot, targetPath, automaticRestore, cancellationToken);
    }
}

/// <summary>
/// Evaluates dependency inputs and suppresses repeated restores for unchanged successful or failed inputs.
/// </summary>
internal sealed class WorkspacePreparationService(IWorkspaceProcessRunner runner)
{
    private const string EvaluationProperties = "TargetFramework,TargetFrameworks,TargetFrameworkIdentifier,UsingMicrosoftNETSdk,ProjectAssetsFile,MSBuildAllProjects,MSBuildProjectExtensionsPath,RestoreConfigFile";
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly ConcurrentDictionary<string, PreparationState> _states = new(PathComparer);

    public async Task<WorkspacePreparationResult> PrepareAsync(
        string repositoryRoot,
        string? targetPath,
        bool automaticRestore,
        CancellationToken cancellationToken)
    {
        WorkspaceSupportValidator.ValidatePlatform();
        var root = RepositoryContextResolver.ResolveExplicitRoot(repositoryRoot, cancellationToken).RootPath;
        var target = NormalizeTarget(root, targetPath);
        var state = _states.GetOrAdd(root + '\0' + target, static _ => new PreparationState());
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sdk = await DotnetSdkResolver.ResolveAsync(root, runner, cancellationToken).ConfigureAwait(false);
            var projectPaths = await DiscoverProjectsAsync(root, target, cancellationToken).ConfigureAwait(false);
            var graph = await EvaluateGraphAsync(root, target, projectPaths, sdk, cancellationToken).ConfigureAwait(false);
            var inputFingerprint = await FingerprintInputsAsync(sdk, graph, cancellationToken).ConfigureAwait(false);
            var assetsFingerprint = await FingerprintFilesAsync(graph.Select(static project => project.AssetsPath), cancellationToken).ConfigureAwait(false);
            var knownSuccessfulState = state.SuccessfulInputs == inputFingerprint && state.SuccessfulAssets == assetsFingerprint;
            var stale = !knownSuccessfulState && (graph.Any(AssetsNeedRestore)
                || (state.SuccessfulInputs is not null && state.SuccessfulInputs != inputFingerprint));

            if (!automaticRestore)
            {
                var missingAssets = graph.FirstOrDefault(static project => !AssetsMatchFramework(project));
                if (missingAssets is not null)
                {
                    throw new WorkspacePreparationException(
                        $"Dependency assets '{missingAssets.AssetsPath}' are missing or incompatible and automatic restore is disabled. Run dotnet restore for '{target ?? root}' before analysis.");
                }

                return CreateResult(sdk, graph, restored: false);
            }

            if (state.FailedInputs == inputFingerprint && state.FailedAssets == assetsFingerprint)
            {
                throw new WorkspacePreparationException(
                    $"The previous restore failed for unchanged dependency inputs. {state.FailureMessage} " +
                    "Fix the dependency inputs, restore manually, or restart the repository session before retrying.");
            }

            if (!stale)
            {
                state.SuccessfulInputs = inputFingerprint;
                state.SuccessfulAssets = assetsFingerprint;
                state.FailedInputs = null;
                state.FailedAssets = null;
                state.FailureMessage = null;
                return CreateResult(sdk, graph, restored: false);
            }

            try
            {
                foreach (var restoreTarget in GetRestoreTargets(target, projectPaths, graph))
                {
                    var result = await runner.RunAsync(
                        sdk.DotnetPath,
                        root,
                        ["restore", restoreTarget, "--nologo", "--verbosity", "quiet", "--force", "--disable-build-servers"],
                        TimeSpan.FromMinutes(5),
                        cancellationToken).ConfigureAwait(false);
                    if (result.ExitCode != 0)
                    {
                        throw new WorkspacePreparationException(
                            $"Dependency restore failed for '{restoreTarget}'. {DotnetSdkResolver.DescribeFailure(result)}");
                    }
                }

                graph = await EvaluateGraphAsync(root, target, projectPaths, sdk, cancellationToken).ConfigureAwait(false);
                var invalidAssets = graph.FirstOrDefault(static project => !AssetsMatchFramework(project));
                if (invalidAssets is not null)
                {
                    throw new WorkspacePreparationException(
                        $"Restore did not produce usable dependency assets at '{invalidAssets.AssetsPath}'. Check ProjectAssetsFile and restore diagnostics.");
                }

                state.SuccessfulInputs = await FingerprintInputsAsync(sdk, graph, cancellationToken).ConfigureAwait(false);
                state.SuccessfulAssets = await FingerprintFilesAsync(graph.Select(static project => project.AssetsPath), cancellationToken).ConfigureAwait(false);
                state.FailedInputs = null;
                state.FailedAssets = null;
                state.FailureMessage = null;
                return CreateResult(sdk, graph, restored: true);
            }
            catch (WorkspacePreparationException exception)
            {
                try
                {
                    graph = await EvaluateGraphAsync(root, target, projectPaths, sdk, cancellationToken).ConfigureAwait(false);
                }
                catch (WorkspacePreparationException)
                {
                    // A failed restore can leave incomplete imports; retain the last complete input graph in that case.
                }

                state.FailedInputs = await FingerprintInputsAsync(sdk, graph, cancellationToken).ConfigureAwait(false);
                state.FailedAssets = await FingerprintFilesAsync(graph.Select(static project => project.AssetsPath), cancellationToken).ConfigureAwait(false);
                state.FailureMessage = exception.Message;
                throw;
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static string? NormalizeTarget(string root, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        var fullPath = PathCanonicalizer.ResolveExistingPath(targetPath, root);
        if (Directory.Exists(fullPath))
        {
            if (!PathComparer.Equals(RepositoryContextResolver.ResolveExplicitRoot(fullPath).RootPath, root))
            {
                throw new WorkspacePreparationException("The target directory must be the explicit repository root.");
            }

            return null;
        }

        if (!File.Exists(fullPath))
        {
            throw new WorkspacePreparationException($"Workspace target '{fullPath}' does not exist.");
        }

        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new WorkspacePreparationException("The workspace target must be inside the explicit repository root.");
        }

        return fullPath;
    }

    private static async Task<IReadOnlyList<string>> DiscoverProjectsAsync(string root, string? target, CancellationToken cancellationToken)
    {
        if (target is null)
        {
            return await RepositoryProjectDiscovery.DiscoverAsync(root, cancellationToken).ConfigureAwait(false);
        }

        var extension = Path.GetExtension(target).ToLowerInvariant();
        var directory = Path.GetDirectoryName(target)!;
        IEnumerable<string> paths;
        switch (extension)
        {
            case ".csproj":
                return [target];
            case ".slnx":
                var solutionXml = XDocument.Parse(await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false));
                paths = solutionXml.Descendants().Where(static element => element.Name.LocalName == "Project")
                    .Select(static element => (string?)element.Attribute("Path"))
                    .OfType<string>();
                break;
            case ".sln":
                var solutionText = await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false);
                paths = Regex.Matches(solutionText, "^Project\\([^\\r\\n]+?\\)\\s*=\\s*\"[^\"]*\"\\s*,\\s*\"([^\"]+\\.[^\"]*proj)\"", RegexOptions.Multiline)
                    .Select(static match => match.Groups[1].Value);
                break;
            case ".slnf":
                using (var filter = JsonDocument.Parse(await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false)))
                {
                    if (!filter.RootElement.TryGetProperty("solution", out var solution)
                        || !solution.TryGetProperty("path", out var solutionPath)
                        || solutionPath.ValueKind != JsonValueKind.String
                        || !solution.TryGetProperty("projects", out var selectedProjects)
                        || selectedProjects.ValueKind != JsonValueKind.Array)
                    {
                        throw new WorkspacePreparationException($"Solution filter '{target}' must specify a solution path and project list.");
                    }

                    directory = Path.GetDirectoryName(ResolvePortablePath(directory, solutionPath.GetString()!))!;
                    paths = selectedProjects.EnumerateArray().Select(static project => project.GetString()!).ToArray();
                }

                break;
            default:
                throw new WorkspacePreparationException("Workspace targets must be .csproj, .sln, .slnx, or .slnf files.");
        }

        var projects = paths.Select(path => ResolvePortablePath(directory, path)).Distinct(PathComparer).Order(StringComparer.Ordinal).ToArray();
        if (projects.Length == 0)
        {
            throw new WorkspacePreparationException($"Workspace target '{target}' does not contain any C# projects.");
        }

        return projects;
    }

    private async Task<IReadOnlyList<EvaluatedProject>> EvaluateGraphAsync(
        string root,
        string? target,
        IReadOnlyList<string> projectPaths,
        DotnetSdkResolution sdk,
        CancellationToken cancellationToken)
    {
        var projects = new Dictionary<string, EvaluatedProject>(PathComparer);
        var pending = new Queue<string>(projectPaths);
        while (pending.TryDequeue(out var path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canonicalPath = PathCanonicalizer.ResolveExistingPath(path);
            if (projects.ContainsKey(canonicalPath))
            {
                continue;
            }

            if (!File.Exists(canonicalPath) || !Path.GetExtension(canonicalPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkspacePreparationException($"Workspace project '{canonicalPath}' must be an existing C# .csproj file.");
            }

            var project = await EvaluateProjectAsync(root, canonicalPath, sdk, cancellationToken).ConfigureAwait(false);
            if (target is not null)
            {
                project.BuildInputs.Add(target);
            }

            projects.Add(canonicalPath, project);
            foreach (var reference in project.ProjectReferences)
            {
                pending.Enqueue(reference);
            }
        }

        return projects.Values.OrderBy(static project => project.Path, StringComparer.Ordinal).ToArray();
    }

    private async Task<EvaluatedProject> EvaluateProjectAsync(
        string root, string projectPath, DotnetSdkResolution sdk, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            sdk.DotnetPath,
            root,
            ["msbuild", projectPath, "-nologo", "-verbosity:quiet", "-property:DesignTimeBuild=true", "-getProperty:" + EvaluationProperties, "-getItem:ProjectReference"],
            TimeSpan.FromMinutes(1),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new WorkspacePreparationException($"Could not evaluate '{projectPath}'. {DotnetSdkResolver.DescribeFailure(result)}");
        }

        Dictionary<string, string> properties;
        string[] references;
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            properties = json.RootElement.GetProperty("Properties").EnumerateObject()
                .ToDictionary(static property => property.Name, static property => property.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            references = json.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray()
                .Select(item => ResolvePortablePath(Path.GetDirectoryName(projectPath)!, item.GetProperty("FullPath").GetString()!))
                .Distinct(PathComparer).ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new WorkspacePreparationException($"MSBuild returned invalid project evaluation data for '{projectPath}': {exception.Message}");
        }

        WorkspaceSupportValidator.ValidateProject(projectPath, properties);
        var assetsPath = properties.GetValueOrDefault("ProjectAssetsFile");
        if (string.IsNullOrWhiteSpace(assetsPath))
        {
            throw new WorkspacePreparationException($"Project '{projectPath}' does not define a ProjectAssetsFile.");
        }

        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        assetsPath = ResolvePortablePath(projectDirectory, assetsPath);
        var inputs = new HashSet<string>(PathComparer) { projectPath };
        AddAncestorInputs(inputs, projectDirectory);
        AddAncestorInputs(inputs, root);
        AddConfigInputsFromAssets(inputs, assetsPath);
        foreach (var propertyName in new[] { "MSBuildAllProjects", "RestoreConfigFile" })
        {
            foreach (var path in properties.GetValueOrDefault(propertyName, string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                inputs.Add(ResolvePortablePath(projectDirectory, path));
            }
        }

        var preprocess = await runner.RunAsync(
            sdk.DotnetPath, root,
            ["msbuild", projectPath, "-nologo", "-verbosity:quiet", "-property:DesignTimeBuild=true", "-preprocess"],
            TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        if (preprocess.ExitCode != 0)
        {
            throw new WorkspacePreparationException($"Could not inspect evaluated imports for '{projectPath}'. {DotnetSdkResolver.DescribeFailure(preprocess)}");
        }

        AddEvaluatedImports(inputs, preprocess.StandardOutput, projectPath);
        return new EvaluatedProject(projectPath, properties["TargetFramework"], assetsPath, inputs, references);
    }

    private static void AddEvaluatedImports(HashSet<string> inputs, string preprocessedProject, string projectPath)
    {
        if (preprocessedProject.EndsWith("[output truncated]", StringComparison.Ordinal))
        {
            throw new WorkspacePreparationException(
                $"Evaluated imports for '{projectPath}' exceeded the bounded MSBuild output limit.");
        }

        try
        {
            var xml = XDocument.Parse(preprocessedProject);
            foreach (var comment in xml.DescendantNodes().OfType<XComment>())
            {
                if (!comment.Value.TrimStart().StartsWith("====", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var line in comment.Value.Split('\n'))
                {
                    var path = line.Trim();
                    if (Path.IsPathFullyQualified(path) && File.Exists(path))
                    {
                        inputs.Add(PathCanonicalizer.ResolveExistingPath(path));
                    }
                }
            }
        }
        catch (System.Xml.XmlException exception)
        {
            throw new WorkspacePreparationException($"MSBuild returned invalid evaluated imports for '{projectPath}': {exception.Message}");
        }
    }

    private static void AddAncestorInputs(HashSet<string> inputs, string startingDirectory)
    {
        for (var directory = new DirectoryInfo(startingDirectory); directory is not null; directory = directory.Parent)
        {
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.Config", "NuGet.config", "nuget.config", "global.json" })
            {
                inputs.Add(Path.Combine(directory.FullName, name));
            }
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            inputs.Add(Path.Combine(userProfile, ".nuget", "NuGet", "NuGet.Config"));
        }

        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(applicationData))
        {
            inputs.Add(Path.Combine(applicationData, "NuGet", "NuGet.Config"));
        }
    }

    private static void AddConfigInputsFromAssets(HashSet<string> inputs, string assetsPath)
    {
        if (!File.Exists(assetsPath))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(assetsPath);
            using var assets = JsonDocument.Parse(stream);
            if (assets.RootElement.TryGetProperty("project", out var project)
                && project.TryGetProperty("restore", out var restore)
                && restore.TryGetProperty("configFilePaths", out var configPaths))
            {
                foreach (var path in configPaths.EnumerateArray())
                {
                    if (path.ValueKind == JsonValueKind.String && Path.IsPathFullyQualified(path.GetString()!))
                    {
                        inputs.Add(path.GetString()!);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException)
        {
            // Missing or malformed assets are handled by the restore decision.
        }
    }

    private static bool AssetsNeedRestore(EvaluatedProject project)
    {
        if (!AssetsMatchFramework(project))
        {
            return true;
        }

        var assetsWriteTime = File.GetLastWriteTimeUtc(project.AssetsPath);
        return project.BuildInputs.Any(path => !IsRestoreOutput(path) && File.Exists(path) && File.GetLastWriteTimeUtc(path) > assetsWriteTime);
    }

    private static bool AssetsMatchFramework(EvaluatedProject project)
    {
        try
        {
            using var stream = File.OpenRead(project.AssetsPath);
            using var assets = JsonDocument.Parse(stream);
            return assets.RootElement.TryGetProperty("project", out var projectMetadata)
                && projectMetadata.TryGetProperty("frameworks", out var frameworks)
                && frameworks.EnumerateObject().Any(framework =>
                    framework.Name.Equals(project.TargetFramework, StringComparison.OrdinalIgnoreCase)
                    || (framework.Value.TryGetProperty("targetAlias", out var alias)
                        && alias.ValueKind == JsonValueKind.String
                        && string.Equals(alias.GetString(), project.TargetFramework, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static IEnumerable<string> GetRestoreTargets(string? target, IReadOnlyList<string> selectedProjects, IReadOnlyList<EvaluatedProject> graph)
    {
        if (target is not null)
        {
            return [target];
        }

        var referencedProjects = graph.SelectMany(static project => project.ProjectReferences).ToHashSet(PathComparer);
        var roots = selectedProjects.Where(path => !referencedProjects.Contains(path)).ToList();
        var projectsByPath = graph.ToDictionary(static project => project.Path, PathComparer);
        var covered = new HashSet<string>(PathComparer);
        foreach (var root in roots)
        {
            Cover(root);
        }

        foreach (var path in selectedProjects)
        {
            if (!covered.Contains(path))
            {
                roots.Add(path);
                Cover(path);
            }
        }

        return roots;

        void Cover(string path)
        {
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.TryPop(out var current))
            {
                if (!covered.Add(current) || !projectsByPath.TryGetValue(current, out var project))
                {
                    continue;
                }

                foreach (var reference in project.ProjectReferences)
                {
                    pending.Push(reference);
                }
            }
        }
    }

    private static WorkspacePreparationResult CreateResult(DotnetSdkResolution sdk, IReadOnlyList<EvaluatedProject> projects, bool restored)
    {
        return new WorkspacePreparationResult(
            sdk,
            projects.Select(static project => project.Path).ToArray(),
            projects.SelectMany(static project => project.BuildInputs).Distinct(PathComparer).Order(StringComparer.Ordinal).ToArray(),
            projects.Select(static project => project.AssetsPath).Distinct(PathComparer).Order(StringComparer.Ordinal).ToArray(),
            restored);
    }

    private static async Task<string> FingerprintInputsAsync(DotnetSdkResolution sdk, IReadOnlyList<EvaluatedProject> projects, CancellationToken cancellationToken)
    {
        var files = await FingerprintFilesAsync(
            projects.SelectMany(static project => project.BuildInputs).Where(static path => !IsRestoreOutput(path)), cancellationToken).ConfigureAwait(false);
        var projectsIdentity = string.Join('\n', projects.Select(static project => project.Path + '\0' + project.TargetFramework + '\0' + project.AssetsPath));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sdk.SdkDirectory + '\0' + sdk.Version + '\0' + files + '\0' + projectsIdentity)));
    }

    private static async Task<string> FingerprintFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths.Distinct(PathComparer).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            fingerprint.AppendData(Encoding.UTF8.GetBytes(path + '\0'));
            if (!File.Exists(path))
            {
                fingerprint.AppendData([0]);
                continue;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            fingerprint.AppendData(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        return Convert.ToHexString(fingerprint.GetHashAndReset());
    }

    private static bool IsRestoreOutput(string path)
    {
        return path.EndsWith(".nuget.g.props", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".nuget.g.targets", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolvePortablePath(string directory, string path)
    {
        return Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar), directory);
    }

    /// <summary>
    /// Retains only dependency preparation outcomes for one repository scope.
    /// </summary>
    private sealed class PreparationState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? SuccessfulInputs { get; set; }
        public string? SuccessfulAssets { get; set; }
        public string? FailedInputs { get; set; }
        public string? FailedAssets { get; set; }
        public string? FailureMessage { get; set; }
    }

    /// <summary>
    /// Records SDK-evaluated framework, asset location, references, and import provenance for one project.
    /// </summary>
    private sealed record EvaluatedProject(string Path, string TargetFramework, string AssetsPath, HashSet<string> BuildInputs, IReadOnlyList<string> ProjectReferences);
}
