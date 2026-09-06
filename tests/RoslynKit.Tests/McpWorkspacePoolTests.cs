namespace RoslynKit.Tests;

/// <summary>
/// Verifies bounded workspace admission, activity protection, scope reuse, and SDK-selection invalidation.
/// </summary>
public sealed class McpWorkspacePoolTests
{
    [Fact]
    public async Task ExecuteAsync_ReusesScopeAndEvictsLeastRecentlyUsedIdleWorker()
    {
        var workers = new List<TestWorker>();
        await using var pool = new McpWorkspacePool(2, (_, _) =>
        {
            var worker = new TestWorker();
            workers.Add(worker);
            return Task.FromResult<IRepositoryWorker>(worker);
        });
        await pool.ExecuteAsync(Query("first"), TestContext.Current.CancellationToken);
        await pool.ExecuteAsync(Query("second"), TestContext.Current.CancellationToken);
        await pool.ExecuteAsync(Query("first"), TestContext.Current.CancellationToken);
        await pool.ExecuteAsync(Query("third"), TestContext.Current.CancellationToken);

        Assert.Equal(3, workers.Count);
        Assert.False(workers[0].Disposed);
        Assert.True(workers[1].Disposed);
        Assert.False(workers[2].Disposed);
        Assert.Equal(2, workers[0].Calls);
    }

    [Fact]
    public async Task ExecuteAsync_WaitsForActiveRequestAndAllowsQueuedCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TestWorker(async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return CliProcessResult.Success("first");
        });
        var starts = 0;
        await using var pool = new McpWorkspacePool(1, (_, _) => Task.FromResult<IRepositoryWorker>(++starts == 1 ? first : new TestWorker()));
        var active = pool.ExecuteAsync(Query("first"), TestContext.Current.CancellationToken);
        await entered.Task;
        using var waitingCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var canceled = pool.ExecuteAsync(Query("second"), waitingCancellation.Token);
        await waitingCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(1, starts);
        Assert.False(first.Disposed);

        var waiting = pool.ExecuteAsync(Query("second"), TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        release.SetResult();
        await Task.WhenAll(active, waiting).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(first.Disposed);
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotEvictWorkerWhileBackgroundIndexIsBusy()
    {
        var worker = new TestWorker { Idle = false };
        var starts = 0;
        await using var pool = new McpWorkspacePool(1, (_, _) => Task.FromResult<IRepositoryWorker>(++starts == 1 ? worker : new TestWorker()));
        await pool.ExecuteAsync(Query("first"), TestContext.Current.CancellationToken);
        var waiting = pool.ExecuteAsync(Query("second"), TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        Assert.False(worker.Disposed);
        Assert.Equal(1, starts);
        worker.Idle = true;
        await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(worker.Disposed);
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task ExecuteAsync_RecreatesWorkerWhenGlobalJsonChanges()
    {
        await using var area = await RepositoryProcessTestArea.CreateAsync(TestContext.Current.CancellationToken);
        var workers = new List<TestWorker>();
        await using var pool = new McpWorkspacePool(2, (_, _) =>
        {
            var worker = new TestWorker();
            workers.Add(worker);
            return Task.FromResult<IRepositoryWorker>(worker);
        });
        var query = new McpQuery(area.RootPath, null, true, ["workspace"], "workspace");
        await pool.ExecuteAsync(query, TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(Path.Combine(area.RootPath, "global.json"), "\n", TestContext.Current.CancellationToken);
        await pool.ExecuteAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal(2, workers.Count);
        Assert.True(workers[0].Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_RetiresFailedWorkerAndRecoversOnNextRequest()
    {
        var first = new TestWorker();
        var starts = 0;
        await using var pool = new McpWorkspacePool(1, (_, _) => Task.FromResult<IRepositoryWorker>(++starts == 1 ? first : new TestWorker()));
        await pool.ExecuteAsync(Query("first"), TestContext.Current.CancellationToken);
        first.IsAlive = false;
        await pool.ExecuteAsync(Query("first"), TestContext.Current.CancellationToken);
        Assert.True(first.Disposed);
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task ExecuteAsync_CompletesCommittedRetirementBeforeHonoringQueuedCancellation()
    {
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TestWorker(retire: async token =>
        {
            committed.SetResult();
            await acknowledge.Task.WaitAsync(token);
            return true;
        });
        var starts = 0;
        await using var pool = new McpWorkspacePool(1, (_, _) => Task.FromResult<IRepositoryWorker>(++starts == 1 ? first : new TestWorker()));
        await pool.ExecuteAsync(Query("first"), TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = pool.ExecuteAsync(Query("second"), cancellation.Token);
        await committed.Task;
        await cancellation.CancelAsync();
        acknowledge.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(first.Disposed);
        Assert.Equal(1, starts);
        await pool.ExecuteAsync(Query("first"), TestContext.Current.CancellationToken);
        Assert.Equal(2, starts);
    }

    private static McpQuery Query(string target) => new(TestPaths.RepositoryRoot(), target, true, ["workspace"], "workspace");

    /// <summary>
    /// Models request and background lifetimes independently for pool admission tests.
    /// </summary>
    private sealed class TestWorker(
        Func<CancellationToken, Task<CliProcessResult>>? execute = null,
        Func<CancellationToken, Task<bool>>? retire = null) : IRepositoryWorker
    {
        public bool Disposed { get; private set; }
        public bool IsAlive { get; set; } = true;
        public bool Idle { get; set; } = true;
        public int Calls { get; private set; }

        public Task<CliProcessResult> ExecuteAsync(string[] args, CancellationToken cancellationToken)
        {
            Calls++;
            return execute?.Invoke(cancellationToken) ?? Task.FromResult(CliProcessResult.Success("fixture"));
        }

        public Task<bool> TryRetireAsync(CancellationToken cancellationToken) => retire?.Invoke(cancellationToken) ?? Task.FromResult(Idle);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
