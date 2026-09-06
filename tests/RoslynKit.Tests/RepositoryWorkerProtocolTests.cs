using System.Text;
using System.Text.Json;

namespace RoslynKit.Tests;

/// <summary>
/// Verifies private message correlation, concurrent requests, cancellation, and end-of-input cleanup.
/// </summary>
public sealed class RepositoryWorkerProtocolTests
{
    [Fact]
    public async Task Protocol_ExecutesConcurrentRequestsAndAcknowledgesCancellationAfterCleanup()
    {
        var (client, server) = McpMemoryStream.CreatePair();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = false;
        var protocol = RepositoryWorkerProtocol.RunAsync(server, server, (args, token) =>
        {
            if (args[0] == "slow")
            {
                entered.TrySetResult();
                try
                {
                    token.WaitHandle.WaitOne();
                    token.ThrowIfCancellationRequested();
                }
                finally
                {
                    cleaned = true;
                }
            }

            return Task.FromResult(CliProcessResult.Success(args[0]));
        }, TestContext.Current.CancellationToken);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new RepositoryWorkerMessage(1, "query", ["slow"])));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new RepositoryWorkerMessage(2, "query", ["fast"])));
        var fast = JsonSerializer.Deserialize<RepositoryWorkerReply>((await reader.ReadLineAsync(TestContext.Current.CancellationToken))!);
        Assert.NotNull(fast);
        Assert.Equal(2, fast.Id);
        Assert.Equal(0, fast.Result.ExitCode);

        await writer.WriteLineAsync(JsonSerializer.Serialize(new RepositoryWorkerMessage(1, "cancel")));
        var canceled = JsonSerializer.Deserialize<RepositoryWorkerReply>((await reader.ReadLineAsync(TestContext.Current.CancellationToken))!);
        Assert.NotNull(canceled);
        Assert.Equal(1, canceled.Id);
        Assert.Equal(130, canceled.Result.ExitCode);
        Assert.True(cleaned);
        await client.DisposeAsync();
        await protocol.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Protocol_EndOfInputCancelsAndAwaitsInFlightRequest()
    {
        var (client, server) = McpMemoryStream.CreatePair();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = false;
        var protocol = RepositoryWorkerProtocol.RunAsync(server, server, async (_, token) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return CliProcessResult.Success("unexpected");
            }
            finally
            {
                cleaned = true;
            }
        }, TestContext.Current.CancellationToken);
        var message = JsonSerializer.Serialize(new RepositoryWorkerMessage(1, "query", ["wait"])) + "\n";
        await client.WriteAsync(Encoding.UTF8.GetBytes(message), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await client.DisposeAsync();
        await protocol.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(cleaned);
    }
}
