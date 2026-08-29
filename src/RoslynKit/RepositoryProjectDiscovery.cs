using System.Text;

namespace RoslynKit;

/// <summary>
/// Discovers Git-visible C# project files for an implicit repository workspace.
/// </summary>
internal static class RepositoryProjectDiscovery
{
    public static async Task<IReadOnlyList<string>> DiscoverAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var result = await ProcessCommandRunner.RunBytesAsync(
            "git",
            repositoryRoot,
            [
                "-C",
                repositoryRoot,
                "ls-files",
                "-z",
                "--cached",
                "--others",
                "--exclude-standard",
            ],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail = result.StandardError.Trim();
            var suffix = detail.Length == 0 ? string.Empty : $": {detail}";
            throw new InvalidOperationException(
                $"Git could not enumerate repository projects (exit code {result.ExitCode}){suffix}.");
        }

        if (!TryParsePaths(result.StandardOutput, out var paths))
        {
            throw new InvalidOperationException(
                "Git returned invalid UTF-8 or an incomplete path while enumerating repository projects.");
        }

        var projectPaths = paths
            .Where(static path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFullPath(
                path.Replace('/', Path.DirectorySeparatorChar),
                repositoryRoot))
            .Where(File.Exists)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (projectPaths.Length == 0)
        {
            throw new CliUsageException(
                "unknown",
                $"Repository '{repositoryRoot}' does not contain any tracked or unignored C# project files.");
        }

        return projectPaths;
    }

    private static bool TryParsePaths(
        ReadOnlySpan<byte> output,
        out IReadOnlyList<string> paths)
    {
        var result = new List<string>();
        var offset = 0;
        while (offset < output.Length)
        {
            var remaining = output[offset..];
            var terminator = remaining.IndexOf((byte)0);
            if (terminator < 0)
            {
                paths = [];
                return false;
            }

            try
            {
                result.Add(new UTF8Encoding(false, true).GetString(remaining[..terminator]));
            }
            catch (DecoderFallbackException)
            {
                paths = [];
                return false;
            }

            offset += terminator + 1;
        }

        paths = result;
        return true;
    }
}
