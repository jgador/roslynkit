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
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RepositoryRoot);

        var issues = new List<RoslynSearchCorpusBuildIssue>();
        var projects = SelectProjects(solution, options.RepositoryRoot, options.ProjectPath, issues);
        var unsupportedProjects = FindMultiTargetProjects(solution, projects, issues);
        var records = new List<RoslynSearchCorpusRecord>();
        var catalogProjects = new List<SqliteSearchIndexProject>();

        foreach (var project in projects
                     .Where(project => !unsupportedProjects.Contains(project.Id))
                     .OrderBy(project => NormalizePath(project.FilePath), StringComparer.Ordinal)
                     .ThenBy(project => project.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            RepositoryRelativePath projectPath;
            try
            {
                projectPath = RepositoryRelativePath.FromPhysicalPath(
                    options.RepositoryRoot,
                    project.FilePath,
                    $"Project '{project.Name}'");
            }
            catch (ArgumentException exception)
            {
                issues.Add(new RoslynSearchCorpusBuildIssue("project-path-invalid", exception.Message));
                continue;
            }

            var documents = await BuildDocumentMapAsync(
                project,
                options.RepositoryRoot,
                issues,
                cancellationToken).ConfigureAwait(false);
            catalogProjects.Add(CreateCatalogProject(solution, project, projectPath, options.RepositoryRoot));
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
                    var record = CreateRecord(options.TargetIdentity, project, projectPath, symbol, declaration);
                    if (record is not null)
                    {
                        records.Add(record);
                    }
                }
            }
        }

        var orderedRecords = records
            .DistinctBy(record => record.SymbolKey, StringComparer.Ordinal)
            .OrderBy(record => record.ProjectPath.Value, StringComparer.Ordinal)
            .ThenBy(record => record.Path.Value, StringComparer.Ordinal)
            .ThenBy(record => record.Location.Line)
            .ThenBy(record => record.Location.Column)
            .ThenBy(record => record.SymbolKey, StringComparer.Ordinal)
            .ToArray();

        return new RoslynSearchCorpusBuildResult(
            orderedRecords,
            catalogProjects
                .DistinctBy(project => project.Path)
                .OrderBy(project => project.Path.Value, StringComparer.Ordinal)
                .ToArray(),
            issues.ToArray());
    }

    private static IReadOnlyList<Project> SelectProjects(
        Solution solution,
        string repositoryRoot,
        RepositoryRelativePath? projectPath,
        ICollection<RoslynSearchCorpusBuildIssue> issues)
    {
        var csharpProjects = solution.Projects
            .Where(project => string.Equals(project.Language, LanguageNames.CSharp, StringComparison.Ordinal))
            .ToArray();

        if (projectPath is null)
        {
            return csharpProjects;
        }

        var selectedPath = projectPath.Value.Resolve(repositoryRoot);
        var matches = csharpProjects
            .Where(project => project.FilePath is not null
                && string.Equals(NormalizePath(project.FilePath), selectedPath, PathComparison))
            .ToArray();

        if (matches.Length == 0)
        {
            issues.Add(new RoslynSearchCorpusBuildIssue(
                "project-not-found",
                $"No loaded C# project matches '{projectPath.Value}'."));
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
        string repositoryRoot,
        ICollection<RoslynSearchCorpusBuildIssue> issues,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<SyntaxTree, CorpusDocument>();
        foreach (var document in project.Documents
                     .OrderBy(document => NormalizePath(document.FilePath), StringComparer.Ordinal)
                     .ThenBy(document => document.Name, StringComparer.Ordinal)
                     .ThenBy(document => document.Id.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();

            RepositoryRelativePath documentPath;
            try
            {
                documentPath = RepositoryRelativePath.FromPhysicalPath(
                    repositoryRoot,
                    document.FilePath,
                    $"Source document '{document.Name}' in project '{project.Name}'");
            }
            catch (ArgumentException exception)
            {
                if (await RoslynDocumentFilters.IsPackageInjectedGeneratedSourceAsync(document, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                issues.Add(new RoslynSearchCorpusBuildIssue("document-path-invalid", exception.Message));
                continue;
            }

            if (RoslynDocumentFilters.IsGenerated(document))
            {
                continue;
            }

            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            if (syntaxTree is null)
            {
                continue;
            }

            result.TryAdd(syntaxTree, new CorpusDocument(documentPath));
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
            yield return new CorpusDeclaration(document, node, sourceRange);
        }
    }

    private static RoslynSearchCorpusRecord? CreateRecord(
        RepositoryRelativePath targetIdentity,
        Project project,
        RepositoryRelativePath projectPath,
        ISymbol symbol,
        CorpusDeclaration declaration)
    {
        var symbolId = DocumentationCommentId.CreateDeclarationId(symbol);
        var displayName = symbol.ToDisplayString(SymbolDisplayFormats.QualifiedMember);
        var signature = NormalizeWhitespace(symbol.ToDisplayString(SearchSignatureFormat));
        var documentation = ExtractDocumentation(symbol, declaration.Node);
        var comments = ExtractComments(declaration.Node);
        var structuredComments = ExtractStructuredComments(declaration);
        var attributes = ExtractAttributes(symbol);
        var body = Truncate(NormalizeWhitespace(declaration.Node.ToFullString()), MaximumBodyCharacters);
        var excerpt = SelectExcerpt(documentation, comments, signature, body);
        var documentKey = $"{projectPath.Value}|{declaration.Document.Path.Value}";
        var identity = symbolId ?? $"{symbol.Kind}:{displayName}";
        var symbolKey = $"{documentKey}|{identity}|{declaration.Node.Span.Start}";

        return new RoslynSearchCorpusRecord(
            targetIdentity,
            projectPath,
            project.Name,
            documentKey,
            declaration.Document.Path,
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
            BuildSearchText(declaration.Document.Path.Value),
            BuildSearchText(body),
            symbol.MetadataName,
            symbol.Kind.ToString(),
            symbol.DeclaredAccessibility.ToString(),
            symbol.IsStatic,
            symbol.ContainingType?.ToDisplayString(SymbolDisplayFormats.Qualified),
            symbol.ContainingNamespace is { IsGlobalNamespace: false }
                ? symbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormats.Qualified)
                : null,
            declaration.Node.Span.Start,
            declaration.Node.Span.Length,
            structuredComments,
            CreateRelations(symbol));
    }

    private static SqliteSearchIndexProject CreateCatalogProject(
        Solution solution,
        Project project,
        RepositoryRelativePath projectPath,
        string repositoryRoot)
    {
        var projectReferences = project.ProjectReferences
            .Select(reference => solution.GetProject(reference.ProjectId)?.FilePath)
            .Where(path => path is not null)
            .Select(path => RepositoryRelativePath.FromPhysicalPath(
                repositoryRoot,
                path!,
                $"Project reference from '{project.Name}'"))
            .Distinct()
            .OrderBy(path => path.Value, StringComparer.Ordinal)
            .ToArray();
        return new SqliteSearchIndexProject(projectPath, project.Name, projectReferences);
    }

    private static IReadOnlyList<SqliteSearchIndexComment> ExtractStructuredComments(
        CorpusDeclaration declaration)
    {
        return declaration.Node
            .DescendantTrivia(descendIntoTrivia: true)
            .Concat(declaration.Node.GetLeadingTrivia())
            .Concat(declaration.Node.GetTrailingTrivia())
            .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            .DistinctBy(trivia => trivia.FullSpan)
            .OrderBy(trivia => trivia.FullSpan.Start)
            .Select(trivia =>
            {
                var location = SourceRange.FromLocation(trivia.GetLocation());
                return new SqliteSearchIndexComment(
                    CommentPlacement(declaration.Node, trivia),
                    trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ? "line" : "block",
                    declaration.Document.Path,
                    location.Line,
                    location.Column,
                    location.EndLine,
                    location.EndColumn,
                    NormalizeCommentText(trivia.ToString()) ?? string.Empty);
            })
            .Where(comment => comment.Text.Length > 0)
            .ToArray();
    }

    private static string CommentPlacement(SyntaxNode declaration, SyntaxTrivia trivia)
    {
        if (declaration.GetLeadingTrivia().Any(candidate => candidate.FullSpan == trivia.FullSpan))
        {
            return "leading";
        }

        if (declaration.GetTrailingTrivia().Any(candidate => candidate.FullSpan == trivia.FullSpan))
        {
            return "trailing";
        }

        return "body";
    }

    private static IReadOnlyList<SqliteSearchIndexRelation> CreateRelations(ISymbol symbol)
    {
        var relations = new HashSet<SqliteSearchIndexRelation>();
        AddRelation(relations, "contained-by", symbol.ContainingSymbol);

        if (symbol is INamedTypeSymbol namedType)
        {
            if (namedType.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
            {
                AddRelation(relations, "inherits", baseType);
            }

            foreach (var interfaceType in namedType.Interfaces)
            {
                AddRelation(relations, "implements", interfaceType);
            }
        }

        switch (symbol)
        {
            case IMethodSymbol method:
                AddRelation(relations, "overrides", method.OverriddenMethod);
                foreach (var interfaceMember in method.ExplicitInterfaceImplementations)
                {
                    AddRelation(relations, "implements", interfaceMember);
                }

                break;
            case IPropertySymbol property:
                AddRelation(relations, "overrides", property.OverriddenProperty);
                foreach (var interfaceMember in property.ExplicitInterfaceImplementations)
                {
                    AddRelation(relations, "implements", interfaceMember);
                }

                break;
            case IEventSymbol eventSymbol:
                AddRelation(relations, "overrides", eventSymbol.OverriddenEvent);
                foreach (var interfaceMember in eventSymbol.ExplicitInterfaceImplementations)
                {
                    AddRelation(relations, "implements", interfaceMember);
                }

                break;
        }

        AddImplicitInterfaceRelations(relations, symbol);
        return relations
            .OrderBy(relation => relation.Kind, StringComparer.Ordinal)
            .ThenBy(relation => relation.TargetSymbolId, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddImplicitInterfaceRelations(
        ISet<SqliteSearchIndexRelation> relations,
        ISymbol symbol)
    {
        if (symbol.ContainingType is not { } containingType
            || symbol is not (IMethodSymbol or IPropertySymbol or IEventSymbol))
        {
            return;
        }

        foreach (var interfaceMember in containingType.AllInterfaces.SelectMany(type => type.GetMembers()))
        {
            var implementation = containingType.FindImplementationForInterfaceMember(interfaceMember);
            if (SymbolEqualityComparer.Default.Equals(implementation, symbol)
                || SymbolEqualityComparer.Default.Equals(implementation?.OriginalDefinition, symbol.OriginalDefinition))
            {
                AddRelation(relations, "implements", interfaceMember);
            }
        }
    }

    private static void AddRelation(
        ISet<SqliteSearchIndexRelation> relations,
        string kind,
        ISymbol? target)
    {
        var targetSymbolId = target is null
            ? null
            : DocumentationCommentId.CreateDeclarationId(target);
        if (targetSymbolId is not null)
        {
            relations.Add(new SqliteSearchIndexRelation(kind, targetSymbolId));
        }
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

    private sealed record CorpusDocument(RepositoryRelativePath Path);

    private sealed record CorpusDeclaration(
        CorpusDocument Document,
        SyntaxNode Node,
        SourceRange Location);
}

/// <summary>
/// Specifies repository-local target and project choices for corpus construction.
/// </summary>
internal sealed record RoslynSearchCorpusBuildOptions(
    string RepositoryRoot,
    RepositoryRelativePath TargetIdentity,
    RepositoryRelativePath? ProjectPath = null);

/// <summary>
/// Reports one non-fatal corpus construction limitation that the command layer can render as an actionable failure.
/// </summary>
internal sealed record RoslynSearchCorpusBuildIssue(string Code, string Message);

/// <summary>
/// Contains deterministic source symbol records and any construction issues.
/// </summary>
internal sealed record RoslynSearchCorpusBuildResult(
    IReadOnlyList<RoslynSearchCorpusRecord> Records,
    IReadOnlyList<SqliteSearchIndexProject> Projects,
    IReadOnlyList<RoslynSearchCorpusBuildIssue> Issues);

/// <summary>
/// Represents one source declaration together with its weighted search fields.
/// </summary>
internal sealed record RoslynSearchCorpusRecord(
    RepositoryRelativePath TargetIdentity,
    RepositoryRelativePath ProjectPath,
    string ProjectName,
    string DocumentKey,
    RepositoryRelativePath Path,
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
    string BodyTokens,
    string MetadataName,
    string SymbolKind,
    string Accessibility,
    bool IsStatic,
    string? ContainingType,
    string? ContainingNamespace,
    int SpanStart,
    int SpanLength,
    IReadOnlyList<SqliteSearchIndexComment> StructuredComments,
    IReadOnlyList<SqliteSearchIndexRelation> Relations)
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
            BodyTokens,
            MetadataName,
            SymbolKind,
            Accessibility,
            IsStatic,
            ContainingType,
            ContainingNamespace,
            SpanStart,
            SpanLength,
            StructuredComments,
            Relations);
    }
}
