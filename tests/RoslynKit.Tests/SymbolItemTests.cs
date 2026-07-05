using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies symbol metadata projection from Roslyn symbols into RoslynKit command models.
/// </summary>
public sealed class SymbolItemTests
{
    [Fact]
    public void FromSymbol_NormalizesXmlSummaryDocumentation()
    {
        const string source = """
            namespace Fixture;

            public sealed class Target;

            public sealed class Subject
            {
                /// <summary>
                ///   Uses <see cref="Target"/>
                ///   with <paramref name="value"/> and <c>code</c>.
                /// </summary>
                public void Run(string value)
                {
                }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default.WithDocumentationMode(DocumentationMode.Parse),
            path: "Subject.cs",
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "Fixture",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var type = compilation.GetTypeByMetadataName("Fixture.Subject");
        Assert.NotNull(type);
        var method = Assert.Single(type!.GetMembers("Run").OfType<IMethodSymbol>());

        var item = SymbolItem.FromSymbol(method, "Fixture");

        Assert.Equal("Uses Fixture.Target with value and code.", item.Documentation);
    }
}
