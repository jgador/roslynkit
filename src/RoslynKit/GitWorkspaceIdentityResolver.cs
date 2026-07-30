using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Build.Locator;

namespace RoslynKit;

/// <summary>
/// Resolves the supported Git worktree boundary and immutable compatibility inputs for a workspace target.
/// </summary>
internal sealed class GitWorkspaceIdentityResolver
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly string[] BuildEnvironmentVariableNames =
    [
        "Configuration",
        "DOTNET_CLI_HOME",
        "DOTNET_HOST_PATH",
        "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR",
        "DOTNET_ROLL_FORWARD",
        "DOTNET_ROLL_FORWARD_TO_PRERELEASE",
        "DOTNET_ROOT",
        "DOTNET_ROOT_X64",
        "DOTNET_ROOT_X86",
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildExtensionsPath32",
        "MSBuildExtensionsPath64",
        "MSBuildSDKsPath",
        "NUGET_FALLBACK_PACKAGES",
        "NUGET_HTTP_CACHE_PATH",
        "NUGET_PACKAGES",
        "NUGET_PLUGINS_CACHE_PATH",
        "NUGET_PLUGIN_PATHS",
        "Platform",
    ];

    private readonly Func<string, string, IReadOnlyList<string>, CancellationToken, Task<ProcessCommandResult>> _runProcessAsync;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string, MSBuildInstanceIdentity> _resolveMSBuildIdentity;

    public GitWorkspaceIdentityResolver()
        : this(ProcessCommandRunner.RunAsync, Environment.GetEnvironmentVariable, ResolveMSBuildIdentity)
    {
    }

    internal GitWorkspaceIdentityResolver(
        Func<string, string, IReadOnlyList<string>, CancellationToken, Task<ProcessCommandResult>> runProcessAsync,
        Func<string, string?> getEnvironmentVariable,
        Func<string, MSBuildInstanceIdentity> resolveMSBuildIdentity)
    {
        _runProcessAsync = runProcessAsync;
        _getEnvironmentVariable = getEnvironmentVariable;
        _resolveMSBuildIdentity = resolveMSBuildIdentity;
    }

    /// <summary>
    /// Validates one target without loading its workspace and captures the daemon compatibility inputs.
    /// </summary>
    public async Task<GitWorkspaceIdentityResolution> ResolveAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var canonicalTarget = ResolveCanonicalTarget(targetPath);
            if (canonicalTarget is null)
            {
                return Unsupported("The target must be an existing .sln, .slnx, or .csproj file.");
            }

            var targetDirectory = Path.GetDirectoryName(canonicalTarget)!;
            var rootResult = await RunGitAsync(
                targetDirectory,
                ["rev-parse", "--path-format=absolute", "--show-toplevel"],
                cancellationToken).ConfigureAwait(false);
            if (rootResult.ExitCode != 0 || string.IsNullOrWhiteSpace(rootResult.StandardOutput))
            {
                return Unsupported("The target is not inside a Git worktree.");
            }

            var worktreeRoot = NormalizeDirectoryPath(rootResult.StandardOutput.Trim());
            if (!IsPathInside(canonicalTarget, worktreeRoot))
            {
                return Unsupported("The target is outside the resolved Git worktree.");
            }

            var headResult = await RunGitAsync(
                worktreeRoot,
                ["rev-parse", "--verify", "HEAD^{commit}"],
                cancellationToken).ConfigureAwait(false);
            if (headResult.ExitCode != 0 || string.IsNullOrWhiteSpace(headResult.StandardOutput))
            {
                return Unsupported("The Git worktree does not have a committed HEAD.");
            }

            var superprojectResult = await RunGitAsync(
                worktreeRoot,
                ["rev-parse", "--show-superproject-working-tree"],
                cancellationToken).ConfigureAwait(false);
            if (superprojectResult.ExitCode != 0)
            {
                return Infrastructure("Git could not determine whether the worktree is a submodule.", superprojectResult);
            }

            if (!string.IsNullOrWhiteSpace(superprojectResult.StandardOutput))
            {
                return Unsupported("Git submodule worktrees are not supported for daemon acceleration.");
            }

            var nestedRepository = await FindContainingRepositoryAsync(worktreeRoot, cancellationToken).ConfigureAwait(false);
            if (nestedRepository is not null)
            {
                return Unsupported($"Nested Git repositories are not supported; containing worktree: '{nestedRepository}'.");
            }

            var submoduleResult = await RunGitAsync(
                worktreeRoot,
                ["submodule", "status", "--recursive"],
                cancellationToken).ConfigureAwait(false);
            if (submoduleResult.ExitCode != 0)
            {
                return Infrastructure("Git could not inspect repository submodules.", submoduleResult);
            }

            if (!string.IsNullOrWhiteSpace(submoduleResult.StandardOutput))
            {
                return Unsupported("Repositories containing Git submodules are not supported for daemon acceleration.");
            }

            var worktreeResult = await RunGitAsync(
                worktreeRoot,
                ["worktree", "list", "--porcelain", "-z"],
                cancellationToken).ConfigureAwait(false);
            if (worktreeResult.ExitCode != 0)
            {
                return Infrastructure("Git could not enumerate repository worktrees.", worktreeResult);
            }

            var matchingWorktrees = ParseWorktreePaths(worktreeResult.StandardOutput)
                .Count(path => PathComparer.Equals(path, worktreeRoot));
            if (matchingWorktrees != 1)
            {
                return Unsupported("The target must resolve to exactly one registered Git worktree.");
            }

            var globalJson = await ResolveGlobalJsonAsync(targetDirectory, cancellationToken).ConfigureAwait(false);
            var dotnetSdkResult = await RunDotNetAsync(targetDirectory, ["--version"], cancellationToken).ConfigureAwait(false);
            if (dotnetSdkResult.ExitCode != 0 || string.IsNullOrWhiteSpace(dotnetSdkResult.StandardOutput))
            {
                return Infrastructure("The .NET SDK selected for the target could not be resolved.", dotnetSdkResult);
            }

            var identity = new GitWorkspaceIdentity(
                worktreeRoot,
                canonicalTarget,
                globalJson,
                new DotNetSdkIdentity(dotnetSdkResult.StandardOutput.Trim()),
                _resolveMSBuildIdentity(targetDirectory),
                CaptureBuildEnvironment(),
                RoslynKitBuildInfo.Identity,
                RoslynKitBuildInfo.DaemonProtocolVersion,
                RuntimeInformation.ProcessArchitecture.ToString());

            return GitWorkspaceIdentityResolution.Supported(identity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return GitWorkspaceIdentityResolution.Failed(
                GitWorkspaceIdentityFailureKind.Infrastructure,
                $"Workspace identity resolution failed: {ex.Message}");
        }
    }

    private static string? ResolveCanonicalTarget(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        var fullPath = ResolveExistingPath(targetPath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return fullPath;
    }

    private async Task<string?> FindContainingRepositoryAsync(
        string worktreeRoot,
        CancellationToken cancellationToken)
    {
        var parent = Directory.GetParent(worktreeRoot);
        if (parent is null)
        {
            return null;
        }

        var result = await RunGitAsync(
            parent.FullName,
            ["rev-parse", "--path-format=absolute", "--show-toplevel"],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        var containingRoot = NormalizeDirectoryPath(result.StandardOutput.Trim());
        return PathComparer.Equals(containingRoot, worktreeRoot) ? null : containingRoot;
    }

    private async Task<GlobalJsonIdentity?> ResolveGlobalJsonAsync(
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        for (var directory = new DirectoryInfo(targetDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "global.json");
            if (!File.Exists(candidate))
            {
                continue;
            }

            await using var stream = File.OpenRead(candidate);
            var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return new GlobalJsonIdentity(
                Path.GetFullPath(candidate),
                Convert.ToHexStringLower(digest));
        }

        return null;
    }

    private IReadOnlyDictionary<string, string?> CaptureBuildEnvironment()
    {
        var values = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var variableName in BuildEnvironmentVariableNames)
        {
            values.Add(variableName, _getEnvironmentVariable(variableName));
        }

        return new ReadOnlyDictionary<string, string?>(values);
    }

    private static MSBuildInstanceIdentity ResolveMSBuildIdentity(string workingDirectory)
    {
        var defaults = VisualStudioInstanceQueryOptions.Default;
        var options = new VisualStudioInstanceQueryOptions
        {
            AllowAllDotnetLocations = defaults.AllowAllDotnetLocations,
            AllowAllRuntimeVersions = defaults.AllowAllRuntimeVersions,
            DiscoveryTypes = defaults.DiscoveryTypes,
            WorkingDirectory = workingDirectory,
        };
        var instance = MSBuildLocator.QueryVisualStudioInstances(options).FirstOrDefault()
            ?? throw new InvalidOperationException("No compatible MSBuild instance was found for the target.");
        var msbuildAssemblyPath = Path.Combine(instance.MSBuildPath, "MSBuild.dll");
        var assemblyVersion = File.Exists(msbuildAssemblyPath)
            ? FileVersionInfo.GetVersionInfo(msbuildAssemblyPath).ProductVersion
            : null;

        return new MSBuildInstanceIdentity(
            instance.Name,
            instance.DiscoveryType.ToString(),
            instance.Version.ToString(),
            NormalizeDirectoryPath(instance.MSBuildPath),
            assemblyVersion);
    }

    private Task<ProcessCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        return _runProcessAsync("git", workingDirectory, arguments, cancellationToken);
    }

    private Task<ProcessCommandResult> RunDotNetAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var dotnetHostPath = _getEnvironmentVariable("DOTNET_HOST_PATH");
        var executable = string.IsNullOrWhiteSpace(dotnetHostPath) ? "dotnet" : dotnetHostPath;
        return _runProcessAsync(executable, workingDirectory, arguments, cancellationToken);
    }

    private static IReadOnlyList<string> ParseWorktreePaths(string porcelain)
    {
        return porcelain
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(field => field.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(field => NormalizeDirectoryPath(field["worktree ".Length..]))
            .ToArray();
    }

    private static string NormalizeDirectoryPath(string path)
    {
        return Path.TrimEndingDirectorySeparator(ResolveExistingPath(path));
    }

    private static string ResolveExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        var resolvedPath = root;
        foreach (var segment in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(resolvedPath, segment);
            FileSystemInfo fileSystemInfo = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : new FileInfo(candidate);
            var linkTarget = fileSystemInfo.ResolveLinkTarget(returnFinalTarget: false);
            resolvedPath = linkTarget is null
                ? candidate
                : Path.GetFullPath(linkTarget.FullName);
        }

        return Path.GetFullPath(resolvedPath);
    }

    private static bool IsPathInside(string path, string root)
    {
        var relativePath = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relativePath)
            && !relativePath.Equals("..", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static GitWorkspaceIdentityResolution Unsupported(string diagnostic)
    {
        return GitWorkspaceIdentityResolution.Failed(
            GitWorkspaceIdentityFailureKind.UnsupportedWorkspace,
            diagnostic);
    }

    private static GitWorkspaceIdentityResolution Infrastructure(
        string diagnostic,
        ProcessCommandResult processResult)
    {
        var processDiagnostic = string.IsNullOrWhiteSpace(processResult.StandardError)
            ? $"exit code {processResult.ExitCode}"
            : processResult.StandardError.Trim();
        return GitWorkspaceIdentityResolution.Failed(
            GitWorkspaceIdentityFailureKind.Infrastructure,
            $"{diagnostic} {processDiagnostic}");
    }
}
