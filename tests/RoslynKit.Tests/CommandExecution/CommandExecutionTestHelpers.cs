namespace RoslynKit.Tests;

/// <summary>
/// Provides shared helpers for command execution tests.
/// </summary>
public sealed partial class CommandExecutionTests
{
    private static void AssertWholeDocumentRange(DocumentRange range, string text)
    {
        var lines = text.Split('\n');

        Assert.Equal(1, range.Line);
        Assert.Equal(1, range.Column);
        Assert.Equal(lines.Length, range.EndLine);
        Assert.Equal(lines[^1].TrimEnd('\r').Length + 1, range.EndColumn);
    }

    private static AmbiguousPathFixture CreateAmbiguousPathFixture()
    {
        var root = Path.Combine(TestPaths.RepositoryRoot(), "artifacts", "path-first-document-selection", Guid.NewGuid().ToString("N"));
        var projectADirectory = Path.Combine(root, "ProjectA");
        var projectBDirectory = Path.Combine(root, "ProjectB");
        Directory.CreateDirectory(projectADirectory);
        Directory.CreateDirectory(projectBDirectory);

        var sharedSourcePath = Path.Combine(root, "Shared.cs");
        File.WriteAllText(sharedSourcePath, "namespace AmbiguousFixture;\n\npublic sealed class Shared\n{\n    public string Value => \"shared\";\n}\n");

        var projectAPath = Path.Combine(projectADirectory, "ProjectA.csproj");
        var projectBPath = Path.Combine(projectBDirectory, "ProjectB.csproj");
        File.WriteAllText(projectAPath, CreateSharedCompileProject("net10.0"));
        File.WriteAllText(projectBPath, CreateSharedCompileProject("netstandard2.1"));

        var solutionPath = Path.Combine(root, "Ambiguous.slnx");
        File.WriteAllText(solutionPath, """
            <Solution>
              <Project Path="ProjectA/ProjectA.csproj" />
              <Project Path="ProjectB/ProjectB.csproj" />
            </Solution>
            """);

        return new AmbiguousPathFixture(solutionPath, projectAPath, projectBPath, sharedSourcePath);
    }

    private static string CreateSharedCompileProject(string targetFramework)
    {
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{{targetFramework}}</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="../Shared.cs" Link="Shared.cs" />
              </ItemGroup>
            </Project>
            """;
    }

    private sealed record AmbiguousPathFixture(
        string SolutionPath,
        string ProjectAPath,
        string ProjectBPath,
        string SharedSourcePath);
}
