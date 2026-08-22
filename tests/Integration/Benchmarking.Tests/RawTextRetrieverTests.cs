namespace RoslynKit.Benchmarking.Tests;

/// <summary>
/// Verifies deterministic file, anchor, context, and line-length bounds for raw retrieval.
/// </summary>
public sealed class RawTextRetrieverTests
{
    [Fact]
    public void Retrieve_IsDeterministicAndBounded()
    {
        using var repository = new TemporaryBenchmarkRepository();
        for (var index = 0; index < 9; index++)
        {
            var lines = Enumerable.Range(1, 20)
                .Select(line => $"alpha beta declaration {index} line {line} {new string('x', 350)}");
            repository.Write($"src/RoslynKit/File{index}.cs", string.Join('\n', lines));
        }

        repository.Write("tests/RoslynKit.Tests/AlphaTests.cs", "alpha beta test evidence\n");
        repository.Write("src/RoslynKit/obj/Generated.cs", "alpha beta generated output\n");
        var benchmarkCase = BenchmarkTestData.Case();

        var first = RawTextRetriever.Retrieve(repository.RootPath, benchmarkCase);
        var second = RawTextRetriever.Retrieve(repository.RootPath, benchmarkCase);

        Assert.Equal(first, second);
        Assert.Contains("src/RoslynKit/File0.cs", first, StringComparison.Ordinal);
        Assert.Contains("src/RoslynKit/File7.cs", first, StringComparison.Ordinal);
        Assert.DoesNotContain("src/RoslynKit/File8.cs", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Generated.cs", first, StringComparison.Ordinal);
        Assert.Contains("tests/RoslynKit.Tests/AlphaTests.cs", first, StringComparison.Ordinal);
        Assert.All(
            first.Split('\n').Where(line => line.Contains(": ", StringComparison.Ordinal)),
            line => Assert.True(line[(line.LastIndexOf(": ", StringComparison.Ordinal) + 2)..].Length <= RawTextRetriever.CharactersPerLine));
    }

    [Fact]
    public void Retrieve_UsesAtMostEightWidelySeparatedAnchorsPerFile()
    {
        using var repository = new TemporaryBenchmarkRepository();
        var lines = Enumerable.Range(0, 100).Select(index => $"line {index}").ToArray();
        for (var anchor = 0; anchor < 10; anchor++)
        {
            lines[anchor * 10] = $"alpha beta anchor-{anchor}";
        }

        lines[80] += " ninth-anchor-marker";
        repository.Write("src/RoslynKit/Anchors.cs", string.Join('\n', lines));
        repository.Write("tests/RoslynKit.Tests/AlphaTests.cs", "alpha beta test evidence\n");

        var evidence = RawTextRetriever.Retrieve(repository.RootPath, BenchmarkTestData.Case());

        Assert.DoesNotContain("ninth-anchor-marker", evidence, StringComparison.Ordinal);
        var renderedSourceLines = evidence.Split('\n')
            .Count(line => line.StartsWith("src/RoslynKit/Anchors.cs:", StringComparison.Ordinal));
        Assert.True(renderedSourceLines <= RawTextRetriever.AnchorsPerFile * ((2 * RawTextRetriever.ContextLines) + 1));
    }
}
