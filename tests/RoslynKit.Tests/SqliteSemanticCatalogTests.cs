using Microsoft.Data.Sqlite;

namespace RoslynKit.Tests;

public sealed class SqliteSemanticCatalogTests
{
    [Fact]
    public async Task ReplaceTarget_PersistsNavigableSymbolsRelationshipsCommentsAndProjects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "roslynkit-tests",
            "semantic-catalog",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var databasePath = Path.Combine(rootPath, "roslynkit.db");

        try
        {
            var target = Relative("__repository__");
            var project = Relative("src/App/App.csproj");
            var referencedProject = Relative("src/Core/Core.csproj");
            var interfacePath = Relative("src/App/IWorker.cs");
            var implementationPath = Relative("src/App/Worker.cs");
            var methodPath = Relative("src/App/Runner.cs");
            var interfaceSymbol = CreateSymbol(
                target,
                project,
                interfacePath,
                "T:Fixture.IWorker",
                "IWorker",
                "Fixture.IWorker",
                "interface");
            var implementationSymbol = CreateSymbol(
                target,
                project,
                implementationPath,
                "T:Fixture.Worker",
                "Worker",
                "Fixture.Worker",
                "class") with
            {
                Relations = [new SqliteSearchIndexRelation("implements", "T:Fixture.IWorker")],
            };
            var methodSymbol = CreateSymbol(
                target,
                project,
                methodPath,
                "M:Fixture.Runner.Run~System.Int32",
                "Run",
                "Fixture.Runner.Run",
                "method") with
            {
                StructuredComments =
                [
                    new SqliteSearchIndexComment(
                        "leading",
                        "line",
                        methodPath,
                        1,
                        1,
                        1,
                        17,
                        "Runs the fixture."),
                ],
                SpanStart = 20,
                SpanLength = 24,
            };
            var index = new SqliteSearchIndex(databasePath);

            await index.ReplaceTargetAsync(
                new SqliteSearchIndexTarget(target, "fingerprint"),
                [interfaceSymbol, implementationSymbol, methodSymbol],
                [new SqliteSearchIndexProject(project, "App", [referencedProject])],
                cancellationToken);

            Assert.True(await index.HasCatalogTargetAsync(target, cancellationToken));
            var methodMatches = await index.ReadCatalogSymbolsAsync(
                target,
                "M:Fixture.Runner.Run",
                cancellationToken);
            var method = Assert.Single(methodMatches);
            Assert.Equal(20, method.SpanStart);
            Assert.Equal("Runs the fixture.", Assert.Single(method.StructuredComments!).Text);

            var implementations = await index.ReadCatalogImplementationsAsync(
                target,
                "T:Fixture.IWorker",
                cancellationToken);
            Assert.Equal("Fixture.Worker", Assert.Single(implementations).DisplayName);

            await index.WriteCatalogOperationAsync(
                target,
                "references\u001fM:Fixture.Runner.Run",
                "references",
                1,
                """{"totalCount":1}""",
                cancellationToken);
            Assert.Equal(
                """{"totalCount":1}""",
                await index.ReadCatalogOperationAsync(
                    target,
                    "references\u001fM:Fixture.Runner.Run",
                    "references",
                    1,
                    cancellationToken));

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT project_references_json
                FROM semantic_catalog_projects
                WHERE target_identity = '__repository__'
                  AND project_path = 'src/App/App.csproj';
                """;
            Assert.Equal(
                """["src/Core/Core.csproj"]""",
                await command.ExecuteScalarAsync(cancellationToken));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static SqliteSearchIndexSymbol CreateSymbol(
        RepositoryRelativePath target,
        RepositoryRelativePath project,
        RepositoryRelativePath path,
        string symbolId,
        string name,
        string displayName,
        string kind)
    {
        return new SqliteSearchIndexSymbol(
            $"{target.Value}|{project.Value}|{path.Value}|{symbolId}|0",
            project,
            "App",
            kind,
            name,
            displayName,
            symbolId,
            path,
            1,
            1,
            1,
            name.Length + 1,
            Documentation: null,
            Signature: displayName,
            Comments: null,
            Body: null,
            NameTokens: name,
            ContainingTokens: displayName,
            DetailsTokens: displayName,
            PathTokens: path.Value,
            BodyTokens: string.Empty,
            MetadataName: name,
            SymbolKind: kind == "class" || kind == "interface" ? "NamedType" : "Method",
            Accessibility: "Public");
    }

    private static RepositoryRelativePath Relative(string value)
    {
        return RepositoryRelativePath.FromStoredValue(value, "Test path");
    }
}
