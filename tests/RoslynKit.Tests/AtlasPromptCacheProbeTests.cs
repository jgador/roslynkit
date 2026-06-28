using System.Text.Json;
using RoslynKit.AtlasPromptCacheProbe;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies the repo-local Atlas prompt-caching probe helpers without making network calls.
/// </summary>
public sealed class AtlasPromptCacheProbeTests
{
    [Fact]
    public void BuildStablePrefix_SortsFeatureCardsByPath()
    {
        var prefix = AtlasPromptProbeRunner.BuildStablePrefix(
            AtlasPromptProbeLane.Router,
            [
                new AtlasPromptProbeTextFile(".codex/atlas/feature-cards/workspace-navigation.md", "# Workspace"),
                new AtlasPromptProbeTextFile(".codex/agents/atlas-router.toml", "name = \"atlas-router\""),
                new AtlasPromptProbeTextFile(".codex/atlas/test-index.md", "# Test Index"),
                new AtlasPromptProbeTextFile(".codex/atlas/repo-map.md", "# Repo Map"),
                new AtlasPromptProbeTextFile(".codex/atlas/feature-cards/cli-routing.md", "# CLI"),
            ]);

        Assert.True(prefix.IndexOf(".codex/agents/atlas-router.toml", StringComparison.Ordinal) < prefix.IndexOf(".codex/atlas/repo-map.md", StringComparison.Ordinal));
        Assert.True(prefix.IndexOf(".codex/atlas/repo-map.md", StringComparison.Ordinal) < prefix.IndexOf(".codex/atlas/test-index.md", StringComparison.Ordinal));
        Assert.True(prefix.IndexOf(".codex/atlas/feature-cards/cli-routing.md", StringComparison.Ordinal) < prefix.IndexOf(".codex/atlas/feature-cards/workspace-navigation.md", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildStablePrefix_ExcludesGeneratedIndexMetadata()
    {
        var prefix = AtlasPromptProbeRunner.BuildStablePrefix(
            AtlasPromptProbeLane.Router,
            [
                new AtlasPromptProbeTextFile(".codex/agents/atlas-router.toml", "name = \"atlas-router\""),
                new AtlasPromptProbeTextFile(".codex/atlas/repo-map.md", "# Repo Map"),
                new AtlasPromptProbeTextFile(".codex/atlas/test-index.md", "# Test Index"),
                new AtlasPromptProbeTextFile(".codex/atlas/feature-cards/cli-routing.md", "# CLI"),
            ]);

        Assert.DoesNotContain("generatedAtUtc", prefix, StringComparison.Ordinal);
        Assert.DoesNotContain("repoRoot", prefix, StringComparison.Ordinal);
        Assert.DoesNotContain("toolPath", prefix, StringComparison.Ordinal);
    }

    [Fact]
    public void GetPromptCacheKey_UsesLaneSpecificValue()
    {
        Assert.Equal("roslynkit:atlas:router:v1", AtlasPromptProbeDefaults.GetPromptCacheKey(AtlasPromptProbeLane.Router));
        Assert.Equal("roslynkit:atlas:csharp-mapper:v1", AtlasPromptProbeDefaults.GetPromptCacheKey(AtlasPromptProbeLane.CSharpMapper));
        Assert.Equal("roslynkit:atlas:doc-mapper:v1", AtlasPromptProbeDefaults.GetPromptCacheKey(AtlasPromptProbeLane.DocMapper));
        Assert.Equal("roslynkit:atlas:test-mapper:v1", AtlasPromptProbeDefaults.GetPromptCacheKey(AtlasPromptProbeLane.TestMapper));
    }

    [Fact]
    public void Parse_UsesDefaultTask_WhenArgumentsAreEmpty()
    {
        var options = AtlasPromptProbeOptions.Parse([]);

        Assert.Equal(AtlasPromptProbeDefaults.DefaultTask, options.Task);
        Assert.Equal(AtlasPromptProbeLane.Router, options.Lane);
        Assert.Equal(AtlasPromptProbeDefaults.DefaultModel, options.Model);
        Assert.Equal("roslynkit:atlas:router:v1", options.PromptCacheKey);
        Assert.Equal(AtlasPromptProbeDefaults.DefaultPromptCacheRetention, options.PromptCacheRetention);
    }

    [Fact]
    public void LoadSelectedIndexes_WarnsWhenIndexesAreMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var routeSummary = new AtlasPromptProbeRouteSummary(
                "trace definition command",
                "Task: trace definition command",
                Array.Empty<string>(),
                ["src/RoslynKit/Program.cs"],
                Array.Empty<string>(),
                ["src/RoslynKit/Program.cs"]);

            var result = AtlasPromptProbeRunner.LoadSelectedIndexes(tempRoot, routeSummary);

            Assert.Empty(result.SelectedIndexes.FileEntries);
            Assert.Contains(result.Warnings, warning => warning.Contains("markdown-only Atlas context", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ReadUsage_ReadsCachedTokensFromPromptTokensDetails()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "usage": {
                "input_tokens": 2048,
                "output_tokens": 128,
                "total_tokens": 2176,
                "prompt_tokens_details": {
                  "cached_tokens": 1536
                }
              }
            }
            """);

        var usage = AtlasPromptProbeRunner.ReadUsage(document.RootElement);

        Assert.Equal(2048, usage.InputTokens);
        Assert.Equal(128, usage.OutputTokens);
        Assert.Equal(2176, usage.TotalTokens);
        Assert.Equal(1536, usage.CachedTokens);
    }
}
