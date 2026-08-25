namespace RoslynKit.Tests;

/// <summary>
/// Verifies deterministic syntax, metadata, and semantic-navigation context for intent-search fixtures.
/// </summary>
public sealed partial class CommandExecutionTests
{
    private const string DiagnosticValidatorMethodId = "M:IntentNavigation.DiagnosticReportValidator.ValidateDiagnosticReport(IntentNavigation.DiagnosticReport)~IntentNavigation.DiagnosticValidationResult";
    private const string DiagnosticValidatorTypeId = "T:IntentNavigation.DiagnosticReportValidator";
    private const string DiagnosticValidatorInterfaceMethodId = "M:IntentNavigation.IDiagnosticReportValidator.ValidateDiagnosticReport(IntentNavigation.DiagnosticReport)~IntentNavigation.DiagnosticValidationResult";
    private const string DiagnosticBootstrapPublishMethodId = "M:IntentNavigation.DiagnosticBootstrap.Publish(IntentNavigation.DiagnosticReport)~IntentNavigation.DiagnosticValidationResult";

    [Fact]
    public async Task SymbolContext_ByMethodSelector_ReturnsDocumentationCommentsDescendantsAndTruncation()
    {
        var complete = await TestPaths.ExecuteCommandAsync<SymbolContextResult>(
            "symbol-context",
            "--target", TestPaths.IntentNavigationProjectPath(),
            "--symbol", DiagnosticValidatorMethodId,
            "--max-results", "20",
            "--max-comments", "20");

        Assert.Null(complete.Document);
        Assert.Null(complete.Line);
        Assert.Null(complete.Column);
        Assert.Equal(DiagnosticValidatorMethodId, complete.Selector);
        Assert.Equal("MethodDeclaration", complete.SelectedNode.Kind);
        Assert.Equal(DiagnosticValidatorMethodId, complete.Symbol.SymbolId);
        Assert.Contains("Rejects fatal diagnostic reports", complete.Documentation!, StringComparison.Ordinal);
        Assert.Contains(complete.Comments, comment =>
            comment.Placement == "leading"
            && comment.Style == "line"
            && comment.Text.Contains("owns the diagnostic-report routing decision", StringComparison.Ordinal));
        Assert.Contains(complete.Comments, comment =>
            comment.Placement == "leading"
            && comment.Style == "block"
            && comment.Text.Contains("fatal diagnostics must not reach telemetry", StringComparison.Ordinal));
        Assert.Contains(complete.Comments, comment =>
            comment.Placement == "body"
            && comment.Style == "line"
            && comment.Text.Contains("narrowed to manual review", StringComparison.Ordinal));
        Assert.Contains(complete.Comments, comment =>
            comment.Placement == "body"
            && comment.Style == "block"
            && comment.Text.Contains("routine diagnostics can continue", StringComparison.Ordinal));
        Assert.Contains(complete.Comments, comment =>
            comment.Placement == "trailing"
            && comment.Style == "line"
            && comment.Text.Contains("preserve the selected route", StringComparison.Ordinal));
        Assert.Contains(complete.Descendants, descendant =>
            descendant.Relation == "construction"
            && descendant.SyntaxKind == "ObjectCreationExpression"
            && descendant.TargetSymbolId?.StartsWith("M:IntentNavigation.DiagnosticValidationResult.#ctor", StringComparison.Ordinal) == true);
        Assert.True(complete.TotalDescendantCount > 1);
        Assert.Equal(complete.TotalDescendantCount, complete.ReturnedDescendantCount);
        Assert.False(complete.DescendantsTruncated);
        Assert.True(complete.TotalCommentCount >= 5);
        Assert.Equal(complete.TotalCommentCount, complete.ReturnedCommentCount);
        Assert.False(complete.CommentsTruncated);

        var bounded = await TestPaths.ExecuteCommandAsync<SymbolContextResult>(
            "symbol-context",
            "--target", TestPaths.IntentNavigationProjectPath(),
            "--symbol", DiagnosticValidatorMethodId,
            "--max-results", "1",
            "--max-comments", "2");

        Assert.Equal(1, bounded.ReturnedDescendantCount);
        Assert.True(bounded.DescendantsTruncated);
        Assert.Equal(2, bounded.ReturnedCommentCount);
        Assert.True(bounded.CommentsTruncated);
    }

    [Fact]
    public async Task SymbolContext_ByTypeSelector_ReturnsAlternatePartialDeclaration()
    {
        var result = await TestPaths.ExecuteCommandAsync<SymbolContextResult>(
            "symbol-context",
            "--target", TestPaths.IntentNavigationProjectPath(),
            "--symbol", DiagnosticValidatorTypeId);

        Assert.Equal(DiagnosticValidatorTypeId, result.Symbol.SymbolId);
        Assert.Equal("ClassDeclaration", result.SelectedNode.Kind);
        var alternate = Assert.Single(result.AlternateDeclarations);
        Assert.EndsWith("DiagnosticReportValidator.cs", Assert.IsType<string>(alternate.Path), StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(result.SelectedNode.Location.Path, alternate.Path);
    }

    [Fact]
    public async Task SymbolContext_ChainsSearchIdentityReferencesImplementationAndCallerPosition()
    {
        await using var area = IntentNavigationSearchArea.Create();
        var search = await ExecuteIntentSearchAsync(
            area,
            "where are fatal diagnostic reports routed before telemetry publication",
            "--max-results", "20");
        var hit = Assert.Single(search.Hits, candidate => candidate.SymbolId == DiagnosticValidatorMethodId);

        var searchedContext = await TestPaths.ExecuteCommandAsync<SymbolContextResult>(
            "symbol-context",
            "--target", TestPaths.IntentNavigationProjectPath(),
            "--symbol", Assert.IsType<string>(hit.SymbolId));
        Assert.Equal(DiagnosticValidatorMethodId, searchedContext.Symbol.SymbolId);

        var references = await TestPaths.ExecuteCommandAsync<ReferencesResult>(
            "references",
            "--target", TestPaths.IntentNavigationProjectPath(),
            "--symbol", searchedContext.Symbol.SymbolId!);
        var callerReference = Assert.Single(references.Locations);
        var callerReferencePath = Assert.IsType<string>(callerReference.Path);
        Assert.EndsWith("DiagnosticBootstrap.cs", callerReferencePath, StringComparison.OrdinalIgnoreCase);

        var callerContext = await TestPaths.ExecuteCommandAsync<SymbolContextResult>(
            "symbol-context",
            "--target", TestPaths.IntentNavigationProjectPath(),
            "--file", callerReferencePath,
            "--line", callerReference.Line.ToString(),
            "--column", callerReference.Column.ToString());

        Assert.Equal(DiagnosticValidatorInterfaceMethodId, callerContext.Symbol.SymbolId);
        var invocationIndex = FindAncestorIndex(callerContext.Ancestors, "InvocationExpression");
        var methodIndex = FindAncestorIndex(callerContext.Ancestors, "MethodDeclaration");
        Assert.True(invocationIndex >= 0, "The reference location should have an invocation ancestor.");
        Assert.True(methodIndex > invocationIndex, "Ancestors should be ordered nearest first and reach the bootstrap method.");
        var callerMethod = callerContext.Ancestors[methodIndex];
        Assert.Equal("DiagnosticBootstrap.cs", Path.GetFileName(Assert.IsType<string>(callerMethod.Location.Path)));
        Assert.Equal(DiagnosticBootstrapPublishMethodId, callerMethod.SymbolId);
        Assert.Equal("Publish", callerMethod.SymbolDisplayName);

        var bootstrapContext = await TestPaths.ExecuteCommandAsync<SymbolContextResult>(
            "symbol-context",
            "--target", TestPaths.IntentNavigationProjectPath(),
            "--symbol", DiagnosticBootstrapPublishMethodId);
        var invocation = Assert.Single(
            bootstrapContext.Descendants,
            descendant => descendant.Relation == "invocation" && descendant.TargetSymbolId == DiagnosticValidatorInterfaceMethodId);
        Assert.Equal("InvocationExpression", invocation.SyntaxKind);

        var implementations = await TestPaths.ExecuteCommandAsync<ImplementationsResult>(
            "implementations",
            "--target", TestPaths.IntentNavigationProjectPath(),
            "--symbol", Assert.IsType<string>(invocation.TargetSymbolId));
        Assert.Contains(implementations.Symbols, implementation => implementation.SymbolId == DiagnosticValidatorMethodId);
    }

    private static int FindAncestorIndex(IReadOnlyList<SyntaxContextNode> ancestors, string kind)
    {
        for (var index = 0; index < ancestors.Count; index++)
        {
            if (ancestors[index].Kind == kind)
            {
                return index;
            }
        }

        return -1;
    }

    private static async Task<SearchResult> ExecuteIntentSearchAsync(
        IntentNavigationSearchArea area,
        string query,
        params string[] additionalArguments)
    {
        var arguments = new List<string>
        {
            "search",
            "--target", TestPaths.IntentNavigationProjectPath(),
            "--index-path", area.DatabasePath,
            "--query", query,
        };
        arguments.AddRange(additionalArguments);

        return await TestPaths.ExecuteCommandAsync<SearchResult>([.. arguments]);
    }

    private sealed class IntentNavigationSearchArea : IAsyncDisposable
    {
        private IntentNavigationSearchArea(string directoryPath)
        {
            DirectoryPath = directoryPath;
        }

        public string DirectoryPath { get; }

        public string DatabasePath => Path.Combine(DirectoryPath, "roslynkit.db");

        public static IntentNavigationSearchArea Create()
        {
            var directoryPath = TestPaths.RepoFile(
                "artifacts",
                "intent-navigation-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            return new IntentNavigationSearchArea(directoryPath);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
