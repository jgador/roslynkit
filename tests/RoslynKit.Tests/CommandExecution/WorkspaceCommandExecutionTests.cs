namespace RoslynKit.Tests;

/// <summary>
/// Verifies workspace command execution against repo and fixture targets.
/// </summary>
public sealed partial class CommandExecutionTests
{
    [Fact]
    public async Task Workspace_WithoutTarget_LoadsRepositoryProjectForest()
    {
        var result = await TestPaths.ExecuteCommandAsync<WorkspaceResult>("workspace");

        Assert.Equal(TestPaths.RepositoryRoot(), result.TargetPath);
        Assert.Equal("repository", result.TargetKind);
        Assert.Contains(result.Projects, project => project.Path == TestPaths.RepoFile("src", "RoslynKit", "RoslynKit.csproj"));
        Assert.Contains(result.Projects, project => project.Path == TestPaths.RepoFile("tests", "RoslynKit.Tests", "RoslynKit.Tests.csproj"));
        Assert.Contains(result.Projects, project => project.Path == TestPaths.RepoFile("tests", "Integration", "Benchmarking", "RoslynKit.Benchmarking.csproj"));
    }

    [Fact]
    public async Task Workspace_DefaultOutput_ListsRepoRelevantSourceDocumentsOnly()
    {
        var result = await TestPaths.ExecuteCommandAsync<WorkspaceResult>(
            "workspace",
            "--target", TestPaths.FixtureProjectPath());

        Assert.NotEmpty(result.Documents);
        Assert.All(result.Documents, document => Assert.Equal(DocumentKindNames.Source, document.DocumentKind));
        Assert.DoesNotContain(result.Documents, document => string.Equals(document.Name, "notes.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Documents, document => string.Equals(document.Name, ".editorconfig", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Workspace_IncludeFlags_AddGeneratedAdditionalAndAnalyzerConfigDocuments()
    {
        var result = await TestPaths.ExecuteCommandAsync<WorkspaceResult>(
            "workspace",
            "--target", TestPaths.FixtureProjectPath(),
            "--include-generated",
            "--include-additional",
            "--include-analyzer-config");

        Assert.Contains(result.Documents, document => document.DocumentKind == DocumentKindNames.SourceGenerated);
        Assert.Contains(result.Documents, document => document.DocumentKind == DocumentKindNames.Additional && document.Name == "notes.txt");
        Assert.Contains(result.Documents, document => document.DocumentKind == DocumentKindNames.AnalyzerConfig && document.Name == ".editorconfig");
    }

    [Fact]
    public async Task Workspace_RendersRootContainedDocumentPathsRelative()
    {
        var result = await TestPaths.ExecuteCommandAsync<WorkspaceResult>(
            "workspace",
            "--target", TestPaths.FixtureProjectPath());

        var source = Assert.Single(result.Documents, document => document.Name == "Source.cs");

        Assert.Equal(Path.Combine("tests", "FixtureWorkspace", "App", "Source.cs"), source.DisplayPath);
        Assert.Equal(Path.Combine("tests", "FixtureWorkspace", "App", "App.csproj"), source.DisplayProjectPath);
    }

    [Fact]
    public async Task Workspace_CallerOwnedLoader_DoesNotReloadOrDisposeWorkspace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(TestPaths.FixtureProjectPath(), cancellationToken);
        var missingTarget = TestPaths.RepoFile("artifacts", "missing", "Missing.csproj");
        var command = CliParser.Parse(["workspace", "--target", missingTarget]);

        var first = Assert.IsType<WorkspaceResult>(
            await RoslynCommandExecutor.ExecuteAsync(command, loaded, cancellationToken));
        var second = Assert.IsType<WorkspaceResult>(
            await RoslynCommandExecutor.ExecuteAsync(command, loaded, cancellationToken));

        Assert.Equal(loaded.TargetPath, first.TargetPath);
        Assert.Equal(first.TargetPath, second.TargetPath);
        Assert.NotEmpty(second.Documents);
    }
}
