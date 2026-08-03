using System.Text;

namespace RoslynKit;

/// <summary>
/// Captures a stable Git worktree fingerprint and coalesces concurrent capture requests.
/// </summary>
internal sealed class GitWorktreeFingerprintService
{
    private const int MaxHashPathsPerBatch = 128;
    private const int MaxHashArgumentCharacters = 24_000;
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(5);
    private readonly string _worktreeRoot;
    private readonly Func<string, string, IReadOnlyList<string>, CancellationToken, Task<ProcessByteCommandResult>> _runProcessAsync;
    private readonly TimeSpan _deadline;
    private readonly object _captureGate = new();
    private Task<GitWorktreeFingerprintResolution>? _inProgress;

    public GitWorktreeFingerprintService(string worktreeRoot)
        : this(worktreeRoot, ProcessCommandRunner.RunBytesAsync, DefaultDeadline)
    {
    }

    internal GitWorktreeFingerprintService(
        string worktreeRoot,
        Func<string, string, IReadOnlyList<string>, CancellationToken, Task<ProcessByteCommandResult>> runProcessAsync,
        TimeSpan deadline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeRoot);
        ArgumentNullException.ThrowIfNull(runProcessAsync);
        if (deadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline), "The fingerprint deadline must be positive.");
        }

        _worktreeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreeRoot));
        _runProcessAsync = runProcessAsync;
        _deadline = deadline;
    }

    /// <summary>
    /// Reuses an in-progress capture for concurrent callers while preserving per-caller cancellation.
    /// </summary>
    public async Task<GitWorktreeFingerprintResolution> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<GitWorktreeFingerprintResolution> capture;
        lock (_captureGate)
        {
            if (_inProgress is null || _inProgress.IsCompleted)
            {
                capture = CaptureWithDeadlineAsync();
                _inProgress = capture;
                _ = ClearCompletedCaptureAsync(capture);
            }
            else
            {
                capture = _inProgress;
            }
        }

        return await capture.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitWorktreeFingerprintResolution> CaptureWithDeadlineAsync()
    {
        using var deadline = new CancellationTokenSource(_deadline);
        try
        {
            return await CaptureCoreAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return GitWorktreeFingerprintResolution.Failed(
                GitWorktreeFingerprintFailureKind.TimedOut,
                "Git fingerprint capture exceeded its total deadline.");
        }
        catch (GitFingerprintCaptureException exception)
        {
            return GitWorktreeFingerprintResolution.Failed(exception.FailureKind, exception.Message);
        }
        catch (Exception exception)
        {
            return GitWorktreeFingerprintResolution.Failed(
                GitWorktreeFingerprintFailureKind.GitFailure,
                $"Git fingerprint capture failed: {exception.Message}");
        }
    }

    private async Task<GitWorktreeFingerprintResolution> CaptureCoreAsync(
        CancellationToken cancellationToken)
    {
        var head0 = await ReadHeadAsync(cancellationToken).ConfigureAwait(false);
        var status0 = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!GitPorcelainParser.TryParse(status0, out var statusEntries, out var parseDiagnostic))
        {
            throw InvalidOutput(parseDiagnostic!);
        }

        var fileFingerprints = await HashChangedPathsAsync(
            statusEntries,
            cancellationToken).ConfigureAwait(false);
        var status1 = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        var head1 = await ReadHeadAsync(cancellationToken).ConfigureAwait(false);

        if (!string.Equals(head0, head1, StringComparison.Ordinal)
            || !status0.AsSpan().SequenceEqual(status1))
        {
            throw new GitFingerprintCaptureException(
                GitWorktreeFingerprintFailureKind.UnstableCapture,
                "Git worktree state changed during fingerprint capture.");
        }

        var sortedStatus = statusEntries
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ThenBy(static entry => entry.OriginalPath, StringComparer.Ordinal)
            .ThenBy(static entry => entry.StatusCode, StringComparer.Ordinal)
            .ToArray();

        return GitWorktreeFingerprintResolution.Successful(
            new GitWorktreeFingerprint(head0, sortedStatus, fileFingerprints));
    }

    private async Task<string> ReadHeadAsync(CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            ["-C", _worktreeRoot, "rev-parse", "--verify", "HEAD^{commit}"],
            cancellationToken).ConfigureAwait(false);
        EnsureSuccessful(result, "read HEAD");

        if (!TryParseObjectIds(result.StandardOutput, out var objectIds) || objectIds.Count != 1)
        {
            throw InvalidOutput("Git returned an invalid HEAD object ID.");
        }

        return objectIds[0];
    }

    private async Task<byte[]> ReadStatusAsync(CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            [
                "-C",
                _worktreeRoot,
                "--no-optional-locks",
                "status",
                "--porcelain=v1",
                "-z",
                "--untracked-files=all",
                "--no-renames",
                "--ignore-submodules=none",
            ],
            cancellationToken).ConfigureAwait(false);
        EnsureSuccessful(result, "read worktree status");
        return result.StandardOutput;
    }

    private async Task<IReadOnlyList<GitFileFingerprint>> HashChangedPathsAsync(
        IReadOnlyList<GitStatusFingerprint> statusEntries,
        CancellationToken cancellationToken)
    {
        var paths = statusEntries
            .SelectMany(GetRecordPaths)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var fingerprints = new List<GitFileFingerprint>(paths.Length);
        var existingPaths = new List<string>(paths.Length);

        foreach (var path in paths)
        {
            var fullPath = ResolveWorktreePath(path);
            if (IsExistingFile(fullPath))
            {
                existingPaths.Add(path);
            }
            else
            {
                fingerprints.Add(new GitFileFingerprint(path, GitFileFingerprint.MissingObjectId));
            }
        }

        foreach (var batch in CreateHashBatches(existingPaths))
        {
            var arguments = new List<string>(5 + batch.Count)
            {
                "-C",
                _worktreeRoot,
                "hash-object",
                "--no-filters",
                "--",
            };
            arguments.AddRange(batch);

            var result = await RunGitAsync(arguments, cancellationToken).ConfigureAwait(false);
            EnsureSuccessful(result, "hash changed worktree paths");
            if (!TryParseObjectIds(result.StandardOutput, out var objectIds)
                || objectIds.Count != batch.Count)
            {
                throw InvalidOutput(
                    $"Git returned {objectIds.Count} object IDs for {batch.Count} worktree paths.");
            }

            for (var index = 0; index < batch.Count; index++)
            {
                fingerprints.Add(new GitFileFingerprint(batch[index], objectIds[index]));
            }
        }

        return fingerprints
            .OrderBy(static fingerprint => fingerprint.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> GetRecordPaths(GitStatusFingerprint entry)
    {
        yield return entry.Path;
        if (entry.OriginalPath is not null)
        {
            yield return entry.OriginalPath;
        }
    }

    private IReadOnlyList<IReadOnlyList<string>> CreateHashBatches(IReadOnlyList<string> paths)
    {
        var batches = new List<IReadOnlyList<string>>();
        var batch = new List<string>();
        var argumentCharacters = _worktreeRoot.Length + 64;

        foreach (var path in paths)
        {
            var pathCharacters = path.Length + 3;
            if (batch.Count > 0
                && (batch.Count >= MaxHashPathsPerBatch
                    || argumentCharacters + pathCharacters > MaxHashArgumentCharacters))
            {
                batches.Add(batch);
                batch = [];
                argumentCharacters = _worktreeRoot.Length + 64;
            }

            batch.Add(path);
            argumentCharacters += pathCharacters;
        }

        if (batch.Count > 0)
        {
            batches.Add(batch);
        }

        return batches;
    }

    private string ResolveWorktreePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            throw InvalidOutput("Git status returned an absolute worktree path.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(_worktreeRoot, path));
        var relativePath = Path.GetRelativePath(_worktreeRoot, fullPath);
        if (Path.IsPathRooted(relativePath)
            || string.Equals(relativePath, "..", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw InvalidOutput("Git status returned a path outside the worktree.");
        }

        return fullPath;
    }

    private static bool IsExistingFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw InvalidOutput("Git status returned a directory despite --untracked-files=all.");
            }

            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private Task<ProcessByteCommandResult> RunGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        return _runProcessAsync("git", _worktreeRoot, arguments, cancellationToken);
    }

    private static void EnsureSuccessful(ProcessByteCommandResult result, string operation)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var detail = result.StandardError.Trim();
        var suffix = detail.Length == 0 ? string.Empty : $": {detail}";
        throw new GitFingerprintCaptureException(
            GitWorktreeFingerprintFailureKind.GitFailure,
            $"Git could not {operation} (exit code {result.ExitCode}){suffix}");
    }

    private static bool TryParseObjectIds(
        ReadOnlySpan<byte> output,
        out IReadOnlyList<string> objectIds)
    {
        var parsed = new List<string>();
        var offset = 0;
        while (offset < output.Length)
        {
            var remaining = output[offset..];
            var lineEnd = remaining.IndexOf((byte)'\n');
            ReadOnlySpan<byte> line;
            if (lineEnd < 0)
            {
                line = remaining;
                offset = output.Length;
            }
            else
            {
                line = remaining[..lineEnd];
                offset += lineEnd + 1;
            }

            if (!line.IsEmpty && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (line.IsEmpty || !IsObjectId(line))
            {
                objectIds = parsed;
                return false;
            }

            parsed.Add(Encoding.ASCII.GetString(line));
        }

        objectIds = parsed;
        return true;
    }

    private static bool IsObjectId(ReadOnlySpan<byte> value)
    {
        if (value.Length is not (40 or 64))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= (byte)'0' and <= (byte)'9')
                and not (>= (byte)'a' and <= (byte)'f')
                and not (>= (byte)'A' and <= (byte)'F'))
            {
                return false;
            }
        }

        return true;
    }

    private static GitFingerprintCaptureException InvalidOutput(string diagnostic)
    {
        return new GitFingerprintCaptureException(
            GitWorktreeFingerprintFailureKind.InvalidOutput,
            diagnostic);
    }

    private async Task ClearCompletedCaptureAsync(Task<GitWorktreeFingerprintResolution> capture)
    {
        try
        {
            await capture.ConfigureAwait(false);
        }
        finally
        {
            lock (_captureGate)
            {
                if (ReferenceEquals(_inProgress, capture))
                {
                    _inProgress = null;
                }
            }
        }
    }

    private sealed class GitFingerprintCaptureException(
        GitWorktreeFingerprintFailureKind failureKind,
        string message) : Exception(message)
    {
        public GitWorktreeFingerprintFailureKind FailureKind { get; } = failureKind;
    }
}
