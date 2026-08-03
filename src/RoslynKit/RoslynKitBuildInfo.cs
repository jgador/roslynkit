using System.Reflection;

namespace RoslynKit;

/// <summary>
/// Provides the public version, exact build identity, and daemon protocol version for compatibility checks.
/// </summary>
internal static class RoslynKitBuildInfo
{
    public const int DaemonProtocolVersion = 1;

    public static RoslynKitBuildIdentity Identity { get; } = CreateIdentity();

    public static string DisplayVersion => Identity.InformationalVersion;

    private static RoslynKitBuildIdentity CreateIdentity()
    {
        var assembly = typeof(RoslynKitBuildInfo).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            informationalVersion = assembly.GetName().Version?.ToString() ?? "unknown";
        }

        return new RoslynKitBuildIdentity(
            informationalVersion,
            assembly.ManifestModule.ModuleVersionId.ToString("D"));
    }
}
