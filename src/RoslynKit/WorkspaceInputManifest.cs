using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynKit;

/// <summary>
/// Captures workspace input contents and membership for background reconciliation and index publication.
/// </summary>
internal sealed class WorkspaceInputManifest
{
    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".roslynkit", ".vs", "artifacts", "bin", "node_modules", "TestResults",
    };

    private static readonly string[] AncestorInputNames =
    [
        "global.json", "NuGet.Config", "nuget.config", "Directory.Build.props", "Directory.Build.targets",
        "Directory.Packages.props", "Directory.Solution.props", "Directory.Solution.targets", ".editorconfig", ".globalconfig",
    ];

    private readonly IReadOnlyDictionary<string, Input> _inputs;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<TextInput>> _textInputs;
    private readonly IReadOnlySet<string> _excludedOutputs;
    private readonly string _projectSettings;

    private WorkspaceInputManifest(
        string repositoryRoot,
        string targetPath,
        IReadOnlyList<string> discoveryRoots,
        IReadOnlyList<string> explicitInputs,
        IReadOnlyDictionary<string, IReadOnlyList<TextInput>> textInputs,
        IReadOnlyDictionary<string, Input> inputs,
        IReadOnlySet<string>? excludedOutputs = null,
        string projectSettings = "")
    {
        RepositoryRoot = repositoryRoot;
        TargetPath = targetPath;
        DiscoveryRoots = discoveryRoots;
        ExplicitInputs = explicitInputs;
        _textInputs = textInputs;
        _inputs = inputs;
        _excludedOutputs = excludedOutputs ?? new HashSet<string>(PathComparer);
        _projectSettings = projectSettings;
        Fingerprint = ComputeFingerprint(targetPath, projectSettings, inputs);
    }

    internal string RepositoryRoot { get; }

    internal string TargetPath { get; }

    internal IReadOnlyList<string> DiscoveryRoots { get; }

    internal IReadOnlyList<string> ExplicitInputs { get; }

    internal IEnumerable<string> Paths => _inputs.Keys;

    internal string Fingerprint { get; }

    internal static async Task<WorkspaceInputManifest> CaptureBeforeLoadAsync(
        string repositoryRoot,
        string? targetPath,
        IReadOnlyList<string> additionalInputs,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? excludedOutputs = null)
    {
        var roots = additionalInputs.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetDirectoryName(Path.GetFullPath(path))!)
            .Append(repositoryRoot)
            .Distinct(PathComparer)
            .ToArray();
        roots = roots.Where(root => !roots.Any(other => !PathComparer.Equals(root, other) && IsCandidateRootInput(other, root))).ToArray();
        var paths = new HashSet<string>(additionalInputs.Select(Path.GetFullPath), PathComparer);
        AddRepositoryControlInputs(paths, repositoryRoot);
        if (targetPath is not null && !Directory.Exists(targetPath))
        {
            paths.Add(targetPath);
        }

        foreach (var root in roots)
        {
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            {
                foreach (var name in AncestorInputNames)
                {
                    paths.Add(Path.Combine(directory.FullName, name));
                }
            }
        }

        return await new WorkspaceInputManifest(
            repositoryRoot,
            targetPath ?? repositoryRoot,
            roots,
            paths.Order(StringComparer.Ordinal).ToArray(),
            new Dictionary<string, IReadOnlyList<TextInput>>(PathComparer),
            new Dictionary<string, Input>(PathComparer),
            excludedOutputs).ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<WorkspaceInputManifest> CaptureAsync(
        RoslynWorkspaceLoader snapshot,
        IEnumerable<string> additionalInputs,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? excludedOutputs = null)
    {
        var explicitInputs = new HashSet<string>(PathComparer);
        var discoveryRoots = new HashSet<string>(PathComparer) { Path.GetFullPath(snapshot.RootPath) };
        var textInputs = new Dictionary<string, List<TextInput>>(PathComparer);
        AddRepositoryControlInputs(explicitInputs, snapshot.RootPath);
        AddPath(explicitInputs, File.Exists(snapshot.TargetPath) ? snapshot.TargetPath : null);

        foreach (var input in additionalInputs)
        {
            AddPath(explicitInputs, input);
        }

        foreach (var project in snapshot.Solution.Projects)
        {
            AddPath(explicitInputs, project.FilePath);
            if (project.FilePath is not null)
            {
                discoveryRoots.Add(Path.GetDirectoryName(Path.GetFullPath(project.FilePath))!);
            }

            foreach (var document in project.Documents)
            {
                AddDocument(document, TextInputKind.Source);
            }

            foreach (var document in project.AdditionalDocuments)
            {
                AddDocument(document, TextInputKind.Additional);
            }

            foreach (var document in project.AnalyzerConfigDocuments)
            {
                AddDocument(document, TextInputKind.AnalyzerConfig);
            }

            foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>())
            {
                AddPath(explicitInputs, reference.FilePath);
                if (reference.FilePath is not null)
                {
                    AddPath(explicitInputs, Path.ChangeExtension(reference.FilePath, ".xml"));
                }
            }

            foreach (var reference in project.AnalyzerReferences)
            {
                AddPath(explicitInputs, reference.FullPath);
            }
        }

        var roots = discoveryRoots
            .Where(candidate => !discoveryRoots.Any(other => !PathComparer.Equals(candidate, other) && IsCandidateRootInput(other, candidate)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (excludedOutputs is not null && explicitInputs.Any(excludedOutputs.Contains))
        {
            throw new InvalidOperationException("The search index output path is also an evaluated workspace input. Select a different index path.");
        }
        foreach (var root in roots)
        {
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            {
                foreach (var name in AncestorInputNames)
                {
                    explicitInputs.Add(Path.Combine(directory.FullName, name));
                }
            }
        }

        var manifest = new WorkspaceInputManifest(
            snapshot.RootPath,
            snapshot.TargetPath,
            roots,
            explicitInputs.Order(StringComparer.Ordinal).ToArray(),
            textInputs.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<TextInput>)pair.Value, PathComparer),
            new Dictionary<string, Input>(PathComparer),
            excludedOutputs,
            CaptureProjectSettings(snapshot));
        return await manifest.ReconcileAsync(cancellationToken).ConfigureAwait(false);

        void AddDocument(TextDocument document, TextInputKind kind)
        {
            if (string.IsNullOrWhiteSpace(document.FilePath))
            {
                return;
            }

            var path = Path.GetFullPath(document.FilePath);
            explicitInputs.Add(path);
            if (!IsCandidateRootInput(snapshot.RootPath, path))
            {
                discoveryRoots.Add(Path.GetDirectoryName(path)!);
            }

            if (!textInputs.TryGetValue(path, out var documents))
            {
                documents = [];
                textInputs.Add(path, documents);
            }

            documents.Add(new TextInput(document.Id, kind));
        }
    }

    internal async Task<WorkspaceInputManifest> ReconcileAsync(
        CancellationToken cancellationToken,
        IReadOnlySet<string>? excludedOutputs = null)
    {
        excludedOutputs ??= _excludedOutputs;
        var paths = new HashSet<string>(ExplicitInputs, PathComparer);
        foreach (var root in DiscoveryRoots)
        {
            foreach (var path in DiscoverFiles(root, cancellationToken))
            {
                if (!excludedOutputs.Contains(path))
                {
                    paths.Add(path);
                }
            }
        }

        var inputs = new Dictionary<string, Input>(PathComparer);
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            inputs[path] = await ReadInputAsync(path, cancellationToken).ConfigureAwait(false);
        }

        return new WorkspaceInputManifest(RepositoryRoot, TargetPath, DiscoveryRoots, ExplicitInputs, _textInputs, inputs, excludedOutputs, _projectSettings);
    }

    internal async Task<WorkspaceInputManifest> ReadChangedSourcesAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        var inputs = new Dictionary<string, Input>(_inputs, PathComparer);
        foreach (var path in paths.Distinct(PathComparer))
        {
            inputs[path] = await ReadInputAsync(path, cancellationToken).ConfigureAwait(false);
        }

        return WithInputs(inputs);
    }

    internal bool Contains(string path) => _inputs.ContainsKey(path);

    internal bool IsSource(string path)
    {
        return _textInputs.TryGetValue(path, out var documents)
            && documents.All(document => document.Kind == TextInputKind.Source)
            && !Path.GetRelativePath(RepositoryRoot, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    internal bool RequiresReplacement(WorkspaceInputManifest previous)
    {
        return ChangedPaths(previous).Any(path => !previous.IsSource(path)
            || !_inputs.TryGetValue(path, out var current)
            || !current.Exists
            || !previous._inputs.TryGetValue(path, out var old)
            || !old.Exists);
    }

    internal bool MatchesKnownInputs(WorkspaceInputManifest other)
    {
        return _inputs.All(pair => other._inputs.TryGetValue(pair.Key, out var input)
                ? pair.Value.Hash == input.Hash
                : !pair.Value.Exists)
            && other._inputs.All(pair => _inputs.ContainsKey(pair.Key) || !IsCandidate(pair.Key) || !pair.Value.Exists);
    }

    internal Solution FreezeText(Solution solution)
    {
        foreach (var (path, documents) in _textInputs)
        {
            if (!_inputs.TryGetValue(path, out var input) || input.Text is null)
            {
                continue;
            }

            foreach (var document in documents)
            {
                var current = document.Kind switch
                {
                    TextInputKind.Source => solution.GetDocument(document.Id),
                    TextInputKind.Additional => solution.GetAdditionalDocument(document.Id),
                    TextInputKind.AnalyzerConfig => solution.GetAnalyzerConfigDocument(document.Id),
                    _ => throw new InvalidOperationException("Unknown workspace text input kind."),
                };
                if (current is not null && current.TryGetText(out var text) && ReferenceEquals(text, input.Text))
                {
                    continue;
                }

                solution = document.Kind switch
                {
                    TextInputKind.Source => solution.WithDocumentText(document.Id, input.Text, PreservationMode.PreserveIdentity),
                    TextInputKind.Additional => solution.WithAdditionalDocumentText(document.Id, input.Text, PreservationMode.PreserveIdentity),
                    TextInputKind.AnalyzerConfig => solution.WithAnalyzerConfigDocumentText(document.Id, input.Text, PreservationMode.PreserveIdentity),
                    _ => throw new InvalidOperationException("Unknown workspace text input kind."),
                };
            }
        }

        return solution;
    }

    internal async Task<Solution?> FreezeMetadataAsync(Solution solution, CancellationToken cancellationToken)
    {
        var references = new Dictionary<string, PortableExecutableReference>(PathComparer);
        foreach (var project in solution.Projects.ToArray())
        {
            var pinned = new List<MetadataReference>();
            foreach (var reference in project.MetadataReferences)
            {
                if (reference is not PortableExecutableReference { FilePath: { } path } portable)
                {
                    pinned.Add(reference);
                    continue;
                }

                path = Path.GetFullPath(path);
                if (!references.TryGetValue(path, out var captured))
                {
                    var contents = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                    if (!MatchesContent(path, contents))
                    {
                        return null;
                    }

                    DocumentationProvider documentation = DocumentationProvider.Default;
                    var documentationPath = Path.ChangeExtension(path, ".xml");
                    if (_inputs.TryGetValue(documentationPath, out var documentationInput) && documentationInput.Exists)
                    {
                        var documentationContents = await File.ReadAllBytesAsync(documentationPath, cancellationToken).ConfigureAwait(false);
                        if (!MatchesContent(documentationPath, documentationContents))
                        {
                            return null;
                        }

                        documentation = XmlDocumentationProvider.CreateFromBytes(documentationContents);
                    }

                    captured = MetadataReference.CreateFromImage(
                        ImmutableCollectionsMarshal.AsImmutableArray(contents), portable.Properties, documentation, path);
                    references.Add(path, captured);
                }

                pinned.Add(captured.WithProperties(portable.Properties));
            }

            solution = solution.WithProjectMetadataReferences(project.Id, pinned);
        }

        return solution;
    }

    internal bool IsCandidate(string path)
    {
        return !_excludedOutputs.Contains(path)
            && (Contains(path) || DiscoveryRoots.Any(root => IsCandidateRootInput(root, path)));
    }

    internal static bool IsCandidateRootInput(string root, string path)
    {
        return IsWithin(path, root) && !Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(ExcludedDirectories.Contains);
    }

    internal static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private IEnumerable<string> ChangedPaths(WorkspaceInputManifest previous)
    {
        return _inputs.Keys.Concat(previous._inputs.Keys).Distinct(PathComparer)
            .Where(path => !_inputs.TryGetValue(path, out var current)
                || !previous._inputs.TryGetValue(path, out var old)
                || current.Hash != old.Hash);
    }

    private WorkspaceInputManifest WithInputs(IReadOnlyDictionary<string, Input> inputs)
    {
        return new WorkspaceInputManifest(RepositoryRoot, TargetPath, DiscoveryRoots, ExplicitInputs, _textInputs, inputs, _excludedOutputs, _projectSettings);
    }

    private bool MatchesContent(string path, byte[] contents)
    {
        return _inputs.TryGetValue(path, out var input) && input.Hash == Convert.ToHexString(SHA256.HashData(contents));
    }

    private async Task<Input> ReadInputAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!_textInputs.ContainsKey(path))
            {
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                var hashString = Convert.ToHexString(hash);
                return _inputs.TryGetValue(path, out var existing) && existing.Hash == hashString
                    ? existing
                    : new Input(hashString, true, null);
            }

            using var contents = new MemoryStream();
            await stream.CopyToAsync(contents, cancellationToken).ConfigureAwait(false);
            var contentHash = Convert.ToHexString(SHA256.HashData(contents.GetBuffer().AsSpan(0, checked((int)contents.Length))));
            if (_inputs.TryGetValue(path, out var previous) && previous.Hash == contentHash)
            {
                return previous;
            }

            contents.Position = 0;
            var text = SourceText.From(contents, encoding: null, checksumAlgorithm: SourceHashAlgorithm.Sha256);
            if (previous?.Text is { } previousText && Equals(previousText.Encoding, text.Encoding))
            {
                text = ApplyTextChange(previousText, text);
            }

            return new Input(contentHash, true, text);
        }
        catch (FileNotFoundException)
        {
            return new Input("missing", false, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new Input("missing", false, null);
        }
    }

    private static SourceText ApplyTextChange(SourceText previous, SourceText current)
    {
        var prefix = 0;
        var commonLength = Math.Min(previous.Length, current.Length);
        while (prefix < commonLength && previous[prefix] == current[prefix])
        {
            prefix++;
        }

        var previousEnd = previous.Length;
        var currentEnd = current.Length;
        while (previousEnd > prefix && currentEnd > prefix && previous[previousEnd - 1] == current[currentEnd - 1])
        {
            previousEnd--;
            currentEnd--;
        }

        return previous.WithChanges(new TextChange(TextSpan.FromBounds(prefix, previousEnd), current.ToString(TextSpan.FromBounds(prefix, currentEnd))));
    }

    private static IEnumerable<string> DiscoverFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    if (!ExcludedDirectories.Contains(entry.Name))
                    {
                        pending.Push(entry.FullName);
                    }
                }
                else
                {
                    yield return entry.FullName;
                }
            }
        }
    }

    private static string CaptureProjectSettings(RoslynWorkspaceLoader snapshot)
    {
        return JsonSerializer.Serialize(snapshot.Solution.Projects
            .OrderBy(project => project.FilePath, StringComparer.Ordinal)
            .ThenBy(project => project.Name, StringComparer.Ordinal)
            .Select(project =>
            {
                var parse = project.ParseOptions as CSharpParseOptions;
                var compilation = project.CompilationOptions as CSharpCompilationOptions;
                return new
                {
                    project.FilePath,
                    project.Name,
                    project.AssemblyName,
                    project.DefaultNamespace,
                    Framework = snapshot.GetTargetFramework(project),
                    Parse = parse is null ? null : new
                    {
                        parse.LanguageVersion,
                        parse.Kind,
                        parse.DocumentationMode,
                        Symbols = parse.PreprocessorSymbolNames.Order(StringComparer.Ordinal).ToArray(),
                        Features = parse.Features.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
                    },
                    Compilation = compilation is null ? null : new
                    {
                        compilation.OutputKind,
                        compilation.ModuleName,
                        compilation.MainTypeName,
                        compilation.ScriptClassName,
                        compilation.OptimizationLevel,
                        compilation.CheckOverflow,
                        compilation.Platform,
                        compilation.AllowUnsafe,
                        compilation.NullableContextOptions,
                        compilation.MetadataImportOptions,
                        compilation.GeneralDiagnosticOption,
                        compilation.WarningLevel,
                        compilation.ReportSuppressedDiagnostics,
                        compilation.PublicSign,
                        compilation.DelaySign,
                        compilation.CryptoKeyFile,
                        compilation.CryptoKeyContainer,
                        compilation.Deterministic,
                        Usings = compilation.Usings.Order(StringComparer.Ordinal).ToArray(),
                        Diagnostics = compilation.SpecificDiagnosticOptions.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
                    },
                    Documents = project.Documents.Select(document => document.FilePath).Order(StringComparer.Ordinal).ToArray(),
                    Additional = project.AdditionalDocuments.Select(document => document.FilePath).Order(StringComparer.Ordinal).ToArray(),
                    Configuration = project.AnalyzerConfigDocuments.Select(document => document.FilePath).Order(StringComparer.Ordinal).ToArray(),
                    References = project.MetadataReferences.OrderBy(reference => reference.Display, StringComparer.Ordinal).Select(reference => new
                    {
                        reference.Display,
                        reference.Properties.Kind,
                        reference.Properties.EmbedInteropTypes,
                        Aliases = reference.Properties.Aliases.Order(StringComparer.Ordinal).ToArray(),
                    }).ToArray(),
                    ProjectReferences = project.ProjectReferences.Select(reference => new
                    {
                        Path = project.Solution.GetProject(reference.ProjectId)?.FilePath,
                        reference.EmbedInteropTypes,
                        Aliases = reference.Aliases.Order(StringComparer.Ordinal).ToArray(),
                    }).OrderBy(reference => reference.Path, StringComparer.Ordinal).ToArray(),
                    Analyzers = project.AnalyzerReferences.Select(reference => reference.FullPath).Order(StringComparer.Ordinal).ToArray(),
                };
            }).ToArray());
    }

    private static string ComputeFingerprint(string targetPath, string projectSettings, IReadOnlyDictionary<string, Input> inputs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var assembly = typeof(RoslynWorkspaceLoader).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString();
        hash.AppendData(Encoding.UTF8.GetBytes($"workspace-input-v2\0{version}\0{projectSettings}\0"));
        hash.AppendData(Encoding.UTF8.GetBytes(targetPath));
        foreach (var (path, input) in inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(input.Hash));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AddPath(ISet<string> paths, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            paths.Add(Path.GetFullPath(path));
        }
    }

    private static void AddRepositoryControlInputs(ISet<string> paths, string repositoryRoot)
    {
        var gitDirectory = Path.Combine(repositoryRoot, ".git");
        if (Directory.Exists(gitDirectory))
        {
            paths.Add(Path.Combine(gitDirectory, "index"));
            paths.Add(Path.Combine(gitDirectory, "config"));
            paths.Add(Path.Combine(gitDirectory, "info", "exclude"));
        }
    }

    private sealed record Input(string Hash, bool Exists, SourceText? Text);

    private sealed record TextInput(DocumentId Id, TextInputKind Kind);

    private enum TextInputKind
    {
        Source,
        Additional,
        AnalyzerConfig,
    }
}
