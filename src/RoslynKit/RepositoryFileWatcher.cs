namespace RoslynKit;

/// <summary>
/// Watches repository inputs and bounded external directories, leaving complete recovery to reconciliation.
/// </summary>
internal sealed class RepositoryFileWatcher : IDisposable
{
    private const int MaximumExternalWatchers = 128;
    private readonly object _gate = new();
    private readonly string _repositoryRoot;
    private readonly Action<string, WatcherChangeTypes> _changed;
    private readonly Action _overflow;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(WorkspaceInputManifest.PathComparer);
    private HashSet<string> _externalInputs = new(WorkspaceInputManifest.PathComparer);
    private HashSet<string> _knownInputs = new(WorkspaceInputManifest.PathComparer);
    private HashSet<string> _discoveryRoots = new(WorkspaceInputManifest.PathComparer);
    private bool _disposed;

    internal RepositoryFileWatcher(
        string repositoryRoot,
        Action<string, WatcherChangeTypes> changed,
        Action overflow)
    {
        _repositoryRoot = repositoryRoot;
        _changed = changed;
        _overflow = overflow;
        _discoveryRoots.Add(repositoryRoot);
        AddWatcher(repositoryRoot, recursive: true);
    }

    internal void UpdateInputs(WorkspaceInputManifest manifest)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _knownInputs = manifest.Paths.ToHashSet(WorkspaceInputManifest.PathComparer);
            _externalInputs = _knownInputs.Where(path => !WorkspaceInputManifest.IsWithin(path, _repositoryRoot))
                .ToHashSet(WorkspaceInputManifest.PathComparer);
            _discoveryRoots = manifest.DiscoveryRoots.ToHashSet(WorkspaceInputManifest.PathComparer);
            var directories = new Dictionary<string, bool>(WorkspaceInputManifest.PathComparer)
            {
                [_repositoryRoot] = true,
            };
            foreach (var root in manifest.DiscoveryRoots)
            {
                directories[root] = true;
            }

            foreach (var directory in _externalInputs.Select(Path.GetDirectoryName)
                         .OfType<string>()
                         .Distinct(WorkspaceInputManifest.PathComparer)
                         .Order(StringComparer.Ordinal)
                         .Take(MaximumExternalWatchers))
            {
                directories.TryAdd(directory, false);
            }

            foreach (var directory in _watchers.Keys.Where(directory => !directories.ContainsKey(directory)).ToArray())
            {
                _watchers.Remove(directory, out var watcher);
                watcher!.Dispose();
            }

            foreach (var (directory, recursive) in directories)
            {
                if (_watchers.TryGetValue(directory, out var existing))
                {
                    existing.IncludeSubdirectories = recursive;
                }
                else
                {
                    AddWatcher(directory, recursive);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }
    }

    private void AddWatcher(string directory, bool recursive)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 32768,
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += (_, _) => _overflow();
            watcher.EnableRaisingEvents = true;
            _watchers.Add(directory, watcher);
        }
        catch (IOException)
        {
            // Reconciliation still covers directories whose operating-system watch limit was reached.
        }
        catch (UnauthorizedAccessException)
        {
            // Explicit input reads during reconciliation report inaccessible semantic inputs.
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        if (ShouldForward(args.FullPath))
        {
            _changed(args.FullPath, args.ChangeType);
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        if (ShouldForward(args.OldFullPath))
        {
            _changed(args.OldFullPath, WatcherChangeTypes.Renamed);
        }

        if (ShouldForward(args.FullPath))
        {
            _changed(args.FullPath, WatcherChangeTypes.Renamed);
        }
    }

    private bool ShouldForward(string path)
    {
        lock (_gate)
        {
            return !_disposed && (_knownInputs.Contains(path)
                || _discoveryRoots.Any(root => WorkspaceInputManifest.IsCandidateRootInput(root, path)));
        }
    }
}
