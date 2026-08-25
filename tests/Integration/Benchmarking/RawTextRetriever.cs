using System.Text;
using System.Text.RegularExpressions;

namespace RoslynKit.Benchmarking;

/// <summary>
/// Produces the deterministic bounded plain-text retrieval baseline.
/// </summary>
internal static partial class RawTextRetriever
{
    internal const int FilesPerScope = 8;
    internal const int AnchorsPerFile = 8;
    internal const int ContextLines = 3;
    internal const int CharactersPerLine = 300;

    private static readonly string[] Scopes = ["src/RoslynKit", "tests/RoslynKit.Tests"];

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "artifacts",
        "bin",
        "node_modules",
        "obj",
        "TestResults",
    };

    public static string Retrieve(string repositoryRoot, BenchmarkCase benchmarkCase)
    {
        var tokens = QueryTokenPattern()
            .Matches(benchmarkCase.Query)
            .Select(match => match.Value.ToLowerInvariant())
            .Where(token => token.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sections = new List<string>
        {
            "Plain-text baseline: files ranked by distinct query terms, then bounded matching-line context.",
        };

        foreach (var scope in Scopes)
        {
            var scopePath = Path.Combine(repositoryRoot, scope.Replace('/', Path.DirectorySeparatorChar));
            var rankedFiles = Directory
                .EnumerateFiles(scopePath, "*.cs", SearchOption.AllDirectories)
                .Where(path => !HasExcludedDirectory(repositoryRoot, path))
                .Select(path => RankFile(repositoryRoot, path, tokens))
                .Where(file => file.DistinctMatches > 0)
                .OrderByDescending(file => file.DistinctMatches)
                .ThenByDescending(file => file.TotalMatches)
                .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
                .Take(FilesPerScope)
                .ToArray();

            sections.Add(string.Empty);
            sections.Add($"## {scope}");
            foreach (var file in rankedFiles)
            {
                sections.Add(string.Empty);
                sections.Add($"### {file.RelativePath}");
                foreach (var lineIndex in SelectLineIndexes(file.Lines, tokens))
                {
                    var line = file.Lines[lineIndex];
                    if (line.Length > CharactersPerLine)
                    {
                        line = line[..CharactersPerLine];
                    }

                    sections.Add($"{file.RelativePath}:{lineIndex + 1}: {line}");
                }
            }
        }

        return string.Join('\n', sections);
    }

    private static RankedFile RankFile(string repositoryRoot, string path, IReadOnlyList<string> tokens)
    {
        var lines = File.ReadAllLines(path);
        var contents = string.Join('\n', lines);
        var counts = tokens.Select(token => CountOccurrences(contents, token)).ToArray();
        return new RankedFile(
            Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'),
            lines,
            counts.Count(count => count > 0),
            counts.Sum());
    }

    private static IReadOnlyList<int> SelectLineIndexes(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> tokens)
    {
        var anchors = lines
            .Select((line, index) =>
            {
                var counts = tokens.Select(token => CountOccurrences(line, token)).ToArray();
                return new LineAnchor(index, counts.Count(count => count > 0), counts.Sum());
            })
            .Where(anchor => anchor.DistinctMatches > 0)
            .OrderByDescending(anchor => anchor.DistinctMatches)
            .ThenByDescending(anchor => anchor.TotalMatches)
            .ThenBy(anchor => anchor.Index)
            .Take(AnchorsPerFile);

        var selected = new SortedSet<int>();
        foreach (var anchor in anchors)
        {
            var start = Math.Max(0, anchor.Index - ContextLines);
            var end = Math.Min(lines.Count - 1, anchor.Index + ContextLines);
            for (var index = start; index <= end; index++)
            {
                selected.Add(index);
            }
        }

        return [.. selected];
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(token, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            offset += token.Length;
        }

        return count;
    }

    private static bool HasExcludedDirectory(string repositoryRoot, string path)
    {
        var relative = Path.GetRelativePath(repositoryRoot, path);
        var directories = Path.GetDirectoryName(relative)?.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries) ?? [];
        return directories.Any(ExcludedDirectoryNames.Contains);
    }

    [GeneratedRegex("[A-Za-z0-9_]+", RegexOptions.CultureInvariant)]
    private static partial Regex QueryTokenPattern();

    private sealed record RankedFile(
        string RelativePath,
        string[] Lines,
        int DistinctMatches,
        int TotalMatches);

    private sealed record LineAnchor(int Index, int DistinctMatches, int TotalMatches);
}
