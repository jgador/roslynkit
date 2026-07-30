namespace RoslynKit;

/// <summary>
/// Represents one stable, Git-native view of mutable worktree state.
/// </summary>
internal sealed class GitWorktreeFingerprint : IEquatable<GitWorktreeFingerprint>
{
    private readonly IReadOnlyList<GitStatusFingerprint> _statusEntries;
    private readonly IReadOnlyList<GitFileFingerprint> _files;

    public GitWorktreeFingerprint(
        string headCommit,
        IEnumerable<GitStatusFingerprint> statusEntries,
        IEnumerable<GitFileFingerprint> files)
    {
        HeadCommit = headCommit;
        _statusEntries = Array.AsReadOnly(statusEntries.ToArray());
        _files = Array.AsReadOnly(files.ToArray());
    }

    public string HeadCommit { get; }

    public IReadOnlyList<GitStatusFingerprint> StatusEntries => _statusEntries;

    public IReadOnlyList<GitFileFingerprint> Files => _files;

    public bool Equals(GitWorktreeFingerprint? other)
    {
        return other is not null
            && string.Equals(HeadCommit, other.HeadCommit, StringComparison.Ordinal)
            && _statusEntries.SequenceEqual(other._statusEntries)
            && _files.SequenceEqual(other._files);
    }

    public override bool Equals(object? obj)
    {
        return obj is GitWorktreeFingerprint other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(HeadCommit, StringComparer.Ordinal);
        foreach (var entry in _statusEntries)
        {
            hash.Add(entry);
        }

        foreach (var file in _files)
        {
            hash.Add(file);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// Preserves one porcelain status record in deterministic path order.
/// </summary>
internal sealed record GitStatusFingerprint(
    string StatusCode,
    string Path,
    string? OriginalPath);

/// <summary>
/// Associates one changed worktree path with its Git blob object ID or a missing marker.
/// </summary>
internal sealed record GitFileFingerprint(string Path, string ObjectId)
{
    public const string MissingObjectId = "<missing>";

    public bool Exists => !string.Equals(ObjectId, MissingObjectId, StringComparison.Ordinal);
}

internal enum GitWorktreeFingerprintFailureKind
{
    GitFailure,
    TimedOut,
    InvalidOutput,
    UnstableCapture,
}

/// <summary>
/// Carries either a reusable fingerprint or a typed reason that cache reuse is unsafe.
/// </summary>
internal sealed record GitWorktreeFingerprintResolution(
    GitWorktreeFingerprint? Fingerprint,
    GitWorktreeFingerprintFailureKind? FailureKind,
    string? Diagnostic)
{
    public bool IsSuccessful => Fingerprint is not null;

    public static GitWorktreeFingerprintResolution Successful(GitWorktreeFingerprint fingerprint)
    {
        return new GitWorktreeFingerprintResolution(fingerprint, null, null);
    }

    public static GitWorktreeFingerprintResolution Failed(
        GitWorktreeFingerprintFailureKind failureKind,
        string diagnostic)
    {
        return new GitWorktreeFingerprintResolution(null, failureKind, diagnostic);
    }
}
