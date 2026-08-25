namespace RoslynKit;

/// <summary>
/// Captures the stable workspace and toolchain inputs that determine daemon compatibility.
/// </summary>
internal sealed record GitWorkspaceIdentity(
    string WorktreeRoot,
    string TargetPath,
    GlobalJsonIdentity? GlobalJson,
    MSBuildInstanceIdentity MSBuild,
    IReadOnlyDictionary<string, string?> BuildEnvironment,
    string ProcessArchitecture);

/// <summary>
/// Identifies the nearest <c>global.json</c> by canonical path and exact content digest.
/// </summary>
internal sealed record GlobalJsonIdentity(string Path, string Sha256);

/// <summary>
/// Identifies the MSBuild installation selected for the target directory.
/// </summary>
internal sealed record MSBuildInstanceIdentity(
    string Name,
    string DiscoveryType,
    string MSBuildPath);

/// <summary>
/// Distinguishes unsupported workspace boundaries from failures in identity infrastructure.
/// </summary>
internal enum GitWorkspaceIdentityFailureKind
{
    UnsupportedWorkspace,
    Infrastructure,
}

/// <summary>
/// Returns either a supported Git workspace identity or the fallback classification and diagnostic.
/// </summary>
internal sealed record GitWorkspaceIdentityResolution(
    GitWorkspaceIdentity? Identity,
    GitWorkspaceIdentityFailureKind? FailureKind,
    string? Diagnostic)
{
    public bool IsSupported => Identity is not null;

    public static GitWorkspaceIdentityResolution Supported(GitWorkspaceIdentity identity)
    {
        return new GitWorkspaceIdentityResolution(identity, null, null);
    }

    public static GitWorkspaceIdentityResolution Failed(
        GitWorkspaceIdentityFailureKind failureKind,
        string diagnostic)
    {
        return new GitWorkspaceIdentityResolution(null, failureKind, diagnostic);
    }
}
