using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoslynKit.Tests;

/// <summary>
/// Exercises tool discovery, routing, errors, and cancellation through the official protocol implementation.
/// </summary>
public sealed class McpServerTests
{
    [Fact]
    public async Task MemoryTransport_InitializesAndExposesExactlyHelpAndQuery()
    {
        var (clientStream, serverStream) = McpMemoryStream.CreatePair();
        var router = new McpToolRouter((_, _) => throw new InvalidOperationException("Help must not load a workspace."));
        var serverTask = McpServerHost.RunAsync(serverStream, serverStream, router, TestContext.Current.CancellationToken);
        await using (var client = await McpClient.CreateAsync(new StreamClientTransport(clientStream, clientStream),
            new McpClientOptions { ProtocolVersion = "2025-11-25" }, cancellationToken: TestContext.Current.CancellationToken))
        {
            var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(["help", "query"], tools.Select(tool => tool.Name));
            var help = await client.CallToolAsync("help", cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(help.IsError);
            Assert.Contains("command: `refresh`", Text(help), StringComparison.Ordinal);
            var refresh = await client.CallToolAsync("help", new Dictionary<string, object?> { ["command"] = "refresh" },
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.StartsWith("command: refresh", Text(refresh), StringComparison.Ordinal);
        }

        await clientStream.DisposeAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Query_ReturnsRepositoryAndCommandFailureAsToolError()
    {
        McpQuery? captured = null;
        var router = new McpToolRouter((query, _) =>
        {
            captured = query;
            return Task.FromResult(CliProcessResult.Failure(2, "usage", "fixture failure"));
        });
        var root = TestPaths.RepositoryRoot();
        var response = await router.CallAsync(Request("query", new { repositoryRoot = root, args = new[] { "workspace", "--target", "." } }),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsError);
        Assert.StartsWith($"repository: {root}\nerror: usage", Text(response), StringComparison.Ordinal);
        Assert.NotNull(captured);
        Assert.Null(captured.TargetPath);
        Assert.Equal(["workspace"], captured.Args);
    }

    [Theory]
    [InlineData("{\"command\":7}", "Argument 'command'")]
    [InlineData("{\"unrecognized\":true}", "Unknown argument")]
    public async Task Help_ReportsInvalidArgumentsAsUsage(string json, string expected)
    {
        var router = new McpToolRouter((_, _) => throw new InvalidOperationException());
        var response = await router.CallAsync(new CallToolRequestParams
        {
            Name = "help",
            Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json),
        }, TestContext.Current.CancellationToken);

        Assert.True(response.IsError);
        Assert.Contains("error: usage", Text(response), StringComparison.Ordinal);
        Assert.Contains(expected, Text(response), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("init")]
    [InlineData("serve")]
    [InlineData("--repository-worker")]
    public void Query_RejectsProcessAndScaffoldOperations(string command)
    {
        Assert.Throws<CliUsageException>(() => McpQuery.Create(TestPaths.RepositoryRoot(), [command]));
    }

    [Fact]
    public void Query_ResolvesAllPathsAgainstExplicitRootAndRejectsEscapes()
    {
        var root = TestPaths.RepositoryRoot();
        var current = Environment.CurrentDirectory;
        var query = McpQuery.Create(root,
            ["document-lines", "--file", "src/RoslynKit/Program.cs", "--project", "src/RoslynKit/RoslynKit.csproj", "--start-line", "1", "--end-line", "2"]);
        var parsed = CliParser.Parse(query.Args);

        Assert.Equal(TestPaths.RepoFile("src", "RoslynKit", "Program.cs"), parsed.Required("file"));
        Assert.Equal(TestPaths.RepoFile("src", "RoslynKit", "RoslynKit.csproj"), parsed.Required("project"));
        Assert.Equal(current, Environment.CurrentDirectory);
        Assert.Throws<CliUsageException>(() => McpQuery.Create(root, ["workspace", "--target", "../escape"]));
        Assert.Throws<CliUsageException>(() => McpQuery.Create(root, ["search", "--query", "class", "--index-path", "../escape.db"]));
    }

    [Fact]
    public async Task Query_RejectsPathThroughRepositorySymlink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var area = await RepositoryProcessTestArea.CreateAsync(TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(Path.Combine(area.RootPath, "escape"), Path.GetTempPath());
        try
        {
            Assert.Throws<CliUsageException>(() => McpQuery.Create(area.RootPath, ["workspace", "--target", "escape"]));
        }
        finally
        {
            Directory.Delete(Path.Combine(area.RootPath, "escape"));
        }
    }

    [Fact]
    public async Task MemoryTransport_CancelsConcurrentQueryWithoutBlockingHelp()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (clientStream, serverStream) = McpMemoryStream.CreatePair();
        var router = new McpToolRouter(async (_, token) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            }
            finally
            {
                canceled.TrySetResult();
            }
        });
        var serverTask = McpServerHost.RunAsync(serverStream, serverStream, router, TestContext.Current.CancellationToken);
        await using (var client = await McpClient.CreateAsync(new StreamClientTransport(clientStream, clientStream),
            new McpClientOptions { ProtocolVersion = "2025-11-25" }, cancellationToken: TestContext.Current.CancellationToken))
        {
            var pending = client.SendRequestAsync(new JsonRpcRequest
            {
                Id = new RequestId("slow-query"),
                Method = "tools/call",
                Params = JsonSerializer.SerializeToNode(new
                {
                    name = "query",
                    arguments = new { repositoryRoot = TestPaths.RepositoryRoot(), args = new[] { "workspace" } },
                }),
            }, TestContext.Current.CancellationToken);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var help = await client.CallToolAsync("help", cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(help.IsError);
            await client.SendMessageAsync(new JsonRpcNotification
            {
                Method = "notifications/cancelled",
                Params = JsonSerializer.SerializeToNode(new { requestId = "slow-query" }),
            }, TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        await clientStream.DisposeAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MemoryTransport_EndOfInputCancelsInFlightQuery()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (clientStream, serverStream) = McpMemoryStream.CreatePair();
        var router = new McpToolRouter(async (_, token) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return CliProcessResult.Success("unexpected");
            }
            finally
            {
                canceled.TrySetResult();
            }
        });
        var serverTask = McpServerHost.RunAsync(serverStream, serverStream, router, TestContext.Current.CancellationToken);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(clientStream, clientStream),
            new McpClientOptions { ProtocolVersion = "2025-11-25" }, cancellationToken: TestContext.Current.CancellationToken);
        var pending = client.CallToolAsync("query", new Dictionary<string, object?>
        {
            ["repositoryRoot"] = TestPaths.RepositoryRoot(),
            ["args"] = new[] { "workspace" },
        }, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await clientStream.DisposeAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(canceled.Task.IsCompleted);
        await client.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
    }

    internal static CallToolRequestParams Request(string name, object arguments)
    {
        return new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments)) };
    }

    internal static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
