using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RoslynKit;

/// <summary>
/// Exposes two tools over the official standard-input/output protocol transport for one client-owned lifetime.
/// </summary>
internal static class McpServerHost
{
    private const string RefreshHelp = "command: refresh\ndescription: Synchronize saved source, project, configuration, and generated build inputs with the retained workspace.\nusage: query(repositoryRoot, [\"refresh\", optional \"--target\", \"path\"])\nnotes: Run after an agent-initiated build; unchanged inputs retain the current workspace revision. Search catches up to the synchronized revision when next requested.";

    internal static readonly IReadOnlyList<Tool> Tools =
    [
        new()
        {
            Name = "help",
            Description = "Discover RoslynKit operations or obtain bounded Markdown help for one command or topic, including refresh.",
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { command = new { type = "string", description = "Optional command name, including multiword commands, or refresh." } },
                additionalProperties = false,
            }),
            Annotations = new ToolAnnotations { ReadOnlyHint = true, OpenWorldHint = false },
        },
        new()
        {
            Name = "query",
            Description = "Run a RoslynKit operation with CLI-shaped args against an explicit absolute Git repository root. Paths resolve inside that root. Use help to discover operations; refresh synchronizes saved inputs after a build. Initial or structurally changed workspaces may restore dependencies unless serve uses --no-restore.",
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    repositoryRoot = new { type = "string", description = "Absolute path to a repository root containing a .git directory." },
                    args = new { type = "array", items = new { type = "string" }, minItems = 1, description = "Command and separate argument tokens, for example [symbols, --query, Service]." },
                },
                required = new[] { "repositoryRoot", "args" },
                additionalProperties = false,
            }),
            Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = true },
        },
    ];

    public static async Task<int> RunAsync(McpServeOptions options, CancellationToken cancellationToken)
    {
        await using var pool = new McpWorkspacePool(options.MaxWorkspaces, Console.Error);
        var router = new McpToolRouter(pool.ExecuteAsync, options.AutomaticRestore);
        await RunAsync(new StdioServerTransport("roslynkit"), router, cancellationToken, pool.DisposeAsync).ConfigureAwait(false);
        return 0;
    }

    internal static Task RunAsync(Stream input, Stream output, McpToolRouter router, CancellationToken cancellationToken)
    {
        return RunAsync(new StreamServerTransport(input, output, "roslynkit"), router, cancellationToken);
    }

    private static async Task RunAsync(
        ITransport transport,
        McpToolRouter router,
        CancellationToken cancellationToken,
        Func<ValueTask>? shutdown = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var server = McpServer.Create(transport, new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "roslynkit", Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown" },
            ServerInstructions = "Call help for operation syntax. Every query requires its repositoryRoot. After a build, call query with args [\"refresh\"] for that repository and target before dependent navigation.",
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = Tools.ToList() }),
                CallToolHandler = (request, token) => new ValueTask<CallToolResult>(router.CallAsync(request.Params!, token)),
            },
        });
        var running = server.RunAsync(lifetime.Token);
        var canceled = Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
        if (await Task.WhenAny(running, transport.MessageReader.Completion, canceled).ConfigureAwait(false) != running)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            if (shutdown is not null)
            {
                await shutdown().ConfigureAwait(false);
            }
        }

        try
        {
            await running.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    internal static string Help(string? command)
    {
        if (command?.Trim() == "refresh")
        {
            return RefreshHelp;
        }

        var tokens = string.IsNullOrWhiteSpace(command)
            ? new[] { "help" }
            : new[] { "help" }.Concat(command.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        var parsed = CliParser.Parse(tokens);
        var result = MarkdownProjection.RenderHelp(parsed.HelpSubject);
        return parsed.HelpSubject is null
            ? result + "\n- command: `refresh` description: Synchronize saved workspace inputs after a build (query tool only).\nUse query with an explicit absolute repositoryRoot and command tokens in args. Operations init and serve are available only from the terminal."
            : result;
    }
}

/// <summary>
/// Converts tool arguments and command outcomes to bounded protocol text results.
/// </summary>
internal sealed class McpToolRouter(
    Func<McpQuery, CancellationToken, Task<CliProcessResult>> execute,
    bool automaticRestore = true)
{
    public async Task<CallToolResult> CallAsync(CallToolRequestParams request, CancellationToken cancellationToken)
    {
        string? root = null;
        try
        {
            var arguments = request.Arguments ?? new Dictionary<string, JsonElement>();
            if (request.Name == "help")
            {
                RejectUnknownArguments(arguments, "command");
                if (arguments.TryGetValue("command", out var value) && value.ValueKind != JsonValueKind.String)
                {
                    throw new CliUsageException("help", "Argument 'command' must be a command or topic string.");
                }

                var topic = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                return Result(McpServerHost.Help(topic), isError: false);
            }

            if (request.Name != "query")
            {
                throw new CliUsageException("tools", $"Unknown tool '{request.Name}'. Available tools: help, query.");
            }

            RejectUnknownArguments(arguments, "repositoryRoot", "args");
            if (!arguments.TryGetValue("repositoryRoot", out var repository) || repository.ValueKind != JsonValueKind.String)
            {
                throw new CliUsageException("query", "Argument 'repositoryRoot' must be an absolute repository path.");
            }

            root = repository.GetString();
            if (!arguments.TryGetValue("args", out var args) || args.ValueKind != JsonValueKind.Array
                || args.EnumerateArray().Any(arg => arg.ValueKind != JsonValueKind.String))
            {
                throw new CliUsageException("query", "Argument 'args' must be an array of command argument strings.");
            }

            var query = McpQuery.Create(root!, args.EnumerateArray().Select(arg => arg.GetString()!).ToArray(), automaticRestore);
            root = query.RepositoryRoot;
            var response = query.CommandName is "help" or "version"
                ? await new CliApplication(TextWriter.Null).ExecuteAsync(query.Args, cancellationToken).ConfigureAwait(false)
                : await execute(query, cancellationToken).ConfigureAwait(false);
            var text = $"repository: {DisplayPath(root)}\n{response.Stdout}";
            if (!string.IsNullOrWhiteSpace(response.Stderr))
            {
                text += $"\nstderr:\n{response.Stderr}";
            }

            return Result(text, response.ExitCode != 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var prefix = root is null ? string.Empty : $"repository: {DisplayPath(root)}\n";
            return Result(prefix + CliProcessResult.FromException(exception).Stdout, isError: true);
        }
    }

    private static void RejectUnknownArguments(IDictionary<string, JsonElement> arguments, params string[] allowed)
    {
        var unknown = arguments.Keys.FirstOrDefault(name => !allowed.Contains(name, StringComparer.Ordinal));
        if (unknown is not null)
        {
            throw new CliUsageException("tools", $"Unknown argument '{unknown}'.");
        }
    }

    private static CallToolResult Result(string text, bool isError)
    {
        return new CallToolResult { Content = [new TextContentBlock { Text = text }], IsError = isError };
    }

    private static string DisplayPath(string path)
    {
        return path.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);
    }
}
