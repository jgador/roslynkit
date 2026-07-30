namespace RoslynKit;

/// <summary>
/// Resolves the hidden daemon process identity and constructs its workspace, lifecycle, and pipe owners.
/// </summary>
internal static class DaemonServerRunner
{
    internal const string InternalModeToken = "__roslynkit-daemon-v1";

    public static async Task<int> RunAsync(string targetPath, CancellationToken cancellationToken)
    {
        try
        {
            var workspaceResolution = await new GitWorkspaceIdentityResolver()
                .ResolveAsync(targetPath, cancellationToken).ConfigureAwait(false);
            if (workspaceResolution.Identity is not { } workspaceIdentity)
            {
                return 1;
            }

            var identity = new DaemonIdentityResolver().Resolve(workspaceIdentity);
            var endpointName = DaemonEndpointName.Create(identity);
            using var lifetimeLease = DaemonLifetimeLease.TryAcquire(endpointName);
            if (lifetimeLease is null)
            {
                return 0;
            }

            var session = new WorkspaceDaemonSession(
                workspaceIdentity.TargetPath,
                workspaceIdentity.WorktreeRoot);
            await using var host = new WorkspaceDaemonHost(session);
            var server = new WorkspaceDaemonServer(endpointName, host);
            _ = await server.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch
        {
            return 1;
        }
    }

    public static bool TryParseArguments(IReadOnlyList<string> arguments, out string? targetPath)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 3
            && arguments[0] == InternalModeToken
            && arguments[1] == "--target"
            && !string.IsNullOrWhiteSpace(arguments[2]))
        {
            targetPath = arguments[2];
            return true;
        }

        targetPath = null;
        return false;
    }
}
