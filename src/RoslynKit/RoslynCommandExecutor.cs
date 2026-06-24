using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynKit;

public static class RoslynCommandExecutor
{
    public static async Task<object> ExecuteAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        return command.Name switch
        {
            "diagnostics" => await DiagnosticsAsync(command, cancellationToken).ConfigureAwait(false),
            "definition" => await DefinitionAsync(command, cancellationToken).ConfigureAwait(false),
            "document-symbols" => await DocumentSymbolsAsync(command, cancellationToken).ConfigureAwait(false),
            "references" => await ReferencesAsync(command, cancellationToken).ConfigureAwait(false),
            "symbols" => await SymbolsAsync(command, cancellationToken).ConfigureAwait(false),
            "workspace" => await WorkspaceAsync(command, cancellationToken).ConfigureAwait(false),
            _ => throw new CliUsageException(command.Name, $"Unknown command '{command.Name}'."),
        };
    }

    private static async Task<object> WorkspaceAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
        var includeGenerated = command.Flag("include-generated");

        var projects = loaded.Solution.Projects
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .Select(project => new ProjectInfoDto(
                project.Name,
                project.FilePath,
                project.Language,
                project.Documents.Count(document => includeGenerated || !RoslynDocumentFilters.IsGenerated(document)),
                project.ProjectReferences
                    .Select(reference => project.Solution.GetProject(reference.ProjectId)?.Name)
                    .Where(referenceName => !string.IsNullOrWhiteSpace(referenceName))
                    .Order(StringComparer.Ordinal)
                    .ToArray()!))
            .ToArray();

        var documents = loaded.Solution.Projects
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .SelectMany(project => project.Documents
                .Where(document => includeGenerated || !RoslynDocumentFilters.IsGenerated(document))
                .Select(document => new DocumentInfoDto(project.Name, document.Name, document.FilePath)))
            .OrderBy(document => document.ProjectName, StringComparer.Ordinal)
            .ThenBy(document => document.Path, StringComparer.Ordinal)
            .ToArray();

        return new WorkspaceResult(loaded.TargetPath, loaded.TargetKind, projects, documents, loaded.WorkspaceDiagnostics);
    }

    private static async Task<object> DiagnosticsAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var maxResults = command.OptionalInt("max-results", 200, 1);
        var includeGenerated = command.Flag("include-generated");
        var includeHidden = command.Flag("include-hidden");
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
        var diagnostics = new List<DiagnosticDto>();

        foreach (var project in loaded.Solution.Projects.OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            var projectSourcePaths = RoslynDocumentFilters.GetProjectSourcePaths(project);
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
            {
                if (!includeHidden && diagnostic.Severity == DiagnosticSeverity.Hidden)
                {
                    continue;
                }

                if (!includeGenerated && diagnostic.Location.IsInSource && !RoslynDocumentFilters.LocationMatchesAnyPath(diagnostic.Location, projectSourcePaths))
                {
                    continue;
                }

                diagnostics.Add(DiagnosticDto.FromDiagnostic(project.Name, diagnostic));
            }
        }

        var ordered = diagnostics
            .OrderBy(diagnostic => diagnostic.Path, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Line)
            .ThenBy(diagnostic => diagnostic.Column)
            .ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .Take(maxResults)
            .ToArray();

        return new DiagnosticsResult(loaded.TargetPath, diagnostics.Count, ordered.Length, diagnostics.Count > ordered.Length, ordered, loaded.WorkspaceDiagnostics);
    }

    private static async Task<object> SymbolsAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var query = command.Required("query");
        var maxResults = command.OptionalInt("max-results", 200, 1);
        var comparison = command.Flag("case-sensitive") ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
        var symbols = new List<SymbolDto>();

        foreach (var project in loaded.Solution.Projects.OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            var projectSourcePaths = RoslynDocumentFilters.GetProjectSourcePaths(project);
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var symbol in RoslynSymbolSearch.EnumerateSourceSymbols(compilation.GlobalNamespace, cancellationToken))
            {
                if (RoslynDocumentFilters.IsDeclaredInProject(symbol, projectSourcePaths) && SymbolMatches(symbol, query, comparison))
                {
                    symbols.Add(SymbolDto.FromSymbol(symbol, project.Name));
                }
            }
        }

        var ordered = symbols
            .OrderBy(symbol => symbol.DisplayName, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Kind, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.PrimaryLocation?.Path, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.PrimaryLocation?.Line)
            .Take(maxResults)
            .ToArray();

        return new SymbolsResult(loaded.TargetPath, query, symbols.Count, ordered.Length, symbols.Count > ordered.Length, ordered, loaded.WorkspaceDiagnostics);
    }

    private static async Task<object> DocumentSymbolsAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
        var document = loaded.FindDocument(command.Required("file"));
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CliUsageException(command.Name, $"Could not create semantic model for '{document.FilePath}'.");
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CliUsageException(command.Name, $"Could not parse '{document.FilePath}'.");

        var symbols = root.DescendantNodesAndSelf()
            .Select(node => model.GetDeclaredSymbol(node, cancellationToken))
            .Where(symbol => symbol is not null && RoslynSymbolSearch.IsDocumentSymbol(symbol) && RoslynDocumentFilters.IsDeclaredInDocument(symbol, document.FilePath))
            .Select(symbol => SymbolDto.FromSymbol(symbol!, document.Project.Name, document.FilePath))
            .Where(symbol => symbol.Declarations.Count > 0)
            .DistinctBy(symbol => string.Concat(symbol.Kind, "|", symbol.DisplayName, "|", symbol.PrimaryLocation?.Path, "|", symbol.PrimaryLocation?.Line, "|", symbol.PrimaryLocation?.Column))
            .OrderBy(symbol => symbol.PrimaryLocation?.Line)
            .ThenBy(symbol => symbol.PrimaryLocation?.Column)
            .ThenBy(symbol => symbol.DisplayName, StringComparer.Ordinal)
            .ToArray();

        return new DocumentSymbolsResult(Path.GetFullPath(command.Required("file")), symbols, loaded.WorkspaceDiagnostics);
    }

    private static async Task<object> DefinitionAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
        var document = loaded.FindDocument(command.Required("file"));
        var symbol = await SymbolAtPositionAsync(command, document, cancellationToken).ConfigureAwait(false);
        var sourceSymbol = await SymbolFinder.FindSourceDefinitionAsync(symbol, loaded.Solution, cancellationToken).ConfigureAwait(false) ?? symbol;

        return new DefinitionResult(
            Path.GetFullPath(command.Required("file")),
            command.OptionalInt("line", 1, 1),
            command.OptionalInt("column", 1, 1),
            SymbolDto.FromSymbol(sourceSymbol, document.Project.Name),
            loaded.WorkspaceDiagnostics);
    }

    private static async Task<object> ReferencesAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var maxResults = command.OptionalInt("max-results", 200, 1);
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(command.Required("target"), cancellationToken).ConfigureAwait(false);
        var document = loaded.FindDocument(command.Required("file"));
        var symbol = await SymbolAtPositionAsync(command, document, cancellationToken).ConfigureAwait(false);
        var sourceSymbol = await SymbolFinder.FindSourceDefinitionAsync(symbol, loaded.Solution, cancellationToken).ConfigureAwait(false) ?? symbol;
        var references = await SymbolFinder.FindReferencesAsync(sourceSymbol, loaded.Solution, cancellationToken).ConfigureAwait(false);

        var locations = references
            .SelectMany(reference => reference.Locations.Select(location => ReferenceLocationDto.FromReferenceLocation(reference.Definition, location)))
            .OrderBy(location => location.Path, StringComparer.Ordinal)
            .ThenBy(location => location.Line)
            .ThenBy(location => location.Column)
            .Take(maxResults)
            .ToArray();

        var totalCount = references.Sum(reference => reference.Locations.Count());

        return new ReferencesResult(
            Path.GetFullPath(command.Required("file")),
            command.OptionalInt("line", 1, 1),
            command.OptionalInt("column", 1, 1),
            SymbolDto.FromSymbol(sourceSymbol, document.Project.Name),
            totalCount,
            locations.Length,
            totalCount > locations.Length,
            locations,
            loaded.WorkspaceDiagnostics);
    }

    private static async Task<ISymbol> SymbolAtPositionAsync(ParsedCommand command, Document document, CancellationToken cancellationToken)
    {
        var line = command.OptionalInt("line", 1, 1);
        var column = command.OptionalInt("column", 1, 1);
        var position = await PositionResolver.GetPositionAsync(document, line, column, command.Name, cancellationToken).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CliUsageException(command.Name, $"Could not parse '{document.FilePath}'.");
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CliUsageException(command.Name, $"Could not create semantic model for '{document.FilePath}'.");
        var token = root.FindToken(position, findInsideTrivia: true);

        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            var symbol = model.GetSymbolInfo(node, cancellationToken).Symbol ?? model.GetDeclaredSymbol(node, cancellationToken);
            if (symbol is not null)
            {
                return symbol;
            }
        }

        throw new CliUsageException(command.Name, $"No symbol found at line {line}, column {column} in '{document.FilePath}'.");
    }

    private static bool SymbolMatches(ISymbol symbol, string query, StringComparison comparison)
    {
        return symbol.Name.Contains(query, comparison)
            || symbol.MetadataName.Contains(query, comparison)
            || (!RoslynSymbolSearch.IsConstructor(symbol) && symbol.ToDisplayString(SymbolDisplayFormats.Qualified).Contains(query, comparison));
    }
}
