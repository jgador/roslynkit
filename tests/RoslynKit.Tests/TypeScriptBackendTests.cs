using System.Text.RegularExpressions;

namespace RoslynKit.Tests;

[Collection(TypeScriptBackendCollection.Name)]
public sealed class TypeScriptBackendTests
{
    [Theory]
    [InlineData("solution.slnx", "CSharp")]
    [InlineData("solution.sln", "CSharp")]
    [InlineData("project.csproj", "CSharp")]
    [InlineData("tsconfig.json", "TypeScript")]
    [InlineData("jsconfig.json", "TypeScript")]
    public void WorkspaceTarget_ResolvesSupportedBackend(string target, string expected)
    {
        Assert.Equal(expected, WorkspaceTarget.Resolve(target, "workspace").ToString());
    }

    [Theory]
    [InlineData("package.json")]
    [InlineData("config.json")]
    [InlineData("project.fsproj")]
    public void WorkspaceTarget_RejectsUnsupportedTargets(string target)
    {
        var exception = Assert.Throws<CliUsageException>(() => WorkspaceTarget.Resolve(target, "workspace"));

        Assert.Contains("tsconfig.json", exception.Message, StringComparison.Ordinal);
        Assert.Contains(".csproj", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DaemonBackend_ReusesBridgeApiAndNativeProcessWhileRefreshingSnapshot()
    {
        await using var fixture = await TypeScriptTestArea.CreateAsync(TestContext.Current.CancellationToken);
        using var owner = new TypeScriptDaemonBackendOwner(fixture.ConfigPath);
        var firstGeneration = await owner.LoadGenerationAsync(TestContext.Current.CancellationToken);
        var firstState = await owner.CaptureStateAsync(TestContext.Current.CancellationToken);
        var first = await firstGeneration.ExecuteAsync(
            Parse("symbols", fixture.ConfigPath, "--query", "UserFormatter", "--exact"),
            TestContext.Current.CancellationToken);
        firstGeneration.Dispose();

        await File.AppendAllTextAsync(
            fixture.SourcePath,
            $"{Environment.NewLine}export class SnapshotRefreshFixture {{}}{Environment.NewLine}",
            TestContext.Current.CancellationToken);
        var secondGeneration = await owner.LoadGenerationAsync(TestContext.Current.CancellationToken);
        var secondState = await owner.CaptureStateAsync(TestContext.Current.CancellationToken);
        var second = await secondGeneration.ExecuteAsync(
            Parse("symbols", fixture.ConfigPath, "--query", "SnapshotRefreshFixture", "--exact"),
            TestContext.Current.CancellationToken);
        secondGeneration.Dispose();

        Assert.Equal(0, first.ExitCode);
        Assert.Contains("UserFormatter", first.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("SnapshotRefreshFixture", second.Stdout, StringComparison.Ordinal);
        Assert.Equal(firstState.BridgeProcessId, secondState.BridgeProcessId);
        Assert.Equal(firstState.NativeProcessId, secondState.NativeProcessId);
        Assert.Equal(firstState.ApiInstanceId, secondState.ApiInstanceId);
        Assert.NotEqual(firstState.SnapshotId, secondState.SnapshotId);
        Assert.Equal(1, secondState.RefreshCount);
    }

    [Fact]
    public async Task StandaloneIndexAndSearch_ReturnRoundTrippableNativeSelectors()
    {
        var targetPath = TestPaths.RepoFile("tests", "TypeScriptFixture", "tsconfig.json");
        var testDirectory = TestPaths.RepoFile("artifacts", "typescript-tests", Guid.NewGuid().ToString("N"));
        var indexPath = Path.Combine(testDirectory, "search.db");
        try
        {
            var index = await WorkspaceCommandBackend.ExecuteStandaloneAsync(
                Parse("index", targetPath, "--index-path", indexPath, "--rebuild"),
                TestContext.Current.CancellationToken);
            var search = await WorkspaceCommandBackend.ExecuteStandaloneAsync(
                Parse("search", targetPath, "--index-path", indexPath, "--query", "user formatter", "--max-results", "5"),
                TestContext.Current.CancellationToken);
            var selector = Regex.Match(search.Stdout, " id: `(?<selector>ts:[^`]+)`", RegexOptions.CultureInvariant)
                .Groups["selector"].Value;

            Assert.Equal(0, index.ExitCode);
            Assert.Contains("index-state: fresh", index.Stdout, StringComparison.Ordinal);
            Assert.Equal(0, search.ExitCode);
            Assert.StartsWith("ts:", selector, StringComparison.Ordinal);
            foreach (var commandName in new[] { "definition", "references", "symbol-source" })
            {
                var result = await WorkspaceCommandBackend.ExecuteStandaloneAsync(
                    Parse(commandName, targetPath, "--symbol", selector),
                    TestContext.Current.CancellationToken);
                Assert.Equal(0, result.ExitCode);
                Assert.Contains(selector, result.Stdout, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RuntimeResolver_ReportsMissingNodeAndNativePreviewPrerequisites()
    {
        var targetPath = TestPaths.RepoFile("tests", "TypeScriptFixture", "tsconfig.json");
        using (new EnvironmentVariableScope(
            "ROSLYNKIT_NODE_PATH",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-node")))
        {
            var nodeException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TypeScriptRuntimeResolver.ResolveAsync(targetPath, TestContext.Current.CancellationToken));
            Assert.Contains("Node.js 16.20 or later", nodeException.Message, StringComparison.Ordinal);
            Assert.Contains("ROSLYNKIT_NODE_PATH", nodeException.Message, StringComparison.Ordinal);
        }

        using (new EnvironmentVariableScope(
            "ROSLYNKIT_TYPESCRIPT_NATIVE_PREVIEW_ROOT",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-native-preview")))
        {
            var packageException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TypeScriptRuntimeResolver.ResolveAsync(targetPath, TestContext.Current.CancellationToken));
            Assert.Contains("does not contain package.json", packageException.Message, StringComparison.Ordinal);
            Assert.Contains("ROSLYNKIT_TYPESCRIPT_NATIVE_PREVIEW_ROOT", packageException.Message, StringComparison.Ordinal);
        }

        using (new EnvironmentVariableScope(
            "ROSLYNKIT_TYPESCRIPT_BRIDGE_PATH",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-bridge.mjs")))
        {
            var bridgeException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TypeScriptRuntimeResolver.ResolveAsync(targetPath, TestContext.Current.CancellationToken));
            Assert.Contains("ROSLYNKIT_TYPESCRIPT_BRIDGE_PATH", bridgeException.Message, StringComparison.Ordinal);
            Assert.Contains("missing bridge script", bridgeException.Message, StringComparison.Ordinal);
        }
    }

    private static ParsedCommand Parse(string commandName, string targetPath, params string[] options)
    {
        return CliParser.Parse([commandName, "--target", targetPath, .. options]);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previousValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previousValue);
        }
    }

    private sealed class TypeScriptTestArea : IAsyncDisposable
    {
        private TypeScriptTestArea(string rootPath)
        {
            RootPath = rootPath;
        }

        public string RootPath { get; }

        public string ConfigPath => Path.Combine(RootPath, "tsconfig.json");

        public string SourcePath => Path.Combine(RootPath, "src", "formatters.ts");

        public static async Task<TypeScriptTestArea> CreateAsync(CancellationToken cancellationToken)
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                "roslynkit-tests",
                "typescript-backend",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            await CopyDirectoryAsync(
                TestPaths.RepoFile("tests", "TypeScriptFixture"),
                rootPath,
                cancellationToken);
            return new TypeScriptTestArea(rootPath);
        }

        private static async Task CopyDirectoryAsync(
            string sourceDirectory,
            string destinationDirectory,
            CancellationToken cancellationToken)
        {
            foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, directory)));
            }

            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, file));
                await using var source = File.OpenRead(file);
                await using var target = File.Create(destination);
                await source.CopyToAsync(target, cancellationToken);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TypeScriptBackendCollection
{
    public const string Name = "TypeScript backend integration";
}
