using System.IO.Pipes;

namespace RoslynKit;

/// <summary>
/// Creates asynchronous local named-pipe streams with server access restricted to the current user.
/// </summary>
internal static class DaemonNamedPipe
{
    public static NamedPipeServerStream CreateServer(string endpointName)
    {
        ValidateEndpointName(endpointName);
        return new NamedPipeServerStream(
            endpointName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public static NamedPipeClientStream CreateClient(string endpointName)
    {
        ValidateEndpointName(endpointName);
        return new NamedPipeClientStream(
            serverName: ".",
            endpointName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    private static void ValidateEndpointName(string endpointName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        if (endpointName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException("Daemon endpoint names cannot contain directory separators.", nameof(endpointName));
        }
    }
}
