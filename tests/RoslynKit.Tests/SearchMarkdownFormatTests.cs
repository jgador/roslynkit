namespace RoslynKit.Tests;

/// <summary>
/// Verifies deterministic search result rendering without exposing internal ranking scores.
/// </summary>
public sealed class SearchMarkdownFormatTests
{
    [Fact]
    public void Render_EmitsIndexMetadata()
    {
        var result = new IndexResult(
            @"C:\repo\App\App.slnx",
            @"C:\repo\App\artifacts\roslynkit.db",
            SearchIndexState.Fresh,
            SymbolCount: 42,
            Rebuilt: true,
            []);

        var rendered = MarkdownProjection.Render(result);

        var expected = "command: index\n"
            + "target: `C:\\repo\\App\\App.slnx`\n"
            + "index-path: `C:\\repo\\App\\artifacts\\roslynkit.db`\n"
            + "index-state: fresh\n"
            + "symbols: 42\n"
            + "rebuilt: true";
        Assert.Equal(expected, rendered);
    }

    [Fact]
    public void Render_EmitsRankedSearchHitsWithSearchMetadata()
    {
        var result = new SearchResult(
            @"C:\repo\App\App.slnx",
            @"C:\repo\App\artifacts\roslynkit.db",
            "how does the workspace daemon reload after source changes",
            SearchIndexState.Fresh,
            TotalCount: 3,
            ReturnedCount: 2,
            Truncated: true,
            [
                new SearchHit(
                    "App.WorkspaceDaemonSession.ReloadAsync",
                    "method",
                    new SourceRange(@"src\App\WorkspaceDaemonSession.cs", 18, 20, 18, 31),
                    "M:App.WorkspaceDaemonSession.ReloadAsync(System.Threading.CancellationToken)",
                    "Reloads the workspace generation after source changes."),
                new SearchHit(
                    "App.WorkspaceDaemonSession",
                    "class",
                    new SourceRange(@"src\App\WorkspaceDaemonSession.cs", 5, 21, 5, 43),
                    "T:App.WorkspaceDaemonSession",
                    null),
            ],
            []);

        var rendered = MarkdownProjection.Render(result);

        var expected = "command: search\n"
            + "target: `C:\\repo\\App\\App.slnx`\n"
            + "index-path: `C:\\repo\\App\\artifacts\\roslynkit.db`\n"
            + "query: `how does the workspace daemon reload after source changes`\n"
            + "index-state: fresh\n"
            + "returned: 2/3\n"
            + "truncated: true\n"
            + "\n"
            + "- rank: 1 kind: method name: `App.WorkspaceDaemonSession.ReloadAsync` loc: `src\\App\\WorkspaceDaemonSession.cs:18:20-18:31` id: `M:App.WorkspaceDaemonSession.ReloadAsync(System.Threading.CancellationToken)`\n"
            + "  excerpt: `Reloads the workspace generation after source changes.`\n"
            + "- rank: 2 kind: class name: `App.WorkspaceDaemonSession` loc: `src\\App\\WorkspaceDaemonSession.cs:5:21-5:43` id: `T:App.WorkspaceDaemonSession`";
        Assert.Equal(expected, rendered);
        Assert.DoesNotContain("score", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_UsesStaleStateAndOmitsOptionalSearchHitFields()
    {
        var result = new SearchResult(
            "app.slnx",
            @"artifacts\roslynkit.db",
            "worker",
            SearchIndexState.Stale,
            TotalCount: 1,
            ReturnedCount: 1,
            Truncated: false,
            [
                new SearchHit(
                    "App.Worker",
                    "class",
                    new SourceRange("src/Worker.cs", 3, 14, 3, 20),
                    null,
                    null),
            ],
            []);

        var rendered = MarkdownProjection.Render(result);

        Assert.Contains("index-state: stale", rendered, StringComparison.Ordinal);
        Assert.Contains("- rank: 1 kind: class name: `App.Worker` loc: `src/Worker.cs:3:14-3:20`", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("id:", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("excerpt:", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EscapesBackticksInSearchQueryAndExcerpt()
    {
        var result = new SearchResult(
            "app.slnx",
            "artifacts/roslynkit.db",
            "`worker`",
            SearchIndexState.Fresh,
            TotalCount: 1,
            ReturnedCount: 1,
            Truncated: false,
            [
                new SearchHit(
                    "App.Worker",
                    "class",
                    new SourceRange("src/Worker.cs", 3, 14, 3, 20),
                    null,
                    "Returns `worker` state."),
            ],
            []);

        var rendered = MarkdownProjection.Render(result);

        Assert.Contains("query: `` `worker` ``", rendered, StringComparison.Ordinal);
        Assert.Contains("excerpt: ``Returns `worker` state.``", rendered, StringComparison.Ordinal);
    }
}
