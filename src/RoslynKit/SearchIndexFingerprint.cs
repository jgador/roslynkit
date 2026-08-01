using System.Security.Cryptography;
using System.Text;

namespace RoslynKit;

/// <summary>
/// Captures the stable Git-backed state used to decide whether one target search index is stale.
/// </summary>
internal sealed class SearchIndexFingerprintService
{
    private const string FingerprintDiscriminator = "roslynkit-search-index-fingerprint";
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly SearchIndexPath _indexPath;
    private readonly GitWorktreeFingerprintService _worktreeFingerprintService;
    private readonly Func<string, string, IReadOnlyList<string>, CancellationToken, Task<ProcessByteCommandResult>> _runProcessAsync;
    private readonly TimeSpan _deadline;

    public SearchIndexFingerprintService(SearchIndexPath indexPath)
        : this(
            indexPath,
            new GitWorktreeFingerprintService(indexPath.RepositoryRoot),
            ProcessCommandRunner.RunBytesAsync,
            DefaultDeadline)
    {
    }

    internal SearchIndexFingerprintService(
        SearchIndexPath indexPath,
        GitWorktreeFingerprintService worktreeFingerprintService)
        : this(
            indexPath,
            worktreeFingerprintService,
            ProcessCommandRunner.RunBytesAsync,
            DefaultDeadline)
    {
    }

    internal SearchIndexFingerprintService(
        SearchIndexPath indexPath,
        GitWorktreeFingerprintService worktreeFingerprintService,
        Func<string, string, IReadOnlyList<string>, CancellationToken, Task<ProcessByteCommandResult>> runProcessAsync,
        TimeSpan deadline)
    {
        ArgumentNullException.ThrowIfNull(indexPath);
        ArgumentNullException.ThrowIfNull(worktreeFingerprintService);
        ArgumentNullException.ThrowIfNull(runProcessAsync);
        if (deadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline), "The change-range deadline must be positive.");
        }

        _indexPath = indexPath;
        _worktreeFingerprintService = worktreeFingerprintService;
        _runProcessAsync = runProcessAsync;
        _deadline = deadline;
    }

    /// <summary>
    /// Produces a target-specific fingerprint from a stable, bounded Git worktree capture.
    /// </summary>
    public async Task<SearchIndexFingerprintResolution> CaptureAsync(CancellationToken cancellationToken)
    {
        var worktreeResolution = await _worktreeFingerprintService
            .CaptureAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!worktreeResolution.IsSuccessful)
        {
            return SearchIndexFingerprintResolution.Failed(
                worktreeResolution.FailureKind!.Value,
                worktreeResolution.Diagnostic!);
        }

        return SearchIndexFingerprintResolution.Successful(
            FromWorktreeFingerprint(worktreeResolution.Fingerprint!));
    }

    /// <summary>
    /// Derives target-specific state from a caller-owned stable Git worktree capture.
    /// </summary>
    internal SearchIndexFingerprint FromWorktreeFingerprint(GitWorktreeFingerprint worktreeFingerprint)
    {
        ArgumentNullException.ThrowIfNull(worktreeFingerprint);

        var changedPaths = worktreeFingerprint.StatusEntries
            .SelectMany(GetRecordPaths)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var changes = CreateChangedPaths(changedPaths);
        var value = CreateValue(_indexPath, worktreeFingerprint);

        return new SearchIndexFingerprint(
            value,
            worktreeFingerprint.HeadCommit,
            _indexPath.TargetPath,
            worktreeFingerprint,
            changes.Paths,
            changes.ChangedSourcePaths,
            changes.RequiresFullRebuild);
    }

    /// <summary>
    /// Lists the repository-relative paths changed by a validated, bounded Git commit range.
    /// </summary>
    public async Task<SearchIndexChangedPathsResolution> ListChangedPathsAsync(
        string oldCommit,
        string newCommit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCommitId(oldCommit) || !IsCommitId(newCommit))
        {
            return SearchIndexChangedPathsResolution.Failed(
                SearchIndexChangedPathsFailureKind.InvalidCommit,
                "Search index change comparison requires full 40- or 64-character hexadecimal Git commit IDs.");
        }

        using var deadline = new CancellationTokenSource(_deadline);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            var result = await _runProcessAsync(
                "git",
                _indexPath.RepositoryRoot,
                [
                    "-C",
                    _indexPath.RepositoryRoot,
                    "diff",
                    "--name-only",
                    "-z",
                    oldCommit,
                    newCommit,
                    "--",
                ],
                linkedCancellation.Token).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return SearchIndexChangedPathsResolution.Failed(
                    SearchIndexChangedPathsFailureKind.GitFailure,
                    $"Git could not list paths changed between '{oldCommit}' and '{newCommit}' (exit code {result.ExitCode}){FormatGitError(result.StandardError)}.");
            }

            if (!TryParseChangedPaths(result.StandardOutput, out var paths, out var diagnostic))
            {
                return SearchIndexChangedPathsResolution.Failed(
                    SearchIndexChangedPathsFailureKind.InvalidOutput,
                    diagnostic!);
            }

            return SearchIndexChangedPathsResolution.Successful(CreateChangedPaths(paths));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return SearchIndexChangedPathsResolution.Failed(
                SearchIndexChangedPathsFailureKind.TimedOut,
                "Search index change comparison exceeded its 5-second deadline.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return SearchIndexChangedPathsResolution.Failed(
                SearchIndexChangedPathsFailureKind.GitFailure,
                $"Search index change comparison failed: {exception.Message}");
        }
    }

    internal static bool TryParseChangedPaths(
        ReadOnlySpan<byte> output,
        out IReadOnlyList<string> paths,
        out string? diagnostic)
    {
        var parsed = new List<string>();
        var offset = 0;
        while (offset < output.Length)
        {
            var remaining = output[offset..];
            var terminator = remaining.IndexOf((byte)0);
            if (terminator < 0)
            {
                paths = parsed;
                diagnostic = "Git returned change paths without required NUL delimiters.";
                return false;
            }

            var bytes = remaining[..terminator];
            offset += terminator + 1;
            if (bytes.IsEmpty)
            {
                paths = parsed;
                diagnostic = "Git returned an empty changed path.";
                return false;
            }

            string path;
            try
            {
                path = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                paths = parsed;
                diagnostic = "Git returned a changed path that is not valid UTF-8.";
                return false;
            }

            if (!IsRepositoryRelativePath(path))
            {
                paths = parsed;
                diagnostic = "Git returned a changed path outside the repository.";
                return false;
            }

            parsed.Add(path);
        }

        paths = parsed
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        diagnostic = null;
        return true;
    }

    private static IEnumerable<string> GetRecordPaths(GitStatusFingerprint record)
    {
        yield return record.Path;
        if (record.OriginalPath is not null)
        {
            yield return record.OriginalPath;
        }
    }

    private static SearchIndexChangedPaths CreateChangedPaths(IEnumerable<string> paths)
    {
        var orderedPaths = paths
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new SearchIndexChangedPaths(
            orderedPaths,
            orderedPaths.Where(IsCSharpSourcePath).ToArray(),
            orderedPaths.Any(IsBuildConfigurationPath));
    }

    private static string CreateValue(SearchIndexPath indexPath, GitWorktreeFingerprint fingerprint)
    {
        var input = new StringBuilder();
        AppendPart(input, FingerprintDiscriminator);
        AppendPart(input, ToRepositoryRelativePath(indexPath.RepositoryRoot, indexPath.TargetPath));
        AppendPart(input, fingerprint.HeadCommit);

        foreach (var status in fingerprint.StatusEntries)
        {
            AppendPart(input, status.StatusCode);
            AppendPart(input, status.Path);
            AppendPart(input, status.OriginalPath ?? string.Empty);
        }

        foreach (var file in fingerprint.Files)
        {
            AppendPart(input, file.Path);
            AppendPart(input, file.ObjectId);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendPart(StringBuilder builder, string value)
    {
        builder.Append(value.Length).Append(':').Append(value).Append('\n');
    }

    private static string ToRepositoryRelativePath(string repositoryRoot, string targetPath)
    {
        return Path.GetRelativePath(repositoryRoot, targetPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static bool IsCSharpSourcePath(string path)
    {
        return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBuildConfigurationPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ruleset", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "global.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommitId(string? value)
    {
        if (value is null || value.Length is not (40 or 64))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')
                and not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRepositoryRelativePath(string path)
    {
        if (Path.IsPathRooted(path)
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("\\", StringComparison.Ordinal)
            || string.Equals(path, "..", StringComparison.Ordinal)
            || path.StartsWith("../", StringComparison.Ordinal)
            || path.StartsWith("..\\", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static string FormatGitError(string standardError)
    {
        var detail = standardError.Trim();
        return detail.Length == 0 ? string.Empty : $": {detail}";
    }
}

/// <summary>
/// Represents one deterministic view of state relevant to a target search index.
/// </summary>
internal sealed record SearchIndexFingerprint(
    string Value,
    string HeadCommit,
    string TargetPath,
    GitWorktreeFingerprint WorktreeFingerprint,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<string> ChangedSourcePaths,
    bool RequiresFullRebuild);

/// <summary>
/// Classifies a deterministic collection of repository-relative paths changed by Git.
/// </summary>
internal sealed record SearchIndexChangedPaths(
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> ChangedSourcePaths,
    bool RequiresFullRebuild);

internal enum SearchIndexChangedPathsFailureKind
{
    InvalidCommit,
    InvalidOutput,
    GitFailure,
    TimedOut,
}

/// <summary>
/// Carries classified commit-range changes or a failure that makes incremental refresh unsafe.
/// </summary>
internal sealed record SearchIndexChangedPathsResolution(
    SearchIndexChangedPaths? Changes,
    SearchIndexChangedPathsFailureKind? FailureKind,
    string? Diagnostic)
{
    public bool IsSuccessful => Changes is not null;

    public static SearchIndexChangedPathsResolution Successful(SearchIndexChangedPaths changes)
    {
        return new SearchIndexChangedPathsResolution(changes, null, null);
    }

    public static SearchIndexChangedPathsResolution Failed(
        SearchIndexChangedPathsFailureKind failureKind,
        string diagnostic)
    {
        return new SearchIndexChangedPathsResolution(null, failureKind, diagnostic);
    }
}

/// <summary>
/// Carries a reusable search fingerprint or the reason refresh safety cannot be established.
/// </summary>
internal sealed record SearchIndexFingerprintResolution(
    SearchIndexFingerprint? Fingerprint,
    GitWorktreeFingerprintFailureKind? FailureKind,
    string? Diagnostic)
{
    public bool IsSuccessful => Fingerprint is not null;

    public static SearchIndexFingerprintResolution Successful(SearchIndexFingerprint fingerprint)
    {
        return new SearchIndexFingerprintResolution(fingerprint, null, null);
    }

    public static SearchIndexFingerprintResolution Failed(
        GitWorktreeFingerprintFailureKind failureKind,
        string diagnostic)
    {
        return new SearchIndexFingerprintResolution(null, failureKind, diagnostic);
    }
}
