namespace RoslynKit.Tests;

/// <summary>
/// Verifies the repo-local RoslynKit skill wrapper routes agent-facing operations to the expected commands.
/// </summary>
public sealed class SkillWrapperTests
{
    [Fact]
    public async Task Wrapper_Workspace_ResolvesNearestTarget_WhenTargetIsOmitted()
    {
        using var envelope = await TestPaths.ExecuteWrapperEnvelopeAsync(
            TestPaths.RepoFile("tests", "FixtureWorkspace", "App"),
            "-Operation", "workspace");

        Assert.True(envelope.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("workspace", envelope.RootElement.GetProperty("command").GetString());
        Assert.Contains(
            envelope.RootElement.GetProperty("data").GetProperty("documents").EnumerateArray().Select(document => document.GetProperty("name").GetString()),
            name => string.Equals(name, "Source.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wrapper_BodyRead_UsesDocumentText()
    {
        using var envelope = await TestPaths.ExecuteWrapperEnvelopeAsync(
            TestPaths.RepositoryRoot(),
            "-Operation", "body-read",
            "-Target", TestPaths.SolutionPath(),
            "-Path", TestPaths.RepoFile("src", "RoslynKit", "Program.cs"),
            "-StartLine", "1",
            "-EndLine", "10");

        Assert.True(envelope.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("document-text", envelope.RootElement.GetProperty("command").GetString());
        Assert.Equal("Program.cs", envelope.RootElement.GetProperty("data").GetProperty("document").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Wrapper_Implementations_UsesImplementations()
    {
        var sourcePath = TestPaths.RepoFile("tests", "FixtureWorkspace", "App", "Source.cs");
        var (line, column) = TestPaths.FindLineAndColumn(sourcePath, "IMessageSource _source");

        using var envelope = await TestPaths.ExecuteWrapperEnvelopeAsync(
            TestPaths.RepositoryRoot(),
            "-Operation", "implementations",
            "-Target", TestPaths.FixtureProjectPath(),
            "-Path", sourcePath,
            "-Line", line.ToString(),
            "-Column", column.ToString());

        Assert.True(envelope.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("implementations", envelope.RootElement.GetProperty("command").GetString());
        Assert.Contains(
            envelope.RootElement.GetProperty("data").GetProperty("symbols").EnumerateArray().Select(symbol => symbol.GetProperty("name").GetString()),
            name => string.Equals(name, "GeneratedMessageSource", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wrapper_QuickInfo_UsesQuickInfo()
    {
        var programPath = TestPaths.RepoFile("src", "RoslynKit", "Program.cs");
        var (line, column) = TestPaths.FindLineAndColumn(programPath, "CliApplication");

        using var envelope = await TestPaths.ExecuteWrapperEnvelopeAsync(
            TestPaths.RepositoryRoot(),
            "-Operation", "quick-info",
            "-Target", TestPaths.SolutionPath(),
            "-Path", programPath,
            "-Line", line.ToString(),
            "-Column", column.ToString());

        Assert.True(envelope.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("quick-info", envelope.RootElement.GetProperty("command").GetString());
        Assert.Contains(
            envelope.RootElement.GetProperty("data").GetProperty("sections").EnumerateArray().Select(section => section.GetProperty("text").GetString()),
            text => text is not null && text.Contains("CliApplication.CliApplication", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wrapper_GeneratedDocumentRead_UsesDocumentText()
    {
        using var workspaceEnvelope = await TestPaths.ExecuteWrapperEnvelopeAsync(
            TestPaths.RepositoryRoot(),
            "-Operation", "workspace",
            "-Target", TestPaths.FixtureProjectPath(),
            "-IncludeGenerated");
        var generatedDocumentKey = workspaceEnvelope.RootElement
            .GetProperty("data")
            .GetProperty("documents")
            .EnumerateArray()
            .First(document => document.GetProperty("documentKind").GetString() == DocumentKindNames.SourceGenerated)
            .GetProperty("documentKey")
            .GetString();

        Assert.False(string.IsNullOrWhiteSpace(generatedDocumentKey));

        using var envelope = await TestPaths.ExecuteWrapperEnvelopeAsync(
            TestPaths.RepositoryRoot(),
            "-Operation", "generated-document-read",
            "-Target", TestPaths.FixtureProjectPath(),
            "-DocumentKey", generatedDocumentKey!);

        Assert.True(envelope.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("document-text", envelope.RootElement.GetProperty("command").GetString());
        Assert.Equal(DocumentKindNames.SourceGenerated, envelope.RootElement.GetProperty("data").GetProperty("document").GetProperty("documentKind").GetString());
    }
}
