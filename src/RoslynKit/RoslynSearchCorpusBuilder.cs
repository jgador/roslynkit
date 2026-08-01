using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynKit;

/// <summary>
/// Builds deterministic, source-declared C# symbol records for persistent search indexing.
/// </summary>
internal sealed class RoslynSearchCorpusBuilder
{
    private const int MaximumBodyCharacters = 24_000;
    private const int MaximumExcerptCharacters = 400;
    private const int MaximumDocumentationCharacters = 4_000;
    private const int MaximumCommentCharacters = 4_000;
    private static readonly SymbolDisplayFormat SearchSignatureFormat = SymbolDisplayFormats.QualifiedMember
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters)
        .WithParameterOptions(
            SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeDefaultValue)
        .WithGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeParameters);

    /// <summary>
    /// Builds the searchable corpus for C# projects in one already-loaded solution.
    /// </summary>
    public async Task<RoslynSearchCorpusBuildResult> BuildAsync(
        Solution solution,
        RoslynSearchCorpusBuildOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TargetIdentity);

        var issues = new List<RoslynSearchCorpusBuildIssue>();
        var projects = SelectProjects(solution, options.ProjectSelector, issues);
        var unsupportedProjects = FindMultiTargetProjects(solution, projects, issues);
        var records = new List<RoslynSearchCorpusRecord>();

        foreach (var project in projects
                     .Where(project => !unsupportedProjects.Contains(project.Id))
                     .OrderBy(project => NormalizePath(project.FilePath), StringComparer.Ordinal)
                     .ThenBy(project => project.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var documents = await BuildDocumentMapAsync(project, options.IncludeGenerated, cancellationToken).ConfigureAwait(false);
            if (documents.Count == 0)
            {
                continue;
            }

            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                issues.Add(new RoslynSearchCorpusBuildIssue(
                    "compilation-unavailable",
                    $"Could not create a C# compilation for project '{FormatProject(project)}'."));
                continue;
            }

            foreach (var symbol in EnumerateNavigableSymbols(compilation.Assembly.GlobalNamespace, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var declaration in GetIncludedDeclarations(symbol, documents, cancellationToken))
                {
                    var record = CreateRecord(options.TargetIdentity, project, symbol, declaration);
                    if (record is not null)
                    {
                        records.Add(record);
                    }
                }
            }
        }

        var orderedRecords = records
            .DistinctBy(record => record.SymbolKey, StringComparer.Ordinal)
            .OrderBy(record => record.ProjectPath, StringComparer.Ordinal)
            .ThenBy(record => record.Path, StringComparer.Ordinal)
            .ThenBy(record => record.Location.Line)
            .ThenBy(record => record.Location.Column)
            .ThenBy(record => record.SymbolKey, StringComparer.Ordinal)
            .ToArray();

        return new RoslynSearchCorpusBuildResult(orderedRecords, issues.ToArray());
    }

    private static IReadOnlyList<Project> SelectProjects(
        Solution solution,
        string? projectSelector,
        ICollection<RoslynSearchCorpusBuildIssue> issues)
    {
        var csharpProjects = solution.Projects
            .Where(project => string.Equals(project.Language, LanguageNames.CSharp, StringComparison.Ordinal))
            .ToArray();

        if (string.IsNullOrWhiteSpace(projectSelector))
        {
            return csharpProjects;
        }

        var normalizedSelector = NormalizePath(projectSelector);
        var matches = csharpProjects
            .Where(project => string.Equals(project.Name, projectSelector, StringComparison.Ordinal)
                || (normalizedSelector is not null
                    && string.Equals(NormalizePath(project.FilePath), normalizedSelector, PathComparison)))
            .ToArray();

        if (matches.Length == 0)
        {
            issues.Add(new RoslynSearchCorpusBuildIssue(
                "project-not-found",
                $"No loaded C# project matches '{projectSelector}'."));
        }

        return matches;
    }

    private static HashSet<ProjectId> FindMultiTargetProjects(
        Solution solution,
        IReadOnlyList<Project> selectedProjects,
        ICollection<RoslynSearchCorpusBuildIssue> issues)
    {
        var selectedProjectIds = selectedProjects.Select(project => project.Id).ToHashSet();
        var unsupportedProjectIds = new HashSet<ProjectId>();

        foreach (var group in solution.Projects
                     .Where(project => string.Equals(project.Language, LanguageNames.CSharp, StringComparison.Ordinal))
                     .Where(project => !string.IsNullOrWhiteSpace(project.FilePath))
                     .GroupBy(project => NormalizePath(project.FilePath), StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            var affectedProjects = group.Where(project => selectedProjectIds.Contains(project.Id)).ToArray();
            if (affectedProjects.Length == 0)
            {
                continue;
            }

            foreach (var project in affectedProjects)
            {
                unsupportedProjectIds.Add(project.Id);
            }

            var targetFrameworkContexts = group
                .Select(project => project.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            issues.Add(new RoslynSearchCorpusBuildIssue(
                "multiple-target-frameworks",
                $"Project '{group.Key}' appears in multiple target-framework contexts ({string.Join(", ", targetFrameworkContexts)}). Search indexing supports one target framework per project."));
        }

        return unsupportedProjectIds;
    }

    private static async Task<IReadOnlyDictionary<SyntaxTree, CorpusDocument>> BuildDocumentMapAsync(
        Project project,
        bool includeGenerated,
        CancellationToken cancellationToken)
    {
        var documents = new List<(Document document, bool generated)>();
        documents.AddRange(project.Documents
            .Where(document => includeGenerated || !RoslynDocumentFilters.IsGenerated(document))
            .Select(document => (document, generated: RoslynDocumentFilters.IsGenerated(document))));
        if (includeGenerated)
        {
            documents.AddRange((await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
                .Select(document => ((Document)document, generated: true)));
        }

        var result = new Dictionary<SyntaxTree, CorpusDocument>();
        foreach (var (document, generated) in documents
                     .OrderBy(item => NormalizePath(item.document.FilePath), StringComparer.Ordinal)
                     .ThenBy(item => item.document.Name, StringComparer.Ordinal)
                     .ThenBy(item => item.document.Id.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            if (syntaxTree is null)
            {
                continue;
            }

            var path = NormalizePath(document.FilePath)
                ?? NormalizePath(syntaxTree.FilePath)
                ?? document.Name;
            var documentKey = !string.IsNullOrWhiteSpace(document.FilePath)
                ? path
                : $"generated:{document.Id.Id:N}:{document.Name}";
            result.TryAdd(syntaxTree, new CorpusDocument(documentKey, path, generated));
        }

        return result;
    }

    private static IEnumerable<ISymbol> EnumerateNavigableSymbols(
        INamespaceSymbol globalNamespace,
        CancellationToken cancellationToken)
    {
        foreach (var namespaceSymbol in globalNamespace.GetNamespaceMembers().OrderBy(symbol => symbol.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var symbol in EnumerateNamespaceSymbols(namespaceSymbol, cancellationToken))
            {
                yield return symbol;
            }
        }

        foreach (var type in globalNamespace.GetTypeMembers().OrderBy(symbol => symbol.MetadataName, StringComparer.Ordinal))
        {
            foreach (var symbol in EnumerateTypeSymbols(type, cancellationToken))
            {
                yield return symbol;
            }
        }
    }

    private static IEnumerable<ISymbol> EnumerateNamespaceSymbols(
        INamespaceSymbol namespaceSymbol,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsNavigableSymbol(namespaceSymbol))
        {
            yield return namespaceSymbol;
        }

        foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers().OrderBy(symbol => symbol.Name, StringComparer.Ordinal))
        {
            foreach (var symbol in EnumerateNamespaceSymbols(childNamespace, cancellationToken))
            {
                yield return symbol;
            }
        }

        foreach (var type in namespaceSymbol.GetTypeMembers().OrderBy(symbol => symbol.MetadataName, StringComparer.Ordinal))
        {
            foreach (var symbol in EnumerateTypeSymbols(type, cancellationToken))
            {
                yield return symbol;
            }
        }
    }

    private static IEnumerable<ISymbol> EnumerateTypeSymbols(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsNavigableSymbol(type))
        {
            yield return type;
        }

        foreach (var member in type.GetMembers().OrderBy(symbol => symbol.MetadataName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member is INamedTypeSymbol nestedType)
            {
                foreach (var symbol in EnumerateTypeSymbols(nestedType, cancellationToken))
                {
                    yield return symbol;
                }

                continue;
            }

            if (IsNavigableSymbol(member))
            {
                yield return member;
            }
        }
    }

    private static bool IsNavigableSymbol(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared || !RoslynSymbolSearch.IsCodeSymbol(symbol))
        {
            return false;
        }

        if (symbol is INamespaceSymbol { IsGlobalNamespace: true })
        {
            return false;
        }

        return symbol is not IMethodSymbol { AssociatedSymbol: not null };
    }

    private static IEnumerable<CorpusDeclaration> GetIncludedDeclarations(
        ISymbol symbol,
        IReadOnlyDictionary<SyntaxTree, CorpusDocument> documents,
        CancellationToken cancellationToken)
    {
        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences
                     .OrderBy(reference => NormalizePath(reference.SyntaxTree.FilePath), StringComparer.Ordinal)
                     .ThenBy(reference => reference.Span.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!documents.TryGetValue(syntaxReference.SyntaxTree, out var document))
            {
                continue;
            }

            var node = syntaxReference.GetSyntax(cancellationToken);
            var location = symbol.Locations.FirstOrDefault(candidate =>
                    candidate.IsInSource
                    && candidate.SourceTree == syntaxReference.SyntaxTree
                    && candidate.SourceSpan.IntersectsWith(syntaxReference.Span))
                ?? node.GetLocation();
            var sourceRange = SourceRange.FromLocation(location);
            var path = sourceRange.Path ?? document.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            yield return new CorpusDeclaration(document, node, sourceRange, path);
        }
    }

    private static RoslynSearchCorpusRecord? CreateRecord(
        string targetIdentity,
        Project project,
        ISymbol symbol,
        CorpusDeclaration declaration)
    {
        var projectPath = NormalizePath(project.FilePath) ?? $"project:{project.Id.Id:N}";
        var symbolId = DocumentationCommentId.CreateDeclarationId(symbol);
        var displayName = symbol.ToDisplayString(SymbolDisplayFormats.QualifiedMember);
        var signature = NormalizeWhitespace(symbol.ToDisplayString(SearchSignatureFormat));
        var documentation = ExtractDocumentation(symbol, declaration.Node);
        var comments = ExtractComments(declaration.Node);
        var attributes = ExtractAttributes(symbol);
        var body = Truncate(NormalizeWhitespace(declaration.Node.ToFullString()), MaximumBodyCharacters);
        var excerpt = SelectExcerpt(documentation, comments, signature, body);
        var documentKey = $"{projectPath}|{declaration.Document.Key}";
        var identity = symbolId ?? $"{symbol.Kind}:{displayName}";
        var symbolKey = $"{targetIdentity}|{documentKey}|{identity}|{declaration.Node.Span.Start}";

        return new RoslynSearchCorpusRecord(
            targetIdentity,
            projectPath,
            project.Name,
            documentKey,
            declaration.Path,
            symbolKey,
            GetSearchKind(symbol),
            symbol.Name,
            displayName,
            symbol.ContainingType?.ToDisplayString(SymbolDisplayFormats.Qualified)
                ?? (symbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace
                    ? containingNamespace.ToDisplayString(SymbolDisplayFormats.Qualified)
                    : null),
            symbolId,
            declaration.Location,
            documentation,
            comments,
            attributes,
            signature,
            body,
            excerpt,
            BuildNameSearchText(symbol.Name),
            BuildSearchText($"{symbol.ContainingType?.ToDisplayString(SymbolDisplayFormats.Qualified)} {symbol.ContainingNamespace?.ToDisplayString(SymbolDisplayFormats.Qualified)} {displayName}"),
            BuildSearchText($"{documentation} {comments} {attributes} {signature}"),
            BuildSearchText(declaration.Path),
            BuildSearchText(body));
    }

    private static string? ExtractDocumentation(ISymbol symbol, SyntaxNode declaration)
    {
        var declarationDocumentation = string.Concat(declaration.GetLeadingTrivia()
            .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .Select(trivia => trivia.ToFullString()));
        var documentation = string.IsNullOrWhiteSpace(declarationDocumentation)
            ? symbol.GetDocumentationCommentXml()
            : declarationDocumentation;
        return Truncate(NormalizeDocumentation(documentation), MaximumDocumentationCharacters);
    }

    private static string GetSearchKind(ISymbol symbol)
    {
        return symbol switch
        {
            INamespaceSymbol => "namespace",
            INamedTypeSymbol { TypeKind: TypeKind.Class } => "class",
            INamedTypeSymbol { TypeKind: TypeKind.Struct } => "struct",
            INamedTypeSymbol { TypeKind: TypeKind.Interface } => "interface",
            INamedTypeSymbol { TypeKind: TypeKind.Enum } => "enum",
            INamedTypeSymbol { TypeKind: TypeKind.Delegate } => "delegate",
            IMethodSymbol => "method",
            IPropertySymbol => "property",
            IFieldSymbol => "field",
            IEventSymbol => "event",
            _ => throw new InvalidOperationException($"Unsupported search symbol kind '{symbol.Kind}'."),
        };
    }

    private static string? ExtractComments(SyntaxNode declaration)
    {
        var comments = string.Concat(declaration.DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            .Select(trivia => trivia.ToFullString()));
        return Truncate(NormalizeCommentText(comments), MaximumCommentCharacters);
    }

    private static string? ExtractAttributes(ISymbol symbol)
    {
        var values = symbol.GetAttributes()
            .Select(attribute => attribute.AttributeClass?.ToDisplayString(SymbolDisplayFormats.Qualified))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        return values.Length == 0 ? null : string.Join(' ', values);
    }

    private static string? SelectExcerpt(string? documentation, string? comments, string? signature, string? body)
    {
        return Truncate(documentation ?? comments ?? signature ?? body, MaximumExcerptCharacters);
    }

    private static string BuildSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var termStart = -1;
        for (var index = 0; index <= value.Length; index++)
        {
            if (index < value.Length && (char.IsAsciiLetterOrDigit(value[index]) || value[index] == '_'))
            {
                if (termStart < 0)
                {
                    termStart = index;
                }

                continue;
            }

            if (termStart < 0)
            {
                continue;
            }

            foreach (var token in SearchQueryTokenizer.TokenizeIdentifier(value[termStart..index]).Tokens)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(token);
            }

            termStart = -1;
        }

        return builder.ToString();
    }

    private static string BuildNameSearchText(string name)
    {
        var tokenization = SearchQueryTokenizer.TokenizeIdentifier(name);
        var asyncIsConventionSuffix = tokenization.Tokens.Count > 1
            && string.Equals(tokenization.Tokens[^1], "async", StringComparison.Ordinal)
            && !string.Equals(tokenization.NormalizedText, "async", StringComparison.Ordinal);
        return string.Join(' ', tokenization.Tokens.Where(token =>
            !asyncIsConventionSuffix || !string.Equals(token, "async", StringComparison.Ordinal)));
    }

    private static string? NormalizeDocumentation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var withoutPrefixes = value
            .Replace("///", " ", StringComparison.Ordinal)
            .Replace("/**", " ", StringComparison.Ordinal)
            .Replace("*/", " ", StringComparison.Ordinal)
            .Replace("*", " ", StringComparison.Ordinal);
        return NormalizeXmlLikeText(withoutPrefixes);
    }

    private static string? NormalizeCommentText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return NormalizeWhitespace(value
            .Replace("//", " ", StringComparison.Ordinal)
            .Replace("/*", " ", StringComparison.Ordinal)
            .Replace("*/", " ", StringComparison.Ordinal));
    }

    private static string? NormalizeXmlLikeText(string value)
    {
        var builder = new StringBuilder(value.Length);
        var insideTag = false;
        foreach (var character in value)
        {
            if (character == '<')
            {
                insideTag = true;
                builder.Append(' ');
                continue;
            }

            if (character == '>')
            {
                insideTag = false;
                builder.Append(' ');
                continue;
            }

            if (!insideTag)
            {
                builder.Append(character);
            }
        }

        return NormalizeWhitespace(builder.ToString());
    }

    private static string? NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? Truncate(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private static string? NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }

    private static string FormatProject(Project project)
    {
        return NormalizePath(project.FilePath) ?? project.Name;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record CorpusDocument(string Key, string Path, bool IsGenerated);

    private sealed record CorpusDeclaration(
        CorpusDocument Document,
        SyntaxNode Node,
        SourceRange Location,
        string Path);
}

/// <summary>
/// Specifies the target and source inclusion choices for corpus construction.
/// </summary>
internal sealed record RoslynSearchCorpusBuildOptions(
    string TargetIdentity,
    string? ProjectSelector = null,
    bool IncludeGenerated = false);

/// <summary>
/// Reports one non-fatal corpus construction limitation that the command layer can render as an actionable failure.
/// </summary>
internal sealed record RoslynSearchCorpusBuildIssue(string Code, string Message);

/// <summary>
/// Contains deterministic source symbol records and any construction issues.
/// </summary>
internal sealed record RoslynSearchCorpusBuildResult(
    IReadOnlyList<RoslynSearchCorpusRecord> Records,
    IReadOnlyList<RoslynSearchCorpusBuildIssue> Issues);

/// <summary>
/// Represents one source declaration together with its weighted search fields.
/// </summary>
internal sealed record RoslynSearchCorpusRecord(
    string TargetIdentity,
    string ProjectPath,
    string ProjectName,
    string DocumentKey,
    string Path,
    string SymbolKey,
    string Kind,
    string Name,
    string DisplayName,
    string? ContainingName,
    string? SymbolId,
    SourceRange Location,
    string? Documentation,
    string? Comments,
    string? Attributes,
    string? Signature,
    string? Body,
    string? Excerpt,
    string NameTokens,
    string ContainingTokens,
    string DetailsTokens,
    string PathTokens,
    string BodyTokens)
{
    /// <summary>
    /// Converts this corpus record into the storage model owned by the SQLite index.
    /// </summary>
    public SqliteSearchIndexSymbol ToSqliteSymbol()
    {
        return new SqliteSearchIndexSymbol(
            SymbolKey,
            ProjectPath,
            ProjectName,
            Kind,
            Name,
            DisplayName,
            SymbolId,
            Path,
            Location.Line,
            Location.Column,
            Location.EndLine,
            Location.EndColumn,
            Documentation,
            Signature,
            Comments,
            Body,
            NameTokens,
            ContainingTokens,
            DetailsTokens,
            PathTokens,
            BodyTokens);
    }
}
