namespace RoslynKit;

/// <summary>
/// Builds and queries the shared search corpus from one maintained native-preview snapshot.
/// </summary>
internal static class TypeScriptSearchCommandService
{
    private static readonly TimeSpan IndexWriterWait = TimeSpan.FromSeconds(5);

    public static async Task<object> ExecuteAsync(
        ParsedCommand command,
        TypeScriptBridgeClient bridge,
        CancellationToken cancellationToken)
    {
        var context = await ResolveContextAsync(command, cancellationToken).ConfigureAwait(false);
        var corpus = await bridge.BuildCorpusAsync(cancellationToken).ConfigureAwait(false);
        return command.Name switch
        {
            "index" => await IndexAsync(command, context, corpus, cancellationToken).ConfigureAwait(false),
            "search" => await SearchAsync(command, context, corpus, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported TypeScript search command '{command.Name}'."),
        };
    }

    private static async Task<IndexResult> IndexAsync(
        ParsedCommand command,
        TypeScriptSearchContext context,
        TypeScriptCorpus corpus,
        CancellationToken cancellationToken)
    {
        await using var lease = await AcquireWriterLeaseAsync(context.Index, cancellationToken).ConfigureAwait(false);
        var metadata = await lease.ReadMetadataAsync(context.TargetIdentity, cancellationToken).ConfigureAwait(false);
        var rebuild = command.Flag("rebuild");
        if (!rebuild && IsFresh(metadata, corpus.Fingerprint))
        {
            return new IndexResult(
                corpus.TargetPath,
                context.Path.DatabasePath,
                SearchIndexState.Fresh,
                metadata!.SymbolCount,
                Rebuilt: false,
                []);
        }

        var symbols = BuildSymbols(context, corpus);
        await lease.ReplaceTargetAsync(
            new SqliteSearchIndexTarget(
                context.TargetIdentity,
                corpus.Fingerprint,
                SourceLanguageNames.TypeScript),
            symbols,
            cancellationToken).ConfigureAwait(false);
        await lease.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new IndexResult(
            corpus.TargetPath,
            context.Path.DatabasePath,
            SearchIndexState.Fresh,
            symbols.Length,
            Rebuilt: rebuild,
            []);
    }

    private static async Task<SearchResult> SearchAsync(
        ParsedCommand command,
        TypeScriptSearchContext context,
        TypeScriptCorpus corpus,
        CancellationToken cancellationToken)
    {
        var metadata = await context.Index.ReadMetadataAsync(
            context.TargetIdentity,
            cancellationToken).ConfigureAwait(false);
        if (!IsFresh(metadata, corpus.Fingerprint))
        {
            await using var lease = await AcquireWriterLeaseAsync(context.Index, cancellationToken).ConfigureAwait(false);
            metadata = await lease.ReadMetadataAsync(context.TargetIdentity, cancellationToken).ConfigureAwait(false);
            if (!IsFresh(metadata, corpus.Fingerprint))
            {
                await lease.ReplaceTargetAsync(
                    new SqliteSearchIndexTarget(
                        context.TargetIdentity,
                        corpus.Fingerprint,
                        SourceLanguageNames.TypeScript),
                    BuildSymbols(context, corpus),
                    cancellationToken).ConfigureAwait(false);
            }

            await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var query = command.Required("query");
        var search = await context.Index.ReadSearchSnapshotAsync(
            new SqliteSearchIndexQuery(
                context.TargetIdentity,
                SearchQueryTokenizer.TokenizeQuery(query),
                ResolveProjectFilter(command, context, corpus),
                ResolveKinds(command.Name, command.Optional("kind")),
                command.OptionalInt("max-results", 20, 1),
                SourceLanguageNames.TypeScript),
            cancellationToken).ConfigureAwait(false);
        var isFresh = IsFresh(search.Metadata, corpus.Fingerprint);
        var result = isFresh ? search.SearchResult : new SqliteSearchIndexSearchResult(0, []);
        var hits = result.Matches.Select(match => new SearchHit(
            match.DisplayName,
            match.Kind,
            new SourceRange(
                match.Path.Resolve(context.Path.RepositoryRoot),
                match.Line,
                match.Column,
                match.EndLine,
                match.EndColumn),
            match.SymbolId,
            match.Excerpt)).ToArray();

        return new SearchResult(
            corpus.TargetPath,
            context.Path.DatabasePath,
            query,
            isFresh ? SearchIndexState.Fresh : SearchIndexState.Stale,
            result.TotalMatchCount,
            hits.Length,
            result.TotalMatchCount > hits.Length,
            hits,
            []);
    }

    private static SqliteSearchIndexSymbol[] BuildSymbols(
        TypeScriptSearchContext context,
        TypeScriptCorpus corpus)
    {
        return corpus.Records.Select(record =>
        {
            var projectPath = RepositoryRelativePath.FromPhysicalPath(
                context.Path.RepositoryRoot,
                record.ProjectPath,
                "TypeScript project");
            var sourcePath = RepositoryRelativePath.FromPhysicalPath(
                context.Path.RepositoryRoot,
                record.Path,
                "TypeScript source");
            var key = $"{context.TargetIdentity.Value}|{projectPath.Value}|{sourcePath.Value}|{record.Selector}|{record.Line}:{record.Column}-{record.EndLine}:{record.EndColumn}";
            return new SqliteSearchIndexSymbol(
                key,
                projectPath,
                record.ProjectName,
                record.Kind,
                record.Name,
                record.DisplayName,
                record.Selector,
                sourcePath,
                record.Line,
                record.Column,
                record.EndLine,
                record.EndColumn,
                record.Documentation,
                record.Signature,
                record.Comments,
                record.Body,
                record.NameTokens,
                record.ContainingTokens,
                record.DetailsTokens,
                record.PathTokens,
                record.BodyTokens,
                SourceLanguageNames.TypeScript);
        }).ToArray();
    }

    private static IReadOnlyCollection<RepositoryRelativePath>? ResolveProjectFilter(
        ParsedCommand command,
        TypeScriptSearchContext context,
        TypeScriptCorpus corpus)
    {
        var selector = command.Optional("project");
        if (selector is null)
        {
            return null;
        }

        var fullPath = Path.GetFullPath(selector);
        var projectPaths = corpus.Records
            .Select(record => record.ProjectPath)
            .Distinct(StringComparer.Ordinal)
            .Where(path => PathsEqual(path, fullPath))
            .Select(path => RepositoryRelativePath.FromPhysicalPath(
                context.Path.RepositoryRoot,
                path,
                "TypeScript project"))
            .ToArray();
        return projectPaths.Length == 1
            ? projectPaths
            : throw new CliUsageException(command.Name, $"Project '{fullPath}' is not part of the loaded target.");
    }

    private static IReadOnlyCollection<string>? ResolveKinds(string commandName, string? kind)
    {
        return kind switch
        {
            null => null,
            "namespace" => ["namespace"],
            "type" => ["class", "interface", "type", "enum"],
            "member" => ["method", "property", "field", "event"],
            "method" or "property" or "field" or "event" or "class" or "interface" or "enum" => [kind],
            _ => throw new CliUsageException(
                commandName,
                $"Unknown TypeScript symbol kind '{kind}'. Supported values: namespace, type, member, method, property, field, event, class, interface, enum."),
        };
    }

    private static async Task<TypeScriptSearchContext> ResolveContextAsync(
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
            "TypeScript search target");
        return new TypeScriptSearchContext(path, targetIdentity, new SqliteSearchIndex(path.DatabasePath));
    }

    private static async Task<SqliteSearchIndexWriterLease> AcquireWriterLeaseAsync(
        SqliteSearchIndex index,
        CancellationToken cancellationToken)
    {
        try
        {
            return await index.AcquireWriterLeaseAsync(IndexWriterWait, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteSearchIndexWriterLeaseUnavailableException exception)
        {
            throw new InvalidOperationException(
                $"Could not acquire the search-index writer within {IndexWriterWait.TotalSeconds:0} seconds. Another index refresh is still running; retry after it completes.",
                exception);
        }
    }

    private static bool IsFresh(SqliteSearchIndexMetadata? metadata, string fingerprint)
    {
        return metadata is not null
            && string.Equals(metadata.Language, SourceLanguageNames.TypeScript, StringComparison.Ordinal)
            && string.Equals(metadata.Fingerprint, fingerprint, StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private sealed record TypeScriptSearchContext(
        SearchIndexPath Path,
        RepositoryRelativePath TargetIdentity,
        SqliteSearchIndex Index);
}
