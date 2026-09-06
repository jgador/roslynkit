using System.Diagnostics;

namespace RoslynKit;

/// <summary>
/// Removes inherited SDK assembly bindings so each child selects MSBuild from its own repository SDK.
/// </summary>
internal static class DotnetProcessEnvironment
{
    private static readonly HashSet<string> SdkBindingVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildExtensionsPath32",
        "MSBuildExtensionsPath64",
        "MSBuildSDKsPath",
        "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR",
        "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR",
        "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER",
    };

    public static void ClearInheritedSdkBindings(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        foreach (var name in startInfo.Environment.Keys.Where(SdkBindingVariables.Contains).ToArray())
        {
            startInfo.Environment.Remove(name);
        }
    }
}
