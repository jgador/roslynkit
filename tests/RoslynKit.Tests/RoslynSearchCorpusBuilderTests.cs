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
            CreateBuildOptions(),
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Issues);
        var messageMethod = Assert.Single(result.Records, record => record.SymbolId is not null
            && record.SymbolId.StartsWith("M:FixtureApp.IMessageSource.GetMessage(System.String)", StringComparison.Ordinal));
        var namespaceRecords = result.Records
            .Where(record => record.Kind == "namespace" && record.Name == "FixtureApp")
            .ToArray();

        Assert.Equal("tests/FixtureWorkspace/App/App.csproj", messageMethod.TargetIdentity.Value);
        Assert.Equal("method", messageMethod.Kind);
        Assert.StartsWith("M:FixtureApp.IMessageSource.GetMessage(System.String)", messageMethod.SymbolId, StringComparison.Ordinal);
        Assert.Equal("tests/FixtureWorkspace/App/App.csproj", messageMethod.ProjectPath.Value);
        Assert.Equal("tests/FixtureWorkspace/App/Source.cs", messageMethod.Path.Value);
        Assert.Contains("tests/FixtureWorkspace/App/Source.cs", messageMethod.SymbolKey, StringComparison.Ordinal);
        Assert.DoesNotContain(TestPaths.RepositoryRoot(), messageMethod.SymbolKey, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("tests fixtureworkspace", messageMethod.PathTokens, StringComparison.Ordinal);
        Assert.Contains("app", messageMethod.PathTokens, StringComparison.Ordinal);
        Assert.Contains("source", messageMethod.PathTokens, StringComparison.Ordinal);
        Assert.DoesNotContain("github", messageMethod.PathTokens, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("roslynkit", messageMethod.PathTokens, StringComparison.OrdinalIgnoreCase);
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
    public async Task BuildAsync_ProjectPathSelectorAcceptsCanonicalRepositoryRelativePath()
    {
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(TestPaths.FixtureProjectPath(), TestContext.Current.CancellationToken);
        var builder = new RoslynSearchCorpusBuilder();
        var cancellationToken = TestContext.Current.CancellationToken;
        var project = Assert.Single(loaded.Solution.Projects);
        var projectPath = RepositoryRelativePath.FromPhysicalPath(
            TestPaths.RepositoryRoot(),
            project.FilePath,
            "fixture project");

        var byPath = await builder.BuildAsync(
            loaded.Solution,
            CreateBuildOptions(projectPath),
            cancellationToken);

        Assert.Empty(byPath.Issues);
        Assert.NotEmpty(byPath.Records);
        Assert.All(byPath.Records, record => Assert.Equal(projectPath, record.ProjectPath));
    }

    [Fact]
    public async Task BuildAsync_ExcludesBinAndObjSourceDocuments()
    {
        using var workspace = new AdhocWorkspace();
        var directoryPath = TestPaths.RepoFile("artifacts", "corpus-builder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        var projectPath = Path.Combine(directoryPath, "GeneratedPaths.csproj");
        var regularPath = Path.Combine(directoryPath, "Regular.cs");
        var binPath = Path.Combine(directoryPath, "bin", "GeneratedBinSymbol.cs");
        var objPath = Path.Combine(directoryPath, "obj", "GeneratedObjSymbol.cs");
        var projectId = ProjectId.CreateNewId("GeneratedPaths");
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(objPath)!);
            await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />", cancellationToken);
            await File.WriteAllTextAsync(regularPath, "namespace GeneratedPaths; public sealed class RegularSearchSymbol { public void ExecuteAsync() { } }", cancellationToken);
            await File.WriteAllTextAsync(binPath, "namespace GeneratedPaths; public sealed class GeneratedBinSymbol { }", cancellationToken);
            await File.WriteAllTextAsync(objPath, "namespace GeneratedPaths; public sealed class GeneratedObjSymbol { }", cancellationToken);

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
                    filePath: regularPath)
                .AddDocument(
                    DocumentId.CreateNewId(projectId),
                    "GeneratedBinSymbol.cs",
                    "namespace GeneratedPaths; public sealed class GeneratedBinSymbol { }",
                    filePath: binPath)
                .AddDocument(
                    DocumentId.CreateNewId(projectId),
                    "GeneratedObjSymbol.cs",
                    "namespace GeneratedPaths; public sealed class GeneratedObjSymbol { }",
                    filePath: objPath);

            var result = await new RoslynSearchCorpusBuilder().BuildAsync(
                solution,
                new RoslynSearchCorpusBuildOptions(
                    TestPaths.RepositoryRoot(),
                    RepositoryRelativePath.FromPhysicalPath(TestPaths.RepositoryRoot(), projectPath, "search target")),
                cancellationToken);

            Assert.Empty(result.Issues);
            Assert.Contains(result.Records, record => record.Name == "RegularSearchSymbol");
            Assert.DoesNotContain(result.Records, record => record.Name == "GeneratedBinSymbol");
            Assert.DoesNotContain(result.Records, record => record.Name == "GeneratedObjSymbol");
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_ReportsIssueForMissingPhysicalProjectPath()
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("MissingProject");
        var missingProjectPath = TestPaths.RepoFile(
            "artifacts",
            "corpus-builder",
            Guid.NewGuid().ToString("N"),
            "MissingProject.csproj");
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "MissingProject",
                "MissingProject",
                LanguageNames.CSharp,
                filePath: missingProjectPath,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddDocument(
                DocumentId.CreateNewId(projectId),
                "ExistingSource.cs",
                "namespace MissingProject; public sealed class ExistingSource { }",
                filePath: TestPaths.RepoFile("tests", "FixtureWorkspace", "App", "Source.cs"));

        var result = await new RoslynSearchCorpusBuilder().BuildAsync(
            solution,
            CreateBuildOptions(),
            TestContext.Current.CancellationToken);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("project-path-invalid", issue.Code);
        Assert.Contains("physical", issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Records);
    }

    [Fact]
    public async Task BuildAsync_ReportsIssuesForExternalGeneratedOrMissingPhysicalDocumentPaths()
    {
        using var loaded = await RoslynWorkspaceLoader.LoadAsync(TestPaths.FixtureProjectPath(), TestContext.Current.CancellationToken);
        var project = Assert.Single(loaded.Solution.Projects);
        var externalDocumentPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.g.cs");
        var missingDocumentPath = TestPaths.RepoFile(
            "artifacts",
            "corpus-builder",
            Guid.NewGuid().ToString("N"),
            "MissingSource.cs");
        try
        {
            await File.WriteAllTextAsync(
                externalDocumentPath,
                "namespace ExternalSource; public sealed class ExternalDocumentSymbol { }",
                TestContext.Current.CancellationToken);

            var solution = loaded.Solution
                .AddDocument(
                    DocumentId.CreateNewId(project.Id),
                    "ExternalSource.g.cs",
                    "namespace ExternalSource; public sealed class ExternalDocumentSymbol { }",
                    filePath: externalDocumentPath)
                .AddDocument(
                    DocumentId.CreateNewId(project.Id),
                    "MissingSource.cs",
                    "namespace MissingSource; public sealed class MissingDocumentSymbol { }",
                    filePath: missingDocumentPath);

            var result = await new RoslynSearchCorpusBuilder().BuildAsync(
                solution,
                CreateBuildOptions(),
                TestContext.Current.CancellationToken);

            Assert.Contains(result.Issues, issue => issue.Code == "document-path-invalid"
                && issue.Message.Contains("outside", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Issues, issue => issue.Code == "document-path-invalid"
                && issue.Message.Contains("physical", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result.Records, record => record.Name == "ExternalDocumentSymbol");
            Assert.DoesNotContain(result.Records, record => record.Name == "MissingDocumentSymbol");
        }
        finally
        {
            File.Delete(externalDocumentPath);
        }
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
            CreateBuildOptions(),
            TestContext.Current.CancellationToken);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("multiple-target-frameworks", issue.Code);
        Assert.Empty(result.Records);
        Assert.Contains("one target framework", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static RoslynSearchCorpusBuildOptions CreateBuildOptions(RepositoryRelativePath? projectPath = null)
    {
        return new RoslynSearchCorpusBuildOptions(
            TestPaths.RepositoryRoot(),
            RepositoryRelativePath.FromStoredValue("tests/FixtureWorkspace/App/App.csproj", "search target"),
            projectPath);
    }
}
