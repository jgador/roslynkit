using System.Reflection;

namespace RoslynKit;

/// <summary>
/// Scaffolds the embedded RoslynKit skill bundle into an agent-specific repository folder.
/// </summary>
public static class InitCommandExecutor
{
    private const string DefaultAgent = "codex";
    private const string ResourcePrefix = "RoslynKit.InitAssets/skills/roslynkit/";

    private static readonly InitAgentTarget[] AgentTargets =
    [
        new("codex", ".agents/skills/roslynkit"),
        new("claude", ".claude/skills/roslynkit"),
        new("copilot", ".github/skills/roslynkit"),
    ];

    /// <summary>
    /// Executes init from the process current directory using the embedded skill bundle in this assembly.
    /// </summary>
    public static InitResult Execute(ParsedCommand command)
    {
        return Execute(command, Directory.GetCurrentDirectory(), typeof(InitCommandExecutor).Assembly);
    }

    /// <summary>
    /// Executes init from an explicit directory, primarily for tests that need isolated repository roots.
    /// </summary>
    public static InitResult Execute(ParsedCommand command, string currentDirectory, Assembly assembly)
    {
        var repositoryRoot = Path.GetFullPath(currentDirectory);
        if (!HasCurrentDirectoryGitMarker(repositoryRoot))
        {
            throw new CliUsageException(
                "init",
                "Current directory must be a Git repository root containing a .git directory or file.",
                "Run roslynkit init from the repository root.");
        }

        var agentSelection = command.Optional("agent") ?? DefaultAgent;
        var overwrite = command.Flag("overwrite");
        var targets = ResolveTargets(agentSelection);
        var assets = LoadAssets(assembly);
        var files = new List<InitFileResult>(targets.Count * assets.Count);

        foreach (var target in targets)
        {
            foreach (var asset in assets)
            {
                var displayPath = $"{target.BundleRoot}/{asset.RelativePath}";
                var outputPath = CombineRelative(repositoryRoot, displayPath);
                var status = WriteAsset(command.Name, displayPath, outputPath, asset.Content, overwrite);
                files.Add(new InitFileResult(target.Agent, displayPath, status));
            }
        }

        return new InitResult(agentSelection, repositoryRoot, files);
    }

    private static bool HasCurrentDirectoryGitMarker(string repositoryRoot)
    {
        var gitPath = Path.Combine(repositoryRoot, ".git");
        return Directory.Exists(gitPath) || File.Exists(gitPath);
    }

    private static IReadOnlyList<InitAgentTarget> ResolveTargets(string agentSelection)
    {
        if (string.Equals(agentSelection, "all", StringComparison.Ordinal))
        {
            return AgentTargets;
        }

        foreach (var target in AgentTargets)
        {
            if (string.Equals(target.Agent, agentSelection, StringComparison.Ordinal))
            {
                return [target];
            }
        }

        throw new CliUsageException("init", $"Unknown agent '{agentSelection}'. Supported values: {SupportedAgentsText()}.");
    }

    private static IReadOnlyList<InitAsset> LoadAssets(Assembly assembly)
    {
        var assets = new List<InitAsset>();
        foreach (var resourceName in assembly.GetManifestResourceNames().OrderBy(resourceName => resourceName, StringComparer.Ordinal))
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relativePath = resourceName[ResourcePrefix.Length..].Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded init asset '{resourceName}' could not be opened.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            assets.Add(new InitAsset(relativePath, buffer.ToArray()));
        }

        if (assets.Count == 0)
        {
            throw new InvalidOperationException("No embedded RoslynKit init assets were found.");
        }

        return assets;
    }

    private static InitFileStatus WriteAsset(
        string commandName,
        string displayPath,
        string outputPath,
        byte[] content,
        bool overwrite)
    {
        if (File.Exists(outputPath))
        {
            var existing = File.ReadAllBytes(outputPath);
            if (existing.SequenceEqual(content))
            {
                return InitFileStatus.Unchanged;
            }

            if (!overwrite)
            {
                throw new CliUsageException(
                    commandName,
                    $"Refusing to overwrite existing file '{displayPath}'.",
                    "Rerun with --overwrite to replace scaffolded RoslynKit skill files.");
            }

            File.WriteAllBytes(outputPath, content);
            return InitFileStatus.Overwritten;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, content);
        return InitFileStatus.Created;
    }

    private static string CombineRelative(string root, string relativePath)
    {
        var path = root;
        foreach (var segment in relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = Path.Combine(path, segment);
        }

        return path;
    }

    internal static bool IsSupportedAgent(string agent)
    {
        return string.Equals(agent, "all", StringComparison.Ordinal)
            || AgentTargets.Any(target => string.Equals(target.Agent, agent, StringComparison.Ordinal));
    }

    internal static string SupportedAgentsText()
    {
        return "codex, claude, copilot, all";
    }

    private sealed record InitAgentTarget(string Agent, string BundleRoot);

    private sealed record InitAsset(string RelativePath, byte[] Content);
}
