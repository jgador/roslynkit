using System.Security.Cryptography;
using System.Text.Json;

namespace RoslynKit;

/// <summary>
/// Derives a fixed-length opaque local endpoint name from canonical daemon identity bytes.
/// </summary>
internal static class DaemonEndpointName
{
    internal const string Prefix = "roslynkit-";
    internal const int Length = 74;

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
            writer.WriteStartObject("user");
            writer.WriteString("kind", identity.User.Kind);
            writer.WriteString("value", identity.User.Value);
            writer.WriteEndObject();
            writer.WriteString("ipcRuntimeDirectory", identity.IpcRuntimeDirectory);
            writer.WriteStartObject("workspace");
            writer.WriteString("worktreeRoot", workspace.WorktreeRoot);
            writer.WriteString("targetPath", workspace.TargetPath);
            WriteGlobalJson(writer, workspace.GlobalJson);
            writer.WriteStartObject("msbuild");
            writer.WriteString("name", workspace.MSBuild.Name);
            writer.WriteString("discoveryType", workspace.MSBuild.DiscoveryType);
            writer.WriteString("path", workspace.MSBuild.MSBuildPath);
            writer.WriteEndObject();
            writer.WriteStartObject("buildEnvironment");
            foreach (var pair in workspace.BuildEnvironment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
            writer.WriteString("processArchitecture", workspace.ProcessArchitecture);
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
}
