namespace RoslynKit;

/// <summary>
/// Retains one repository and target in an isolated process with its selected MSBuild installation.
/// </summary>
internal static class RepositoryWorkerHost
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, Stream input, Stream output, CancellationToken cancellationToken)
    {
        if (args.Count < 1)
        {
            throw new CliUsageException("worker", "A private worker requires an explicit repository root.");
        }

        var root = RepositoryContextResolver.ResolveExplicitRoot(args[0]).RootPath;
        var query = McpQuery.Create(root, ["refresh", .. args.Skip(1)]);
        await using var session = new RepositoryWorkspaceSession(root, query.TargetPath, query.AutomaticRestore);
        await using var index = new LiveSearchIndex(session, root, query.TargetPath);
        var application = new CliApplication(TextWriter.Null, TextWriter.Null, ExecuteCommandAsync);
        await RepositoryWorkerProtocol.RunAsync(input, output, ExecuteAsync, cancellationToken).ConfigureAwait(false);
        return 0;

        async Task<CliProcessResult> ExecuteAsync(string[] commandArgs, CancellationToken token)
        {
            if (commandArgs is ["--worker-retire"])
            {
                if (index.IsBusy || !session.TryBeginRetirement())
                {
                    return CliProcessResult.Success("false");
                }

                await index.DisposeAsync().ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
                return CliProcessResult.Success("true");
            }

            var invocation = McpQuery.Create(root, commandArgs, query.AutomaticRestore);
            if (!string.Equals(invocation.TargetPath, query.TargetPath, McpQuery.PathComparison))
            {
                throw new CliUsageException("query", "A private worker cannot change its repository target.");
            }

            if (invocation.CommandName == "refresh")
            {
                var revision = await session.SynchronizeAsync(token).ConfigureAwait(false);
                return CliProcessResult.Success($"command: refresh\nrevision: {revision}\nstatus: synchronized");
            }

            return await application.ExecuteAsync(invocation.Args, token).ConfigureAwait(false);
        }

        async Task<CliProcessResult> ExecuteCommandAsync(ParsedCommand command, CancellationToken token)
        {
            if (command.Name == "search")
            {
                return CliProcessResult.Success(MarkdownProjection.Render(await index.SearchAsync(command, token).ConfigureAwait(false)));
            }

            if (command.Name == "index")
            {
                return CliProcessResult.Success(MarkdownProjection.Render(await index.IndexAsync(command, token).ConfigureAwait(false)));
            }

            using var lease = await session.AcquireAsync(token).ConfigureAwait(false);
            var result = await RoslynCommandExecutor.ExecuteAsync(command, lease.Snapshot, token).ConfigureAwait(false);
            return CliProcessResult.Success(MarkdownProjection.Render(result));
        }
    }
}
