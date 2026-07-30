using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace RoslynKit;

/// <summary>
/// Captures every stable input that selects one compatible workspace daemon.
/// </summary>
internal sealed record DaemonIdentity(
    DaemonUserIdentity User,
    string IpcRuntimeDirectory,
    GitWorkspaceIdentity Workspace);

/// <summary>
/// Identifies the operating-system user allowed to share a daemon endpoint.
/// </summary>
internal sealed record DaemonUserIdentity(string Kind, string Value);

/// <summary>
/// Adds same-user and local-IPC inputs to a resolved Git workspace identity.
/// </summary>
internal sealed class DaemonIdentityResolver
{
    internal const string UnixEffectiveUserIdKind = "unix-euid";
    internal const string WindowsSidKind = "windows-sid";

    private readonly Func<DaemonUserIdentity> _resolveCurrentUser;
    private readonly Func<string> _resolveIpcRuntimeDirectory;

    public DaemonIdentityResolver()
        : this(ResolveCurrentUser, Path.GetTempPath)
    {
    }

    internal DaemonIdentityResolver(
        Func<DaemonUserIdentity> resolveCurrentUser,
        Func<string> resolveIpcRuntimeDirectory)
    {
        _resolveCurrentUser = resolveCurrentUser;
        _resolveIpcRuntimeDirectory = resolveIpcRuntimeDirectory;
    }

    /// <summary>
    /// Resolves the process-local compatibility inputs required before endpoint naming.
    /// </summary>
    public DaemonIdentity Resolve(GitWorkspaceIdentity workspaceIdentity)
    {
        ArgumentNullException.ThrowIfNull(workspaceIdentity);

        var user = _resolveCurrentUser()
            ?? throw new InvalidOperationException("The current operating-system user could not be resolved.");
        if (string.IsNullOrWhiteSpace(user.Kind) || string.IsNullOrWhiteSpace(user.Value))
        {
            throw new InvalidOperationException("The current operating-system user identity is incomplete.");
        }

        var runtimeDirectory = _resolveIpcRuntimeDirectory();
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            throw new InvalidOperationException("The local IPC runtime directory could not be resolved.");
        }

        return new DaemonIdentity(
            user,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeDirectory)),
            CloneWorkspaceIdentity(workspaceIdentity));
    }

    private static GitWorkspaceIdentity CloneWorkspaceIdentity(GitWorkspaceIdentity identity)
    {
        var environment = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in identity.BuildEnvironment)
        {
            environment.Add(pair.Key, pair.Value);
        }

        return identity with
        {
            BuildEnvironment = new ReadOnlyDictionary<string, string?>(environment),
        };
    }

    private static DaemonUserIdentity ResolveCurrentUser()
    {
        if (OperatingSystem.IsWindows())
        {
            return ResolveWindowsUser();
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            return new DaemonUserIdentity(
                UnixEffectiveUserIdKind,
                GetEffectiveUserId().ToString(CultureInfo.InvariantCulture));
        }

        throw new PlatformNotSupportedException("RoslynKit daemon identity requires Windows or a Unix-like operating system.");
    }

    [SupportedOSPlatform("windows")]
    private static DaemonUserIdentity ResolveWindowsUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            throw new InvalidOperationException("The current Windows security identifier could not be resolved.");
        }

        return new DaemonUserIdentity(WindowsSidKind, sid);
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
