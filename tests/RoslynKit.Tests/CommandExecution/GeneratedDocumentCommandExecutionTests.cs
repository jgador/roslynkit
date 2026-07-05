namespace RoslynKit.Tests;

/// <summary>
/// Verifies generated-document and ambiguous-path command execution.
/// </summary>
public sealed partial class CommandExecutionTests
{
    [Fact]
    public async Task DocumentText_FileSelector_ReadsFullGeneratedDocument()
    {
        var workspace = await TestPaths.ExecuteCommandAsync<WorkspaceResult>(
            "workspace",
            "--target", TestPaths.FixtureProjectPath(),
            "--include-generated");
        var generatedDocument = workspace.Documents.First(document => document.DocumentKind == DocumentKindNames.SourceGenerated);
        Assert.NotNull(generatedDocument.Path);
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(TestPaths.FixtureProjectPath(), TestContext.Current.CancellationToken);
        var context = await loaded.FindTextDocumentAsync(generatedDocument.Path, null, null, DocumentKindNames.SourceGenerated, "document-text", TestContext.Current.CancellationToken);
        var expectedText = (await context.TextDocument.GetTextAsync(TestContext.Current.CancellationToken)).ToString();

        var result = await TestPaths.ExecuteCommandAsync<DocumentTextResult>(
            "document-text",
            "--target", TestPaths.FixtureProjectPath(),
            "--file", generatedDocument.Path!,
            "--document-kind", DocumentKindNames.SourceGenerated);

        Assert.Equal(DocumentKindNames.SourceGenerated, result.Document.DocumentKind);
        Assert.Equal(expectedText, result.Text);
        AssertWholeDocumentRange(result.ResolvedRange, expectedText);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task DocumentText_AmbiguousFilePath_ListsProjectTfmKindAndPath()
    {
        var fixture = CreateAmbiguousPathFixture();

        var exception = await Assert.ThrowsAsync<CliUsageException>(() => TestPaths.ExecuteCommandAsync<DocumentTextResult>(
            "document-text",
            "--target", fixture.SolutionPath,
            "--file", fixture.SharedSourcePath));

        Assert.Equal("document-text", exception.CommandName);
        Assert.Contains("multiple document contexts", exception.Message, StringComparison.Ordinal);
        var hint = exception.Hint;
        Assert.NotNull(hint);
        Assert.Contains("--project", hint!, StringComparison.Ordinal);
        Assert.Contains("--tfm", hint!, StringComparison.Ordinal);
        Assert.Contains("--document-kind", hint!, StringComparison.Ordinal);
        Assert.Contains("ProjectA.csproj", hint!, StringComparison.Ordinal);
        Assert.Contains("ProjectB.csproj", hint!, StringComparison.Ordinal);
        Assert.Contains("net10.0", hint!, StringComparison.Ordinal);
        Assert.Contains("netstandard2.1", hint!, StringComparison.Ordinal);
        Assert.Contains("Shared.cs", hint!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentText_ContextOptions_DisambiguateFilePath()
    {
        var fixture = CreateAmbiguousPathFixture();

        var projectResult = await TestPaths.ExecuteCommandAsync<DocumentTextResult>(
            "document-text",
            "--target", fixture.SolutionPath,
            "--file", fixture.SharedSourcePath,
            "--project", fixture.ProjectAPath);
        var tfmResult = await TestPaths.ExecuteCommandAsync<DocumentTextResult>(
            "document-text",
            "--target", fixture.SolutionPath,
            "--file", fixture.SharedSourcePath,
            "--tfm", "netstandard2.1");

        Assert.Equal("ProjectA", projectResult.Document.ProjectName);
        Assert.Equal("net10.0", projectResult.Document.TargetFramework);
        Assert.Equal("ProjectB", tfmResult.Document.ProjectName);
        Assert.Equal("netstandard2.1", tfmResult.Document.TargetFramework);
    }
}
