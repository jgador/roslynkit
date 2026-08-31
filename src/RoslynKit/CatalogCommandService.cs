using System.Text.Json;

namespace RoslynKit;

/// <summary>
/// Serves compiler-independent semantic commands from a fresh repository catalog.
/// </summary>
internal static class CatalogCommandService
{
    private const int ReferenceCacheFormatVersion = 1;
    private const string ReferenceResultType = "references";

    public static bool MaintainsCatalog(ParsedCommand command)
    {
        return command.Name is "symbols"
            or "definition"
            or "references"
            or "implementations"
            or "symbol-context"
            or "symbol-source";
    }

    /// <summary>
    /// Returns a cached result when the catalog can answer the exact request without Roslyn.
    /// </summary>
    public static async Task<object?> TryExecuteAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        if (!CanReadFromCatalog(command))
        {
            return null;
        }

        var context = await SearchCommandService.ResolveFreshCatalogContextAsync(
            command,
            cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }

        return command.Name switch
        {
            "symbols" => await SymbolsAsync(command, context, cancellationToken).ConfigureAwait(false),
            "definition" => await DefinitionAsync(command, context, cancellationToken).ConfigureAwait(false),
            "references" => await ReferencesAsync(command, context, cancellationToken).ConfigureAwait(false),
            "implementations" => await ImplementationsAsync(command, context, cancellationToken).ConfigureAwait(false),
            "symbol-source" => await SymbolSourceAsync(command, context, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    /// <summary>
    /// Persists complete live operation results whose relationships are populated lazily.
    /// </summary>
    public static async Task StoreLiveResultAsync(
        ParsedCommand command,
        object result,
        CancellationToken cancellationToken)
    {
        if (command.Name != "references"
            || command.Optional("symbol") is null
            || result is not ReferencesResult references)
        {
            return;
        }

        var context = await SearchCommandService.ResolveFreshCatalogContextAsync(
            command,
            cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return;
        }
        var locations = references.Locations
            .Select(location => new CachedReferenceLocation(
                location.Path is null
                    ? null
                    : RepositoryRelativePath.FromPhysicalPath(
                        context.Path.RepositoryRoot,
                        location.Path,
                        "Reference location").Value,
                location.Line,
                location.Column,
                location.EndLine,
                location.EndColumn,
                location.IsImplicit,
                location.Definition))
            .ToArray();
        var cached = new CachedReferences(
            references.Symbol.SymbolId ?? command.Required("symbol"),
            references.TotalCount,
            references.ReturnedCount,
            references.Truncated,
            locations);
        await context.Index.WriteCatalogOperationAsync(
            context.TargetIdentity,
            CreateOperationKey(command),
            ReferenceResultType,
            ReferenceCacheFormatVersion,
            JsonSerializer.Serialize(cached),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool CanReadFromCatalog(ParsedCommand command)
    {
        return command.Name switch
        {
            "symbols" => command.Flag("exact"),
            "definition" or "references" or "implementations" or "symbol-source" =>
                command.Optional("symbol") is not null,
            _ => false,
        };
    }

    private static async Task<SymbolsResult> SymbolsAsync(
        ParsedCommand command,
        SemanticCatalogContext context,
        CancellationToken cancellationToken)
    {
        var query = command.Required("query");
        var maxResults = command.OptionalInt("max-results", CommandDefaults.MaxResults, 1);
        var rows = await context.Index.ReadCatalogSymbolsByNameAsync(
            context.TargetIdentity,
            query,
            command.Flag("case-sensitive"),
            ResolveKinds(command.Optional("kind")),
            cancellationToken).ConfigureAwait(false);
        var symbols = GroupSymbols(rows, context.Path.RepositoryRoot)
            .OrderBy(symbol => symbol.DisplayName, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Kind, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.PrimaryLocation?.Path, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.PrimaryLocation?.Line)
            .ToArray();
        var selected = symbols.Take(maxResults).ToArray();
        return new SymbolsResult(
            context.Path.TargetPath,
            query,
            symbols.Length,
            selected.Length,
            symbols.Length > selected.Length,
            selected,
            []);
    }

    private static async Task<DefinitionResult?> DefinitionAsync(
        ParsedCommand command,
        SemanticCatalogContext context,
        CancellationToken cancellationToken)
    {
        var selector = command.Required("symbol");
        var rows = await TryResolveSymbolRowsAsync(
            command.Name,
            context,
            selector,
            cancellationToken).ConfigureAwait(false);
        if (rows is null)
        {
            return null;
        }

        return new DefinitionResult(
            document: null,
            line: null,
            column: null,
            selector,
            CreateSymbol(rows, context.Path.RepositoryRoot),
            []);
    }

    private static async Task<ReferencesResult?> ReferencesAsync(
        ParsedCommand command,
        SemanticCatalogContext context,
        CancellationToken cancellationToken)
    {
        var payload = await context.Index.ReadCatalogOperationAsync(
            context.TargetIdentity,
            CreateOperationKey(command),
            ReferenceResultType,
            ReferenceCacheFormatVersion,
            cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return null;
        }

        var cached = JsonSerializer.Deserialize<CachedReferences>(payload)
            ?? throw new InvalidOperationException(
                "The cached reference payload is invalid. Run index --rebuild to recreate the catalog.");
        var rows = await TryResolveSymbolRowsAsync(
            command.Name,
            context,
            cached.SymbolSelector,
            cancellationToken).ConfigureAwait(false);
        if (rows is null)
        {
            return null;
        }

        var locations = cached.Locations
            .Select(location => new ReferenceItem(
                location.Path is null
                    ? null
                    : RepositoryRelativePath
                        .FromStoredValue(location.Path, "Cached reference path")
                        .Resolve(context.Path.RepositoryRoot),
                location.Line,
                location.Column,
                location.EndLine,
                location.EndColumn,
                location.IsImplicit,
                location.Definition))
            .ToArray();
        return new ReferencesResult(
            document: null,
            line: null,
            column: null,
            command.Required("symbol"),
            CreateSymbol(rows, context.Path.RepositoryRoot),
            cached.TotalCount,
            cached.ReturnedCount,
            cached.Truncated,
            locations,
            []);
    }

    private static async Task<ImplementationsResult?> ImplementationsAsync(
        ParsedCommand command,
        SemanticCatalogContext context,
        CancellationToken cancellationToken)
    {
        var selector = command.Required("symbol");
        var targetRows = await TryResolveSymbolRowsAsync(
            command.Name,
            context,
            selector,
            cancellationToken).ConfigureAwait(false);
        if (targetRows is null)
        {
            return null;
        }

        var target = CreateSymbol(targetRows, context.Path.RepositoryRoot);
        if (target.SymbolId is null)
        {
            return null;
        }

        var rows = await context.Index.ReadCatalogImplementationsAsync(
            context.TargetIdentity,
            target.SymbolId,
            cancellationToken).ConfigureAwait(false);
        var symbols = GroupSymbols(rows, context.Path.RepositoryRoot)
            .OrderBy(symbol => symbol.DisplayName, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Kind, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.PrimaryLocation?.Path, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.PrimaryLocation?.Line)
            .ToArray();
        var maxResults = command.OptionalInt("max-results", CommandDefaults.MaxResults, 1);
        var selected = symbols.Take(maxResults).ToArray();
        return new ImplementationsResult(
            document: null,
            line: null,
            column: null,
            selector,
            target,
            symbols.Length,
            selected.Length,
            symbols.Length > selected.Length,
            selected,
            []);
    }

    private static async Task<SymbolSourceResult?> SymbolSourceAsync(
        ParsedCommand command,
        SemanticCatalogContext context,
        CancellationToken cancellationToken)
    {
        var selector = command.Required("symbol");
        var rows = await TryResolveSymbolRowsAsync(
            command.Name,
            context,
            selector,
            cancellationToken).ConfigureAwait(false);
        if (rows is null)
        {
            return null;
        }

        var declarations = new List<SymbolSourceDeclaration>();
        foreach (var row in rows
                     .OrderBy(row => row.Path.Value, StringComparer.Ordinal)
                     .ThenBy(row => row.SpanStart))
        {
            var sourcePath = row.Path.Resolve(context.Path.RepositoryRoot);
            var source = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (row.SpanStart > source.Length
                || row.SpanLength > source.Length - row.SpanStart)
            {
                throw new InvalidOperationException(
                    $"The semantic catalog source span for '{sourcePath}' is invalid. Run index --rebuild to recreate the catalog.");
            }

            var projectPath = row.ProjectPath.Resolve(context.Path.RepositoryRoot);
            declarations.Add(new SymbolSourceDeclaration(
                new DocumentDescriptor(
                    row.ProjectName,
                    projectPath,
                    targetFramework: null,
                    documentKind: "source",
                    Path.GetFileName(sourcePath),
                    sourcePath,
                    row.ProjectPath.Value,
                    row.Path.Value),
                new DocumentRange(row.Line, row.Column, row.EndLine, row.EndColumn),
                source.Substring(row.SpanStart, row.SpanLength)));
        }

        return new SymbolSourceResult(
            context.Path.TargetPath,
            selector,
            CreateSymbol(rows, context.Path.RepositoryRoot),
            declarations,
            []);
    }

    private static async Task<IReadOnlyList<SqliteSearchIndexSymbol>?> TryResolveSymbolRowsAsync(
        string commandName,
        SemanticCatalogContext context,
        string selector,
        CancellationToken cancellationToken)
    {
        var rows = await context.Index.ReadCatalogSymbolsAsync(
            context.TargetIdentity,
            selector,
            cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var identityGroups = rows
            .GroupBy(SymbolIdentity, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        if (!IsDeclarationId(selector) && identityGroups.Length > 1)
        {
            throw new CliUsageException(
                commandName,
                $"Symbol '{selector}' is ambiguous in the indexed target. Retry with --symbol and one of: {string.Join(", ", identityGroups.Select(group => group.Key).Take(20))}.");
        }

        var selectedIdentity = identityGroups[0]
            .OrderBy(row => row.ProjectName, StringComparer.Ordinal)
            .ThenBy(row => row.ProjectPath.Value, StringComparer.Ordinal)
            .First();
        return identityGroups[0]
            .Where(row => row.ProjectPath == selectedIdentity.ProjectPath)
            .OrderBy(row => row.Path.Value, StringComparer.Ordinal)
            .ThenBy(row => row.Line)
            .ThenBy(row => row.Column)
            .ToArray();
    }

    private static IReadOnlyList<SymbolItem> GroupSymbols(
        IReadOnlyList<SqliteSearchIndexSymbol> rows,
        string repositoryRoot)
    {
        return rows
            .GroupBy(
                row => $"{row.ProjectPath.Value}|{SymbolIdentity(row)}",
                StringComparer.Ordinal)
            .Select(group => CreateSymbol(group.ToArray(), repositoryRoot))
            .ToArray();
    }

    private static SymbolItem CreateSymbol(
        IReadOnlyList<SqliteSearchIndexSymbol> rows,
        string repositoryRoot)
    {
        var ordered = rows
            .OrderBy(row => row.Path.Value, StringComparer.Ordinal)
            .ThenBy(row => row.Line)
            .ThenBy(row => row.Column)
            .ToArray();
        var first = ordered[0];
        var declarations = ordered
            .Select(row => new SourceRange(
                row.Path.Resolve(repositoryRoot),
                row.Line,
                row.Column,
                row.EndLine,
                row.EndColumn))
            .ToArray();
        return new SymbolItem(
            first.ProjectName,
            first.Name,
            first.MetadataName ?? first.Name,
            first.DisplayName,
            first.SymbolKind ?? first.Kind,
            first.Accessibility ?? "NotApplicable",
            first.IsStatic,
            first.ContainingType,
            first.ContainingNamespace,
            declarations[0],
            declarations,
            first.SymbolId,
            first.Documentation);
    }

    private static IReadOnlyCollection<string>? ResolveKinds(string? kind)
    {
        return kind switch
        {
            null => null,
            "namespace" => ["namespace"],
            "type" => ["class", "interface", "struct", "enum", "delegate"],
            "member" => ["method", "property", "field", "event"],
            "method" or "property" or "field" or "event" or "class" or "interface" or "struct" or "enum" or "delegate" => [kind],
            _ => throw new InvalidOperationException($"Unexpected validated symbol kind '{kind}'."),
        };
    }

    private static string SymbolIdentity(SqliteSearchIndexSymbol symbol)
    {
        return symbol.SymbolId ?? $"{symbol.Kind}:{symbol.DisplayName}";
    }

    private static bool IsDeclarationId(string selector)
    {
        return selector.Length > 2
            && selector[1] == ':'
            && selector[0] is 'T' or 'M' or 'P' or 'F' or 'E' or 'N';
    }

    private static string CreateOperationKey(ParsedCommand command)
    {
        return string.Join(
            '\u001f',
            command.Name,
            command.Required("symbol"),
            command.Optional("project") ?? string.Empty,
            command.Optional("max-results") ?? string.Empty);
    }

    private sealed record CachedReferences(
        string SymbolSelector,
        int TotalCount,
        int ReturnedCount,
        bool Truncated,
        IReadOnlyList<CachedReferenceLocation> Locations);

    private sealed record CachedReferenceLocation(
        string? Path,
        int Line,
        int Column,
        int EndLine,
        int EndColumn,
        bool IsImplicit,
        string Definition);
}
