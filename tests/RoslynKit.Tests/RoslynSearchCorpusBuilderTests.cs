using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies conversion of source-declared Roslyn symbols into persistent-search records.
/// </summary>
public sealed class RoslynSearchCorpusBuilderTests
{
    [Fact]
    public async Task BuildAsync_ProducesNavigableDeclarationsAndWeightedSearchFields()
    {
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(TestPaths.FixtureProjectPath(), TestContext.Current.CancellationToken);

        var result = await new RoslynSearchCorpusBuilder().BuildAsync(
            loaded.Solution,
            new RoslynSearchCorpusBuildOptions("fixture-target"),
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Issues);
        var messageMethod = Assert.Single(result.Records, record => record.SymbolId is not null
            && record.SymbolId.StartsWith("M:FixtureApp.IMessageSource.GetMessage(System.String)", StringComparison.Ordinal));
        var namespaceRecords = result.Records
            .Where(record => record.Kind == "namespace" && record.Name == "FixtureApp")
            .ToArray();

        Assert.Equal("fixture-target", messageMethod.TargetIdentity);
        Assert.Equal("method", messageMethod.Kind);
        Assert.StartsWith("M:FixtureApp.IMessageSource.GetMessage(System.String)", messageMethod.SymbolId, StringComparison.Ordinal);
        Assert.Contains("produces", messageMethod.Documentation!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("getmessage", messageMethod.NameTokens, StringComparison.Ordinal);
        Assert.Contains("message", messageMethod.NameTokens, StringComparison.Ordinal);
        Assert.Contains("fixtureapp", messageMethod.ContainingTokens, StringComparison.Ordinal);
        Assert.Contains("string", messageMethod.DetailsTokens, StringComparison.Ordinal);
        Assert.Equal(messageMethod.Documentation, messageMethod.Excerpt);
        Assert.NotEmpty(namespaceRecords);
        Assert.All(namespaceRecords, record => Assert.NotEmpty(record.SymbolKey));
        Assert.Contains(result.Records, record => record.Kind == "interface" && record.Name == "IMessageSource");
        Assert.Contains(result.Records, record => record.Kind == "class" && record.Name == "GeneratedMessageSource");
        Assert.Contains(result.Records, record => record.Kind == "field" && record.Name == "_source");
        Assert.DoesNotContain(result.Records, record => record.Name == "source");

        var storageRecord = messageMethod.ToSqliteSymbol();
        Assert.Equal(messageMethod.SymbolKey, storageRecord.SymbolKey);
        Assert.Equal(messageMethod.Location.Line, storageRecord.Line);
        Assert.Equal(messageMethod.Location.EndLine, storageRecord.EndLine);
        Assert.Equal(messageMethod.BodyTokens, storageRecord.BodyTokens);
    }

    [Fact]
    public async Task BuildAsync_ProjectSelectorAcceptsNameAndNormalizedProjectPath()
    {
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(TestPaths.FixtureProjectPath(), TestContext.Current.CancellationToken);
        var builder = new RoslynSearchCorpusBuilder();
        var cancellationToken = TestContext.Current.CancellationToken;
        var project = Assert.Single(loaded.Solution.Projects);

        var byName = await builder.BuildAsync(
            loaded.Solution,
            new RoslynSearchCorpusBuildOptions("fixture-target", project.Name),
            cancellationToken);
        var byPath = await builder.BuildAsync(
            loaded.Solution,
            new RoslynSearchCorpusBuildOptions("fixture-target", Path.GetFullPath(project.FilePath!)),
            cancellationToken);

        Assert.Empty(byName.Issues);
        Assert.Empty(byPath.Issues);
        Assert.NotEmpty(byName.Records);
        Assert.Equal(
            byName.Records.Select(record => record.SymbolKey),
            byPath.Records.Select(record => record.SymbolKey));
    }

    [Fact]
    public async Task BuildAsync_IncludeGeneratedControlsDocumentsWithGeneratedPaths()
    {
        using var workspace = new AdhocWorkspace();
        var projectPath = Path.Combine(TestPaths.RepositoryRoot(), "artifacts", "corpus-builder", "GeneratedPaths.csproj");
        var projectId = ProjectId.CreateNewId("GeneratedPaths");
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "GeneratedPaths",
                "GeneratedPaths",
                LanguageNames.CSharp,
                filePath: projectPath,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddDocument(
                DocumentId.CreateNewId(projectId),
                "Regular.cs",
                "namespace GeneratedPaths; public sealed class RegularSearchSymbol { public void ExecuteAsync() { } }",
                filePath: Path.Combine(Path.GetDirectoryName(projectPath)!, "Regular.cs"))
            .AddDocument(
                DocumentId.CreateNewId(projectId),
                "GeneratedSearchSymbol.g.cs",
                "namespace GeneratedPaths; public sealed class GeneratedSearchSymbol { }",
                filePath: Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "GeneratedSearchSymbol.g.cs"));
        var builder = new RoslynSearchCorpusBuilder();
        var cancellationToken = TestContext.Current.CancellationToken;

        var withoutGenerated = await builder.BuildAsync(
            solution,
            new RoslynSearchCorpusBuildOptions("generated-path-target"),
            cancellationToken);
        var withGenerated = await builder.BuildAsync(
            solution,
            new RoslynSearchCorpusBuildOptions("generated-path-target", IncludeGenerated: true),
            cancellationToken);

        Assert.Contains(withoutGenerated.Records, record => record.Name == "RegularSearchSymbol");
        Assert.DoesNotContain(withoutGenerated.Records, record => record.Name == "GeneratedSearchSymbol");
        Assert.Contains(withGenerated.Records, record => record.Name == "RegularSearchSymbol");
        Assert.Contains(withGenerated.Records, record => record.Name == "GeneratedSearchSymbol");
        var executeAsync = Assert.Single(withGenerated.Records, record => record.Name == "ExecuteAsync");
        Assert.Equal("executeasync execute", executeAsync.NameTokens);
        Assert.Contains("async", executeAsync.ContainingTokens.Split(' '), StringComparer.Ordinal);
        Assert.Contains("async", executeAsync.DetailsTokens.Split(' '), StringComparer.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_ReportsProjectsLoadedForMultipleTargetFrameworkContexts()
    {
        using var workspace = new AdhocWorkspace();
        var projectPath = Path.Combine(TestPaths.RepositoryRoot(), "artifacts", "corpus-builder", "MultiTarget.csproj");
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId("App (net10.0)"),
                VersionStamp.Create(),
                "App (net10.0)",
                "App",
                LanguageNames.CSharp,
                filePath: projectPath,
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId("App (net11.0)"),
                VersionStamp.Create(),
                "App (net11.0)",
                "App",
                LanguageNames.CSharp,
                filePath: projectPath,
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));

        var result = await new RoslynSearchCorpusBuilder().BuildAsync(
            solution,
            new RoslynSearchCorpusBuildOptions("fixture-target"),
            TestContext.Current.CancellationToken);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("multiple-target-frameworks", issue.Code);
        Assert.Empty(result.Records);
        Assert.Contains("one target framework", issue.Message, StringComparison.OrdinalIgnoreCase);
    }
}
