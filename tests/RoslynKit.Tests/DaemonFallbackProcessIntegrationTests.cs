using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace RoslynKit.Tests;

/// <summary>
/// Exercises standalone fallback when an external daemon connection fails mid-response.
/// </summary>
[Collection(DaemonProcessIntegrationCollection.Name)]
public sealed class DaemonFallbackProcessIntegrationTests
{
    private const string DaemonUnavailableWarning = "warning: daemon unavailable; executing standalone";
    private const string PartialFakeOutput = "fake daemon partial output";

    [Fact]
    public async Task WorkspaceCommand_WhenDaemonDisconnects_FallsBackToStandaloneWithoutPartialDaemonOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var area = await DaemonProcessTestArea.CreateAsync(cancellationToken);
        var workspaceResolution = await new GitWorkspaceIdentityResolver()
            .ResolveAsync(area.TargetPath, cancellationToken);
        var workspaceIdentity = Assert.IsType<GitWorkspaceIdentity>(workspaceResolution.Identity);
        var childBuildEnvironment = new Dictionary<string, string?>(workspaceIdentity.BuildEnvironment, StringComparer.Ordinal)
        {
            ["DOTNET_CLI_HOME"] = Path.Combine(area.RootPath, ".dotnet-cli"),
        };
        var childWorkspaceIdentity = workspaceIdentity with { BuildEnvironment = childBuildEnvironment };
        var endpointName = DaemonEndpointName.Create(new DaemonIdentityResolver().Resolve(childWorkspaceIdentity));
        using var daemonLifetimeLease = DaemonLifetimeLease.TryAcquire(endpointName);
        Assert.NotNull(daemonLifetimeLease);

        using var fakeServerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var firstListenerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeServerTask = ServeDisconnectingEndpointAsync(
            endpointName,
            firstListenerReady,
            fakeServerCancellation.Token);
        await firstListenerReady.Task.WaitAsync(cancellationToken);

        ProcessResult result;
        try
        {
            result = await area.RunCliAsync(
                ["workspace", "--target", area.TargetPath],
                cancellationToken);
        }
        finally
        {
            fakeServerCancellation.Cancel();
            await fakeServerTask;
        }

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("command: workspace", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(DaemonUnavailableWarning + Environment.NewLine, result.StandardError);
        Assert.DoesNotContain(PartialFakeOutput, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(PartialFakeOutput, result.StandardError, StringComparison.Ordinal);
    }

    private static async Task ServeDisconnectingEndpointAsync(
        string endpointName,
        TaskCompletionSource firstListenerReady,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await using var server = DaemonNamedPipe.CreateServer(endpointName);
                firstListenerReady.TrySetResult();
                try
                {
                    await server.WaitForConnectionAsync(cancellationToken);
                    var handshake = Assert.IsType<DaemonHandshakeRequest>(
                        await DaemonProtocol.ReadRequestAsync(server, cancellationToken));
                    await DaemonProtocol.WriteResponseAsync(
                        server,
                        new DaemonHandshakeResponse(
                            RoslynKitBuildInfo.DaemonProtocolVersion,
                            handshake.RequestId,
                        Accepted: true,
                        Diagnostic: null),
                    cancellationToken);

                    try
                    {
                        var operation = await DaemonProtocol.ReadRequestAsync(server, cancellationToken);
                        Assert.IsType<DaemonCommandRequest>(operation);
                        await WritePartialResponseAsync(server, cancellationToken);
                        if (server.IsConnected)
                        {
                            server.Disconnect();
                        }
                    }
                    catch (DaemonProtocolException exception) when (exception.Error is DaemonProtocolError.EndOfStream
                        or DaemonProtocolError.UnexpectedEndOfStream)
                    {
                        // Readiness probes close after their successful handshake.
                    }
                }
                catch (IOException exception) when (exception is not DaemonProtocolException)
                {
                    // A client can time out while completing the short readiness handshake.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            firstListenerReady.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            firstListenerReady.TrySetException(exception);
            throw;
        }
    }

    private static async Task WritePartialResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(PartialFakeOutput);
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length + 1);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
