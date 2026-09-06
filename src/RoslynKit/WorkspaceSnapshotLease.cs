namespace RoslynKit;

/// <summary>
/// Keeps the workspace owner alive while one query uses an immutable solution revision.
/// </summary>
internal sealed class WorkspaceSnapshotLease : IDisposable
{
    private Action? _release;

    internal WorkspaceSnapshotLease(
        RoslynWorkspaceLoader snapshot,
        long revision,
        WorkspaceInputManifest manifest,
        Action release)
    {
        Snapshot = snapshot;
        Revision = revision;
        Manifest = manifest;
        _release = release;
    }

    public RoslynWorkspaceLoader Snapshot { get; }

    public long Revision { get; }

    public string Fingerprint => Manifest.Fingerprint;

    internal WorkspaceInputManifest Manifest { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
