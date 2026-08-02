using System.Security.Cryptography;
using System.Text.Json;

namespace RoslynKit;

/// <summary>
/// Derives a fixed-length opaque local endpoint name from canonical daemon identity bytes.
/// </summary>
internal static class DaemonEndpointName
{
    internal const string Prefix = "roslynkit-v1-";
    internal const int Length = 77;

    public static string Create(DaemonIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var canonicalIdentity = SerializeCanonical(identity);
        var digest = SHA256.HashData(canonicalIdentity);
        return Prefix + Convert.ToHexStringLower(digest);
    }

    internal static byte[] SerializeCanonical(DaemonIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(identity.User);
        ArgumentNullException.ThrowIfNull(identity.Workspace);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            var workspace = identity.Workspace;

            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteStartObject("user");
            writer.WriteString("kind", identity.User.Kind);
            writer.WriteString("value", identity.User.Value);
            writer.WriteEndObject();
            writer.WriteString("ipcRuntimeDirectory", identity.IpcRuntimeDirectory);
            writer.WriteStartObject("workspace");
            writer.WriteString("worktreeRoot", workspace.WorktreeRoot);
            writer.WriteString("targetPath", workspace.TargetPath);
            WriteGlobalJson(writer, workspace.GlobalJson);
            writer.WriteStartObject("dotnetSdk");
            writer.WriteString("version", workspace.DotNetSdk.Version);
            writer.WriteEndObject();
            writer.WriteStartObject("msbuild");
            writer.WriteString("name", workspace.MSBuild.Name);
            writer.WriteString("discoveryType", workspace.MSBuild.DiscoveryType);
            writer.WriteString("instanceVersion", workspace.MSBuild.InstanceVersion);
            writer.WriteString("path", workspace.MSBuild.MSBuildPath);
            writer.WriteString("assemblyVersion", workspace.MSBuild.MSBuildAssemblyVersion);
            writer.WriteEndObject();
            writer.WriteStartObject("buildEnvironment");
            foreach (var pair in workspace.BuildEnvironment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
            writer.WriteStartObject("roslynKit");
            writer.WriteString("informationalVersion", workspace.RoslynKit.InformationalVersion);
            writer.WriteString("moduleVersionId", workspace.RoslynKit.ModuleVersionId);
            writer.WriteEndObject();
            writer.WriteNumber("protocolVersion", workspace.ProtocolVersion);
            writer.WriteString("processArchitecture", workspace.ProcessArchitecture);
            WriteTypeScriptRuntime(writer, workspace.TypeScriptRuntime);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteGlobalJson(Utf8JsonWriter writer, GlobalJsonIdentity? globalJson)
    {
        writer.WritePropertyName("globalJson");
        if (globalJson is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("path", globalJson.Path);
        writer.WriteString("sha256", globalJson.Sha256);
        writer.WriteEndObject();
    }

    private static void WriteTypeScriptRuntime(
        Utf8JsonWriter writer,
        TypeScriptRuntimeIdentity? runtime)
    {
        writer.WritePropertyName("typeScriptRuntime");
        if (runtime is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("nodePath", runtime.NodePath);
        writer.WriteString("nodeVersion", runtime.NodeVersion);
        writer.WriteString("bridgePath", runtime.BridgePath);
        writer.WriteString("bridgeSha256", runtime.BridgeSha256);
        writer.WriteString("nativePreviewRoot", runtime.NativePreviewRoot);
        writer.WriteString("nativePreviewVersion", runtime.NativePreviewVersion);
        writer.WriteEndObject();
    }
}
