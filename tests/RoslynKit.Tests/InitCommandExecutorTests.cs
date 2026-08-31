namespace RoslynKit.Tests;

/// <summary>
/// Verifies the repository guardrail and skill-bundle file writes performed by the init command.
/// </summary>
public sealed class InitCommandExecutorTests
{
    [Fact]
    public void Execute_ScaffoldsCodexSkillBundleByDefault()
    {
        var root = CreateRepositoryRoot();
        try
        {
            var result = InitCommandExecutor.Execute(CliParser.Parse(["init"]), root, typeof(InitCommandExecutor).Assembly);

            Assert.Equal("codex", result.AgentSelection);
            Assert.True(result.Files.Count >= 3);
            Assert.All(result.Files, file => Assert.Equal(InitFileStatus.Created, file.Status));
            Assert.True(File.Exists(Path.Combine(root, ".agents", "skills", "roslynkit", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(root, ".agents", "skills", "roslynkit", "references", "commands.md")));
            Assert.True(File.Exists(Path.Combine(root, ".agents", "skills", "roslynkit", "references", "output.md")));
            var skill = File.ReadAllText(Path.Combine(root, ".agents", "skills", "roslynkit", "SKILL.md"));
            Assert.Contains("name: roslynkit", skill, StringComparison.Ordinal);
            Assert.Contains("Never request more than 80 inclusive lines", skill, StringComparison.Ordinal);
            Assert.Contains("at most 8 RoslynKit invocations total", skill, StringComparison.Ordinal);
            Assert.Contains("do not read C# source with `rg`", skill, StringComparison.Ordinal);
            Assert.Contains("## Bounded evidence workflow", skill, StringComparison.Ordinal);
            Assert.Contains("--max-results 25", skill, StringComparison.Ordinal);
            Assert.Contains("increasing to `--max-results 50`", skill, StringComparison.Ordinal);
            Assert.Contains("third and final search with `--max-results 200`", skill, StringComparison.Ordinal);
            Assert.Contains("never run a fourth search", skill, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void Execute_ScaffoldsSelectedAgentRoot()
    {
        var root = CreateRepositoryRoot();
        try
        {
            var result = InitCommandExecutor.Execute(CliParser.Parse(["init", "--agent", "claude"]), root, typeof(InitCommandExecutor).Assembly);

            Assert.Equal("claude", result.AgentSelection);
            Assert.All(result.Files, file => Assert.StartsWith(".claude/skills/roslynkit/", file.Path, StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(root, ".claude", "skills", "roslynkit", "SKILL.md")));
            Assert.False(Directory.Exists(Path.Combine(root, ".agents")));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void Execute_ScaffoldsAllAgentRoots()
    {
        var root = CreateRepositoryRoot();
        try
        {
            var result = InitCommandExecutor.Execute(CliParser.Parse(["init", "--agent", "all"]), root, typeof(InitCommandExecutor).Assembly);

            Assert.Equal("all", result.AgentSelection);
            Assert.Equal(["claude", "codex", "copilot"], result.Files.Select(file => file.Agent).Distinct().OrderBy(agent => agent, StringComparer.Ordinal));
            Assert.True(File.Exists(Path.Combine(root, ".agents", "skills", "roslynkit", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(root, ".claude", "skills", "roslynkit", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(root, ".github", "skills", "roslynkit", "SKILL.md")));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void Execute_LeavesIdenticalFilesUnchanged()
    {
        var root = CreateRepositoryRoot();
        try
        {
            InitCommandExecutor.Execute(CliParser.Parse(["init"]), root, typeof(InitCommandExecutor).Assembly);

            var result = InitCommandExecutor.Execute(CliParser.Parse(["init"]), root, typeof(InitCommandExecutor).Assembly);

            Assert.All(result.Files, file => Assert.Equal(InitFileStatus.Unchanged, file.Status));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void Execute_RejectsExistingDifferentFileWithoutOverwrite()
    {
        var root = CreateRepositoryRoot();
        try
        {
            var skillPath = Path.Combine(root, ".agents", "skills", "roslynkit", "SKILL.md");
            Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
            File.WriteAllText(skillPath, "local content");

            var exception = Assert.Throws<CliUsageException>(
                () => InitCommandExecutor.Execute(CliParser.Parse(["init"]), root, typeof(InitCommandExecutor).Assembly));

            Assert.Equal("init", exception.CommandName);
            Assert.Contains("Refusing to overwrite existing file '.agents/skills/roslynkit/SKILL.md'", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void Execute_OverwritesExistingDifferentFileWhenRequested()
    {
        var root = CreateRepositoryRoot();
        try
        {
            var skillPath = Path.Combine(root, ".agents", "skills", "roslynkit", "SKILL.md");
            Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
            File.WriteAllText(skillPath, "local content");

            var result = InitCommandExecutor.Execute(CliParser.Parse(["init", "--overwrite"]), root, typeof(InitCommandExecutor).Assembly);

            Assert.Contains(result.Files, file => file.Path == ".agents/skills/roslynkit/SKILL.md" && file.Status == InitFileStatus.Overwritten);
            Assert.Contains("name: roslynkit", File.ReadAllText(skillPath), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void Execute_RequiresGitMarkerInCurrentDirectory()
    {
        var root = CreateTestRoot();
        try
        {
            var exception = Assert.Throws<CliUsageException>(
                () => InitCommandExecutor.Execute(CliParser.Parse(["init"]), root, typeof(InitCommandExecutor).Assembly));

            Assert.Equal("init", exception.CommandName);
            Assert.Contains("Current directory must be a Git repository root", exception.Message, StringComparison.Ordinal);
            Assert.Equal("Run roslynkit init from the repository root.", exception.Hint);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static string CreateRepositoryRoot()
    {
        var root = CreateTestRoot();
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        return root;
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(TestPaths.RepositoryRoot(), "artifacts", "init-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
