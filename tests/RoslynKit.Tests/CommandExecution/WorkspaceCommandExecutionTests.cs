namespace RoslynKit.Tests;

/// <summary>
/// Verifies workspace command execution against repo and fixture targets.
/// </summary>
public sealed partial class CommandExecutionTests
{
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
}
