namespace RoslynKit;

/// <summary>
/// Captures the stable workspace and toolchain inputs that determine daemon compatibility.
/// </summary>
internal sealed record GitWorkspaceIdentity(
    string WorktreeRoot,
    string TargetPath,
    GlobalJsonIdentity? GlobalJson,
    DotNetSdkIdentity DotNetSdk,
    MSBuildInstanceIdentity MSBuild,
    IReadOnlyDictionary<string, string?> BuildEnvironment,
    RoslynKitBuildIdentity RoslynKit,
    int ProtocolVersion,
    string ProcessArchitecture);

/// <summary>
/// Identifies the nearest <c>global.json</c> by canonical path and exact content digest.
/// </summary>
internal sealed record GlobalJsonIdentity(string Path, string Sha256);

/// <summary>
/// Identifies the .NET SDK selected from the target directory.
/// </summary>
internal sealed record DotNetSdkIdentity(string Version);

/// <summary>
/// Identifies the MSBuild installation selected for the target directory.
/// </summary>
internal sealed record MSBuildInstanceIdentity(
    string Name,
    string DiscoveryType,
    string InstanceVersion,
    string MSBuildPath,
    string? MSBuildAssemblyVersion);

/// <summary>
/// Identifies the exact RoslynKit build independently of its public package version.
/// </summary>
internal sealed record RoslynKitBuildIdentity(string InformationalVersion, string ModuleVersionId);

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
