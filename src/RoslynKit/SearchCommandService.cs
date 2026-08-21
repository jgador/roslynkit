using System.Text.Json;

using Microsoft.CodeAnalysis;

namespace RoslynKit;

/// <summary>
/// Coordinates repository validation, workspace corpus refreshes, and persistent ranked symbol searches.
/// </summary>
internal static class SearchCommandService
{
    private static readonly TimeSpan WorkspaceReloadDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan IndexWriterWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InitialSearchWriterPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Loads a standalone workspace only when before/after repository fingerprints identify the same source state.
    /// </summary>
    public static async Task<RoslynWorkspaceLoader> LoadStableWorkspaceAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        var context = await ResolveContextAsync(command, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = await CaptureFingerprintAsync(command.Name, context, cancellationToken).ConfigureAwait(false);
            var loaded = command.Flag("text-only")
                ? await RoslynWorkspaceLoader.LoadTextOnlyAsync(command.Required("target"), cancellationToken).ConfigureAwait(false)
                : await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
            var after = await CaptureFingerprintAsync(command.Name, context, cancellationToken).ConfigureAwait(false);
            if (string.Equals(before.Value, after.Value, StringComparison.Ordinal))
            {
                loaded.SetLoadedWorktreeFingerprint(after.WorktreeFingerprint);
                return loaded;
            }

            loaded.Dispose();
            if (attempt == 0)
            {
                await Task.Delay(WorkspaceReloadDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            "The repository changed while RoslynKit loaded the workspace. Retry after edits settle.");
    }

    /// <summary>
    /// Strictly prepares the selected target partition and reports whether records changed.
    /// </summary>
    public static async Task<IndexResult> IndexAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var context = await ResolveContextAsync(command, cancellationToken).ConfigureAwait(false);
        ValidateSingleTargetFrameworkProjects(command.Name, loaded.Solution);

        SqliteSearchIndexWriterLease lease;
        try
        {
            lease = await context.Index.AcquireWriterLeaseAsync(IndexWriterWait, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteSearchIndexWriterLeaseUnavailableException exception)
        {
            throw new InvalidOperationException(
                $"Could not acquire the search-index writer within {IndexWriterWait.TotalSeconds:0} seconds. Another index refresh is still running; retry after it completes.",
                exception);
        }

        await using (lease)
        {
            var fingerprint = await CaptureFingerprintAsync(command.Name, context, cancellationToken).ConfigureAwait(false);
            EnsureWorkspaceMatches(command.Name, context, loaded, fingerprint);
            var metadata = await lease.ReadMetadataAsync(context.TargetIdentity, cancellationToken).ConfigureAwait(false);
            var requestedFingerprint = StoredFingerprint.Create(fingerprint);
            var forceRebuild = command.Flag("rebuild");
            if (!forceRebuild && FingerprintMatches(metadata, requestedFingerprint))
            {
                return new IndexResult(
                    loaded.TargetPath,
                    context.Path.DatabasePath,
                    SearchIndexState.Fresh,
                    metadata!.SymbolCount,
                    Rebuilt: false,
                    loaded.WorkspaceDiagnostics);
            }

            var refreshed = await RefreshAsync(
                command.Name,
                context,
                loaded,
                lease,
                fingerprint,
                metadata,
                forceFullRebuild: forceRebuild,
                cancellationToken).ConfigureAwait(false);
            return new IndexResult(
                loaded.TargetPath,
                context.Path.DatabasePath,
                SearchIndexState.Fresh,
                refreshed.SymbolCount,
                Rebuilt: forceRebuild,
                loaded.WorkspaceDiagnostics);
        }
    }

    /// <summary>
    /// Refreshes stale content when possible and returns bounded ranked symbol matches.
    /// </summary>
    public static async Task<SearchResult> SearchAsync(
        ParsedCommand command,
        RoslynWorkspaceLoader loaded,
        CancellationToken cancellationToken)
    {
        var context = await ResolveContextAsync(command, cancellationToken).ConfigureAwait(false);
        ValidateSingleTargetFrameworkProjects(command.Name, loaded.Solution);

        var query = command.Required("query");
        var queryTokens = SearchQueryTokenizer.TokenizeQuery(query);
        var maxResults = command.OptionalInt("max-results", 20, 1);
        var compact = command.Flag("compact");
        var balanced = command.Flag("balanced");
        var projectPaths = ResolveProjectFilter(command, context.Path.RepositoryRoot, loaded.Solution);
        var kinds = ResolveKindFilter(command.Name, command.Optional("kind"));
        var fingerprint = await CaptureFingerprintAsync(command.Name, context, cancellationToken).ConfigureAwait(false);
        EnsureWorkspaceMatches(command.Name, context, loaded, fingerprint);
        var metadata = await context.Index.ReadMetadataAsync(context.TargetIdentity, cancellationToken).ConfigureAwait(false);
        var requestedFingerprint = StoredFingerprint.Create(fingerprint);

        if (!FingerprintMatches(metadata, requestedFingerprint))
        {
            var mustWaitForCompatibleIndex = metadata is null
                || StoredFingerprint.TryParse(metadata.Fingerprint) is null;
            var lease = mustWaitForCompatibleIndex
                ? await WaitForWriterLeaseAsync(context.Index, cancellationToken).ConfigureAwait(false)
                : await TryAcquireWriterLeaseAsync(context.Index, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);

            if (lease is not null)
            {
                await using (lease)
                {
                    fingerprint = await CaptureFingerprintAsync(command.Name, context, cancellationToken).ConfigureAwait(false);
                    EnsureWorkspaceMatches(command.Name, context, loaded, fingerprint);
                    requestedFingerprint = StoredFingerprint.Create(fingerprint);
                    metadata = await lease.ReadMetadataAsync(context.TargetIdentity, cancellationToken).ConfigureAwait(false);
                    if (!FingerprintMatches(metadata, requestedFingerprint))
                    {
                        metadata = await RefreshAsync(
                            command.Name,
                            context,
                            loaded,
                            lease,
                            fingerprint,
                            metadata,
                            forceFullRebuild: false,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        var searchLimit = balanced
            ? (int)Math.Min((long)maxResults * 4, int.MaxValue)
            : maxResults;
        var searchSnapshot = await context.Index.ReadSearchSnapshotAsync(
            new SqliteSearchIndexQuery(
                context.TargetIdentity,
                queryTokens,
                projectPaths,
                kinds,
                searchLimit),
            cancellationToken).ConfigureAwait(false);
        var snapshotFingerprint = StoredFingerprint.TryParse(searchSnapshot.Metadata?.Fingerprint);
        var snapshotIsCompatible = snapshotFingerprint is not null;
        var indexState = snapshotIsCompatible
            && FingerprintMatches(searchSnapshot.Metadata, requestedFingerprint)
                ? SearchIndexState.Fresh
                : SearchIndexState.Stale;
        var search = snapshotIsCompatible
            ? searchSnapshot.SearchResult
            : new SqliteSearchIndexSearchResult(0, []);
        var selectedMatches = balanced
            ? SelectBalancedMatches(search.Matches, maxResults)
            : search.Matches;
        var hits = selectedMatches
            .Select(match => new SearchHit(
                match.DisplayName,
                match.Kind,
                new SourceRange(
                    compact ? match.Path.Value : match.Path.Resolve(context.Path.RepositoryRoot),
                    match.Line,
                    match.Column,
                    match.EndLine,
                    match.EndColumn),
                match.SymbolId,
                match.Excerpt)
            {
                ExcerptSource = match.ExcerptSource,
            })
            .ToArray();

        return new SearchResult(
            loaded.TargetPath,
            context.Path.DatabasePath,
            query,
            indexState,
            search.TotalMatchCount,
            hits.Length,
            search.TotalMatchCount > hits.Length,
            hits,
            loaded.WorkspaceDiagnostics,
            compact);
    }

    private static IReadOnlyList<SqliteSearchIndexMatch> SelectBalancedMatches(
        IReadOnlyList<SqliteSearchIndexMatch> matches,
        int maxResults)
    {
        if (matches.Count <= maxResults)
        {
            return matches;
        }

        var testQuota = Math.Max(1, maxResults / 2);
        var productionQuota = maxResults - testQuota;
        var selectedKeys = matches
            .Where(match => !IsTestPath(match.Path.Value))
            .Take(productionQuota)
            .Concat(matches.Where(match => IsTestPath(match.Path.Value)).Take(testQuota))
            .Select(match => match.SymbolKey)
            .ToHashSet(StringComparer.Ordinal);

        if (selectedKeys.Count < maxResults)
        {
            foreach (var match in matches)
            {
                selectedKeys.Add(match.SymbolKey);
                if (selectedKeys.Count == maxResults)
                {
                    break;
                }
            }
        }

        return matches
            .Where(match => selectedKeys.Contains(match.SymbolKey))
            .Take(maxResults)
            .ToArray();
    }

    private static bool IsTestPath(string path)
    {
        return path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/tests/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<SearchCommandContext> ResolveContextAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        var resolution = await new SearchIndexPathPolicy().ResolveAsync(
            command.Required("target"),
            command.Required("index-path"),
            cancellationToken).ConfigureAwait(false);
        if (!resolution.IsSuccessful)
        {
            throw new CliUsageException(command.Name, resolution.Diagnostic ?? "Search index path validation failed.");
        }

        var path = resolution.Path!;
        var targetIdentity = RepositoryRelativePath.FromPhysicalPath(
            path.RepositoryRoot,
            path.TargetPath,
            "Search target");
        if (command.Flag("text-only"))
        {
            targetIdentity = RepositoryRelativePath.FromStoredValue(
                $"__text__/{targetIdentity.Value}",
                "Text-only search target");
        }
        return new SearchCommandContext(
            path,
            targetIdentity,
            new SearchIndexFingerprintService(path),
            new SqliteSearchIndex(path.DatabasePath));
    }

    private static async Task<SqliteSearchIndexMetadata> RefreshAsync(
        string commandName,
        SearchCommandContext context,
        RoslynWorkspaceLoader loaded,
        SqliteSearchIndexWriterLease lease,
        SearchIndexFingerprint fingerprint,
        SqliteSearchIndexMetadata? existingMetadata,
        bool forceFullRebuild,
        CancellationToken cancellationToken)
    {
        var plan = await CreateRefreshPlanAsync(
            context,
            loaded.Solution,
            existingMetadata,
            fingerprint,
            forceFullRebuild,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RoslynSearchCorpusRecord> records = [];

        if (plan.Kind == SearchIndexRefreshKind.Projects)
        {
            var incrementalRecords = new List<RoslynSearchCorpusRecord>();
            foreach (var projectPath in plan.ProjectPaths)
            {
                var build = await BuildCorpusAsync(
                    commandName,
                    context,
                    loaded.Solution,
                    projectPath,
                    cancellationToken).ConfigureAwait(false);
                incrementalRecords.AddRange(build);
            }

            records = incrementalRecords;
        }
        else if (plan.Kind == SearchIndexRefreshKind.Full)
        {
            records = await BuildCorpusAsync(
                commandName,
                context,
                loaded.Solution,
                projectSelector: null,
                cancellationToken).ConfigureAwait(false);
        }

        var verifiedFingerprint = await CaptureFingerprintAsync(commandName, context, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(fingerprint.Value, verifiedFingerprint.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The repository changed while RoslynKit was building the search index. No partial refresh was published; retry after edits settle.");
        }

        var target = new SqliteSearchIndexTarget(
            context.TargetIdentity,
            StoredFingerprint.Create(verifiedFingerprint).Serialize());
        var symbols = records.Select(record => record.ToSqliteSymbol()).ToArray();
        if (plan.Kind == SearchIndexRefreshKind.Projects)
        {
            await lease.ReplaceProjectsAsync(target, plan.ProjectPaths, symbols, cancellationToken).ConfigureAwait(false);
        }
        else if (plan.Kind == SearchIndexRefreshKind.Full)
        {
            await lease.ReplaceTargetAsync(target, symbols, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await lease.UpdateTargetMetadataAsync(target, cancellationToken).ConfigureAwait(false);
        }

        await lease.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await context.Index.ReadMetadataAsync(context.TargetIdentity, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The search index refresh completed without persistent target metadata.");
    }

    private static async Task<IReadOnlyList<RoslynSearchCorpusRecord>> BuildCorpusAsync(
        string commandName,
        SearchCommandContext context,
        Solution solution,
        RepositoryRelativePath? projectSelector,
        CancellationToken cancellationToken)
    {
        var build = await new RoslynSearchCorpusBuilder().BuildAsync(
            solution,
            new RoslynSearchCorpusBuildOptions(
                context.Path.RepositoryRoot,
                context.TargetIdentity,
                projectSelector),
            cancellationToken).ConfigureAwait(false);
        if (build.Issues.Count > 0)
        {
            throw new CliUsageException(
                commandName,
                string.Join(" ", build.Issues.Select(issue => issue.Message)));
        }

        return build.Records;
    }

    private static async Task<SearchIndexRefreshPlan> CreateRefreshPlanAsync(
        SearchCommandContext context,
        Solution solution,
        SqliteSearchIndexMetadata? existingMetadata,
        SearchIndexFingerprint current,
        bool forceFullRebuild,
        CancellationToken cancellationToken)
    {
        var stored = StoredFingerprint.TryParse(existingMetadata?.Fingerprint);
        if (forceFullRebuild
            || existingMetadata is null
            || stored is null)
        {
            return SearchIndexRefreshPlan.Full;
        }

        var changedPaths = new HashSet<string>(stored.ChangedPaths, StringComparer.Ordinal);
        changedPaths.UnionWith(current.ChangedPaths);
        var requiresFullRebuild = stored.RequiresFullRebuild || current.RequiresFullRebuild;
        if (!string.Equals(stored.HeadCommit, current.HeadCommit, StringComparison.Ordinal))
        {
            var committedChanges = await context.FingerprintService.ListChangedPathsAsync(
                stored.HeadCommit,
                current.HeadCommit,
                cancellationToken).ConfigureAwait(false);
            if (!committedChanges.IsSuccessful)
            {
                return SearchIndexRefreshPlan.Full;
            }

            changedPaths.UnionWith(committedChanges.Changes!.Paths);
            requiresFullRebuild |= committedChanges.Changes.RequiresFullRebuild;
        }

        if (requiresFullRebuild)
        {
            return SearchIndexRefreshPlan.Full;
        }

        var changedSourcePaths = changedPaths
            .Where(IsCSharpSourcePath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (changedSourcePaths.Length == 0)
        {
            return SearchIndexRefreshPlan.MetadataOnly;
        }

        var affectedProjectPaths = ResolveIncrementalProjects(context, solution, changedSourcePaths);
        return affectedProjectPaths is null
            ? SearchIndexRefreshPlan.Full
            : SearchIndexRefreshPlan.ForProjects(affectedProjectPaths);
    }

    private static IReadOnlyList<RepositoryRelativePath>? ResolveIncrementalProjects(
        SearchCommandContext context,
        Solution solution,
        IReadOnlyList<string> changedSourcePaths)
    {

        var affected = new HashSet<RepositoryRelativePath>();
        foreach (var relativePath in changedSourcePaths)
        {
            var fullPath = Path.GetFullPath(relativePath, context.Path.RepositoryRoot);
            var matches = solution.Projects
                .Where(project => project.FilePath is not null)
                .Where(project => project.Documents.Any(document => PathsEqual(document.FilePath, fullPath)))
                .Select(project => Path.GetFullPath(project.FilePath!))
                .ToArray();
            if (matches.Length == 0)
            {
                matches = solution.Projects
                    .Where(project => project.FilePath is not null)
                    .Select(project => Path.GetFullPath(project.FilePath!))
                    .Where(projectPath => IsUnderDirectory(fullPath, Path.GetDirectoryName(projectPath)!))
                    .OrderByDescending(projectPath => Path.GetDirectoryName(projectPath)!.Length)
                    .Take(1)
                    .ToArray();
            }

            if (matches.Length == 0)
            {
                return null;
            }

            foreach (var match in matches)
            {
                affected.Add(RepositoryRelativePath.FromPhysicalPath(
                    context.Path.RepositoryRoot,
                    match,
                    "Loaded project"));
            }
        }

        return affected.OrderBy(path => path.Value, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyCollection<RepositoryRelativePath>? ResolveProjectFilter(
        ParsedCommand command,
        string repositoryRoot,
        Solution solution)
    {
        var selector = command.Optional("project");
        if (selector is null)
        {
            return null;
        }

        var fullSelector = Path.GetFullPath(selector);
        var matches = solution.Projects
            .Where(project => project.FilePath is not null && PathsEqual(project.FilePath, fullSelector))
            .Select(project => RepositoryRelativePath.FromPhysicalPath(
                repositoryRoot,
                project.FilePath,
                $"Project '{project.Name}'"))
            .Distinct()
            .ToArray();
        return matches.Length switch
        {
            1 => matches,
            0 => throw new CliUsageException(command.Name, $"Project '{fullSelector}' is not part of the loaded target."),
            _ => throw new CliUsageException(command.Name, $"Project '{fullSelector}' has multiple target-framework contexts. Search supports one target framework per project."),
        };
    }

    private static IReadOnlyCollection<string>? ResolveKindFilter(string commandName, string? kind)
    {
        return kind switch
        {
            null => null,
            "namespace" => ["namespace"],
            "type" => ["class", "interface", "struct", "enum", "delegate"],
            "member" => ["method", "property", "field", "event"],
            "method" or "property" or "field" or "event" or "class" or "interface" or "struct" or "enum" or "delegate" => [kind],
            _ => throw new CliUsageException(commandName, $"Unknown symbol kind '{kind}'. Supported values: {string.Join(", ", SupportedKinds)}."),
        };
    }

    private static async Task<SearchIndexFingerprint> CaptureFingerprintAsync(
        string commandName,
        SearchCommandContext context,
        CancellationToken cancellationToken)
    {
        var resolution = await context.FingerprintService.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (!resolution.IsSuccessful)
        {
            throw new InvalidOperationException(
                $"Could not capture a stable repository state for '{commandName}': {resolution.Diagnostic}");
        }

        return resolution.Fingerprint!;
    }

    private static void EnsureWorkspaceMatches(
        string commandName,
        SearchCommandContext context,
        RoslynWorkspaceLoader loaded,
        SearchIndexFingerprint currentFingerprint)
    {
        if (loaded.LoadedWorktreeFingerprint is null)
        {
            throw new InvalidOperationException(
                $"The loaded workspace for '{commandName}' is not associated with a stable repository fingerprint. Retry after edits settle.");
        }

        var workspaceFingerprint = context.FingerprintService.FromWorktreeFingerprint(loaded.LoadedWorktreeFingerprint);
        if (!string.Equals(workspaceFingerprint.Value, currentFingerprint.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The repository changed after RoslynKit loaded the workspace for '{commandName}'. No search-index records were published; retry after edits settle.");
        }
    }

    private static bool FingerprintMatches(
        SqliteSearchIndexMetadata? metadata,
        StoredFingerprint requested)
    {
        var stored = StoredFingerprint.TryParse(metadata?.Fingerprint);
        return stored is not null
            && string.Equals(stored.Value, requested.Value, StringComparison.Ordinal)
            && string.Equals(stored.HeadCommit, requested.HeadCommit, StringComparison.Ordinal);
    }

    private static void ValidateSingleTargetFrameworkProjects(string commandName, Solution solution)
    {
        var duplicates = solution.Projects
            .Where(project => string.Equals(project.Language, LanguageNames.CSharp, StringComparison.Ordinal))
            .Where(project => !string.IsNullOrWhiteSpace(project.FilePath))
            .GroupBy(project => Path.GetFullPath(project.FilePath!), PathComparer)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null)
        {
            throw new CliUsageException(
                commandName,
                $"Project '{duplicates.Key}' has multiple target-framework contexts. Search indexing supports one target framework per project.");
        }
    }

    private static async Task<SqliteSearchIndexWriterLease?> TryAcquireWriterLeaseAsync(
        SqliteSearchIndex index,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await index.AcquireWriterLeaseAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteSearchIndexWriterLeaseUnavailableException)
        {
            return null;
        }
    }

    private static async Task<SqliteSearchIndexWriterLease> WaitForWriterLeaseAsync(
        SqliteSearchIndex index,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var lease = await TryAcquireWriterLeaseAsync(
                index,
                InitialSearchWriterPoll,
                cancellationToken).ConfigureAwait(false);
            if (lease is not null)
            {
                return lease;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static bool IsCSharpSourcePath(string path)
    {
        return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string? left, string right)
    {
        return left is not null && PathComparer.Equals(Path.GetFullPath(left), Path.GetFullPath(right));
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static IReadOnlyList<string> SupportedKinds { get; } =
    [
        "namespace",
        "type",
        "member",
        "method",
        "property",
        "field",
        "event",
        "class",
        "interface",
        "struct",
        "enum",
        "delegate",
    ];

    private sealed record SearchCommandContext(
        SearchIndexPath Path,
        RepositoryRelativePath TargetIdentity,
        SearchIndexFingerprintService FingerprintService,
        SqliteSearchIndex Index);

    private sealed record StoredFingerprint(
        string Value,
        string HeadCommit,
        bool RequiresFullRebuild,
        IReadOnlyList<string> ChangedPaths)
    {
        public static StoredFingerprint Create(SearchIndexFingerprint fingerprint)
        {
            return new StoredFingerprint(
                fingerprint.Value,
                fingerprint.HeadCommit,
                fingerprint.RequiresFullRebuild,
                fingerprint.ChangedPaths.Order(StringComparer.Ordinal).ToArray());
        }

        public string Serialize()
        {
            return JsonSerializer.Serialize(this);
        }

        public static StoredFingerprint? TryParse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<StoredFingerprint>(value);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private enum SearchIndexRefreshKind
    {
        MetadataOnly,
        Projects,
        Full,
    }

    private sealed record SearchIndexRefreshPlan(
        SearchIndexRefreshKind Kind,
        IReadOnlyList<RepositoryRelativePath> ProjectPaths)
    {
        public static SearchIndexRefreshPlan MetadataOnly { get; } = new(SearchIndexRefreshKind.MetadataOnly, []);

        public static SearchIndexRefreshPlan Full { get; } = new(SearchIndexRefreshKind.Full, []);

        public static SearchIndexRefreshPlan ForProjects(IReadOnlyList<RepositoryRelativePath> projectPaths)
        {
            return new SearchIndexRefreshPlan(SearchIndexRefreshKind.Projects, projectPaths);
        }
    }
}
