namespace RoslynKit;

/// <summary>
/// Identifies the installed SDK selected by the dotnet host from one repository working directory.
/// </summary>
internal sealed record DotnetSdkResolution(
    string DotnetPath,
    string Version,
    string SdkDirectory,
    string? GlobalJsonPath);

/// <summary>
/// Honors global.json through the installed dotnet host without downloading or installing an SDK.
/// </summary>
internal static class DotnetSdkResolver
{
    public static Task<DotnetSdkResolution> ResolveAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        return ResolveAsync(repositoryRoot, new WorkspaceProcessRunner(), cancellationToken);
    }

    internal static async Task<DotnetSdkResolution> ResolveAsync(
        string repositoryRoot,
        IWorkspaceProcessRunner runner,
        CancellationToken cancellationToken)
    {
        WorkspaceSupportValidator.ValidatePlatform();
        var dotnetPath = FindDotnetHost();
        var versionResult = await runner.RunAsync(
            dotnetPath, repositoryRoot, ["--version"], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (versionResult.ExitCode != 0)
        {
            throw new WorkspacePreparationException(
                $"No compatible installed .NET SDK could be selected for '{repositoryRoot}'. " +
                $"Check global.json and installed SDKs. {DescribeFailure(versionResult)}");
        }

        var version = versionResult.StandardOutput.Trim();
        var listResult = await runner.RunAsync(
            dotnetPath, repositoryRoot, ["--list-sdks"], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (listResult.ExitCode != 0)
        {
            throw new WorkspacePreparationException($"Could not enumerate installed .NET SDKs. {DescribeFailure(listResult)}");
        }

        foreach (var line in listResult.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var bracket = line.IndexOf('[');
            var closingBracket = line.LastIndexOf(']');
            if (bracket < 1 || closingBracket <= bracket || !line[..bracket].Trim().Equals(version, StringComparison.Ordinal))
            {
                continue;
            }

            var sdkDirectory = Path.Combine(line[(bracket + 1)..closingBracket], version);
            if (!File.Exists(Path.Combine(sdkDirectory, "MSBuild.dll")))
            {
                continue;
            }

            return new DotnetSdkResolution(
                dotnetPath,
                version,
                PathCanonicalizer.ResolveExistingPath(sdkDirectory),
                FindGlobalJson(repositoryRoot));
        }

        throw new WorkspacePreparationException(
            $"The selected .NET SDK '{version}' has no usable installed MSBuild directory. Check the dotnet installation and global.json.");
    }

    internal static string DescribeFailure(ProcessCommandResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        const int maximumDetailCharacters = 4096;
        if (detail.Length > maximumDetailCharacters)
        {
            detail = detail[..maximumDetailCharacters] + " [truncated]";
        }

        return $"Exit code {result.ExitCode}. {detail}".TrimEnd();
    }

    private static string FindDotnetHost()
    {
        var explicitHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(explicitHost) && File.Exists(explicitHost))
        {
            return Path.GetFullPath(explicitHost);
        }

        var executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory.Trim('"'), executableName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new WorkspacePreparationException("An installed dotnet host is required on PATH. RoslynKit does not install SDKs.");
    }

    private static string? FindGlobalJson(string repositoryRoot)
    {
        for (var directory = new DirectoryInfo(repositoryRoot); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "global.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
