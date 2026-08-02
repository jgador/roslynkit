using System.Diagnostics;

namespace RoslynKit;

/// <summary>
/// Creates the target-selected standalone backend while leaving existing Roslyn execution unchanged.
/// </summary>
internal static class WorkspaceCommandBackend
{
    public static async Task<CliProcessResult> ExecuteStandaloneAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        var targetPath = command.Required("target");
        return WorkspaceTarget.Resolve(targetPath, command.Name) switch
        {
            WorkspaceTargetKind.CSharp => CliProcessResult.Success(
                MarkdownProjection.Render(
                    await RoslynCommandExecutor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false))),
            WorkspaceTargetKind.TypeScript => await ExecuteTypeScriptStandaloneAsync(
                command,
                cancellationToken).ConfigureAwait(false),
            _ => throw new UnreachableException(),
        };
    }

    private static async Task<CliProcessResult> ExecuteTypeScriptStandaloneAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        using var backend = await TypeScriptWorkspaceCommandBackend.CreateAsync(
            command.Required("target"),
            cancellationToken).ConfigureAwait(false);
        return await backend.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Owns one maintained Node bridge and native-preview API instance for a TypeScript target.
/// </summary>
internal sealed class TypeScriptWorkspaceCommandBackend : IDisposable
{
    private readonly TypeScriptBridgeClient _bridge;
    private int _disposed;

    private TypeScriptWorkspaceCommandBackend(string targetPath, TypeScriptBridgeClient bridge)
    {
        TargetPath = targetPath;
        _bridge = bridge;
    }

    public string TargetPath { get; }

    public static async Task<TypeScriptWorkspaceCommandBackend> CreateAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        var canonicalTarget = PathCanonicalizer.ResolveExistingPath(targetPath);
        var bridge = await TypeScriptBridgeClient.StartAsync(
            canonicalTarget,
            cancellationToken).ConfigureAwait(false);
        return new TypeScriptWorkspaceCommandBackend(canonicalTarget, bridge);
    }

    public async Task<CliProcessResult> ExecuteAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (command.Name is "index" or "search")
        {
            var result = await TypeScriptSearchCommandService.ExecuteAsync(
                command,
                _bridge,
                cancellationToken).ConfigureAwait(false);
            return CliProcessResult.Success(MarkdownProjection.Render(result));
        }

        var response = await _bridge.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        return response.ToProcessResult();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var response = await _bridge.RefreshAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessful();
    }

    internal Task<TypeScriptBridgeState> CaptureStateAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _bridge.CaptureStateAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _bridge.Dispose();
        }
    }
}

/// <summary>
/// Preserves one TypeScript backend across daemon generations while refreshing only its native snapshot.
/// </summary>
internal sealed class TypeScriptDaemonBackendOwner : IDisposable
{
    private readonly string _targetPath;
    private TypeScriptWorkspaceCommandBackend? _backend;
    private bool _generationCreated;
    private int _disposed;

    public TypeScriptDaemonBackendOwner(string targetPath)
    {
        _targetPath = targetPath;
    }

    public async Task<WorkspaceDaemonGeneration> LoadGenerationAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_backend is null)
        {
            _backend = await TypeScriptWorkspaceCommandBackend.CreateAsync(
                _targetPath,
                cancellationToken).ConfigureAwait(false);
        }
        else if (_generationCreated)
        {
            await _backend.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        _generationCreated = true;
        return new WorkspaceDaemonGeneration(
            NoopDisposable.Instance,
            _backend.ExecuteAsync);
    }

    internal Task<TypeScriptBridgeState> CaptureStateAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _backend?.CaptureStateAsync(cancellationToken)
            ?? throw new InvalidOperationException("The TypeScript daemon backend has not loaded a generation.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _backend?.Dispose();
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
