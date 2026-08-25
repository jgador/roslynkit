namespace RoslynKit.Tests;

public sealed class WorkspaceDaemonHostTests
{
    private static readonly DateTimeOffset InitialUtcNow =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StatusRequest_DoesNotResetFiveMinuteIdleTimeout()
    {
        var delays = new ControlledDelay();
        var disposed = 0;
        await using var host = CreateHost(
            delays,
            (_, _) => Task.FromResult(Successful()),
            () => new WorkspaceDaemonSessionSnapshot(
                WorkspaceDaemonSessionState.Ready,
                Generation: 4,
                ActiveRequests: 2,
                QueuedRequests: 1,
                LastInfrastructureDiagnostic: "diagnostic"),
            () =>
            {
                Interlocked.Increment(ref disposed);
                return ValueTask.CompletedTask;
            });
        var originalIdleDelay = delays.GetPending(TimeSpan.FromMinutes(5));

        var response = host.CreateStatusResponse(
            new DaemonStatusRequest(Guid.NewGuid()));

        Assert.True(response.Running);
        Assert.Equal(TestPaths.SolutionPath(), response.TargetPath);
        Assert.Equal(42, response.ProcessId);
        Assert.Equal("ready", response.WorkspaceState);
        Assert.Equal(4, response.Generation);
        Assert.Equal(2, response.ActiveRequests);
        Assert.Equal(1, response.QueuedRequests);
        Assert.Equal("diagnostic", response.Diagnostic);
        Assert.Same(originalIdleDelay, delays.GetPending(TimeSpan.FromMinutes(5)));

        originalIdleDelay.Release();
        var reason = await host.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceDaemonStopReason.IdleTimeout, reason);
        Assert.Equal(1, Volatile.Read(ref disposed));
    }

    [Fact]
    public async Task ExecuteAsync_ClientDisconnectCancelsRunningRequest()
    {
        var delays = new ControlledDelay();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = CreateHost(
            delays,
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Successful();
            });
        using var clientDisconnected = new CancellationTokenSource();
        var request = CreateRequest(Guid.NewGuid());

        var execution = host.ExecuteAsync(request, clientDisconnected.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        clientDisconnected.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal(1, delays.PendingCount(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task ExecuteAsync_ExpiredDeadlineCancelsBeforeExecutionCompletes()
    {
        var delays = new ControlledDelay();
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = CreateHost(
            delays,
            async (_, cancellationToken) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }

                return Successful();
            });
        var request = CreateRequest(Guid.NewGuid(), InitialUtcNow - TimeSpan.FromSeconds(1));

        var execution = host.ExecuteAsync(request, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopRequest_RejectsNewWorkAndDrainsActiveRequest()
    {
        var delays = new ControlledDelay();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = 0;
        await using var host = CreateHost(
            delays,
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return Successful();
            },
            disposeAsync: () =>
            {
                Interlocked.Increment(ref disposed);
                return ValueTask.CompletedTask;
            });
        var execution = host.ExecuteAsync(CreateRequest(Guid.NewGuid()), CancellationToken.None);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        var response = host.BeginStop(
            new DaemonStopRequest(Guid.NewGuid()));

        Assert.True(response.Stopping);
        Assert.Equal(WorkspaceDaemonHostState.Stopping, host.CaptureStatus().State);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.ExecuteAsync(CreateRequest(Guid.NewGuid()), CancellationToken.None));
        Assert.False(host.Completion.IsCompleted);
        Assert.Equal(1, delays.PendingCount(TimeSpan.FromSeconds(30)));

        release.TrySetResult();
        Assert.True((await execution).IsSuccessful);
        var reason = await host.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceDaemonStopReason.StopRequested, reason);
        Assert.Equal(WorkspaceDaemonHostState.Stopped, host.CaptureStatus().State);
        Assert.Equal(1, Volatile.Read(ref disposed));
    }

    [Fact]
    public async Task StopRequest_CancelsRemainingRequestAfterThirtySeconds()
    {
        var delays = new ControlledDelay();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellationUnwind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = 0;
        await using var host = CreateHost(
            delays,
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return Successful();
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    await releaseCancellationUnwind.Task;
                    throw;
                }
            },
            disposeAsync: () =>
            {
                Interlocked.Increment(ref disposed);
                return ValueTask.CompletedTask;
            });
        var execution = host.ExecuteAsync(CreateRequest(Guid.NewGuid()), CancellationToken.None);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        _ = host.BeginStop(
            new DaemonStopRequest(Guid.NewGuid()));

        delays.GetPending(TimeSpan.FromSeconds(30)).Release();
        await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(host.Completion.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref disposed));
        releaseCancellationUnwind.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal(
            WorkspaceDaemonStopReason.StopRequested,
            await host.Completion.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref disposed));
    }

    [Fact]
    public async Task ExecuteAsync_FutureDeadlineCancelsWhenItsTimerExpires()
    {
        var delays = new ControlledDelay();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = CreateHost(
            delays,
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Successful();
            });
        var execution = host.ExecuteAsync(CreateRequest(Guid.NewGuid()), CancellationToken.None);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var deadline = delays.GetPending(TimeSpan.FromHours(1));

        Assert.False(execution.IsCompleted);
        deadline.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }

    [Fact]
    public async Task StopAndDisposeRace_DisposesSessionOnce()
    {
        var delays = new ControlledDelay();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellationUnwind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = 0;
        var host = CreateHost(
            delays,
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return Successful();
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    await releaseCancellationUnwind.Task;
                    throw;
                }
            },
            disposeAsync: () =>
            {
                Interlocked.Increment(ref disposed);
                return ValueTask.CompletedTask;
            });
        var execution = host.ExecuteAsync(CreateRequest(Guid.NewGuid()), CancellationToken.None);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        _ = host.BeginStop(
            new DaemonStopRequest(Guid.NewGuid()));

        var dispose = host.DisposeAsync().AsTask();
        await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(dispose.IsCompleted);
        releaseCancellationUnwind.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await dispose.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkspaceDaemonStopReason.StopRequested, await host.Completion);
        Assert.Equal(1, Volatile.Read(ref disposed));
    }

    [Fact]
    public async Task StopRequest_WithNoActiveWorkCompletesWithoutGraceDelay()
    {
        var delays = new ControlledDelay();
        var disposed = 0;
        await using var host = CreateHost(
            delays,
            (_, _) => Task.FromResult(Successful()),
            disposeAsync: () =>
            {
                Interlocked.Increment(ref disposed);
                return ValueTask.CompletedTask;
            });

        _ = host.BeginStop(
            new DaemonStopRequest(Guid.NewGuid()));

        Assert.Equal(
            WorkspaceDaemonStopReason.StopRequested,
            await host.Completion.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, delays.PendingCount(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, Volatile.Read(ref disposed));
    }

    [Fact]
    public async Task StopRequest_DisposesSessionWhenCancellationCallbackThrows()
    {
        var delays = new ControlledDelay();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;
        var disposed = 0;
        var host = CreateHost(
            delays,
            async (_, cancellationToken) =>
            {
                var execution = Interlocked.Increment(ref executionCount);
                if (execution == 1)
                {
                    using var registration = cancellationToken.Register(
                        static () => throw new InvalidOperationException("callback failure"));
                    if (Volatile.Read(ref executionCount) == 2)
                    {
                        bothStarted.TrySetResult();
                    }

                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                else
                {
                    bothStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        secondCancellationObserved.TrySetResult();
                        throw;
                    }
                }

                return Successful();
            },
            disposeAsync: () =>
            {
                Interlocked.Increment(ref disposed);
                return ValueTask.CompletedTask;
            });
        var first = host.ExecuteAsync(CreateRequest(Guid.NewGuid()), CancellationToken.None);
        var second = host.ExecuteAsync(CreateRequest(Guid.NewGuid()), CancellationToken.None);
        await bothStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        _ = host.BeginStop(
            new DaemonStopRequest(Guid.NewGuid()));

        delays.GetPending(TimeSpan.FromSeconds(30)).Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await secondCancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => host.Completion.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Contains(
            exception.InnerExceptions,
            static inner => inner is InvalidOperationException { Message: "callback failure" });
        Assert.Equal(1, Volatile.Read(ref disposed));
    }

    [Fact]
    public async Task ExecuteAsync_RejectsDuplicateActiveRequestId()
    {
        var delays = new ControlledDelay();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = CreateHost(
            delays,
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return Successful();
            });
        var requestId = Guid.NewGuid();
        var request = CreateRequest(requestId);
        var first = host.ExecuteAsync(request, CancellationToken.None);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<DaemonProtocolException>(
            () => host.ExecuteAsync(request, CancellationToken.None));

        Assert.Equal(DaemonProtocolError.InvalidMessage, exception.Error);
        release.TrySetResult();
        Assert.True((await first).IsSuccessful);
    }

    [Fact]
    public async Task DisposeAsync_CancelsActiveRequestWithoutGraceDelay()
    {
        var delays = new ControlledDelay();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = CreateHost(
            delays,
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Successful();
            });
        var execution = host.ExecuteAsync(CreateRequest(Guid.NewGuid()), CancellationToken.None);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        var dispose = host.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await dispose.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkspaceDaemonHostState.Stopped, host.CaptureStatus().State);
    }

    private static WorkspaceDaemonHost CreateHost(
        ControlledDelay delays,
        Func<ParsedCommand, CancellationToken, Task<WorkspaceDaemonSessionResult>> executeAsync,
        Func<WorkspaceDaemonSessionSnapshot>? captureSnapshot = null,
        Func<ValueTask>? disposeAsync = null)
    {
        return new WorkspaceDaemonHost(
            TestPaths.SolutionPath(),
            processId: 42,
            executeAsync,
            captureSnapshot ?? (static () => new WorkspaceDaemonSessionSnapshot(
                WorkspaceDaemonSessionState.Ready,
                Generation: 1,
                ActiveRequests: 0,
                QueuedRequests: 0,
                LastInfrastructureDiagnostic: null)),
            disposeAsync ?? (static () => ValueTask.CompletedTask),
            new FixedTimeProvider(InitialUtcNow),
            delays.DelayAsync,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(30));
    }

    private static DaemonCommandRequest CreateRequest(Guid requestId, DateTimeOffset? deadlineUtc = null)
    {
        var command = CliParser.Parse(["workspace", "--target", TestPaths.SolutionPath()]);
        return DaemonCommandRequest.Create(
            command,
            requestId,
            deadlineUtc ?? InitialUtcNow + TimeSpan.FromHours(1));
    }

    private static WorkspaceDaemonSessionResult Successful()
    {
        return WorkspaceDaemonSessionResult.Successful(CliProcessResult.Success("workspace"), generation: 1);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ControlledDelay
    {
        private readonly object _gate = new();
        private readonly List<DelayEntry> _entries = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var entry = new DelayEntry(delay, cancellationToken);
            lock (_gate)
            {
                _entries.Add(entry);
            }

            return entry.Task;
        }

        public int PendingCount(TimeSpan delay)
        {
            lock (_gate)
            {
                return _entries.Count(entry => entry.Delay == delay && !entry.Task.IsCompleted);
            }
        }

        public DelayEntry GetPending(TimeSpan delay)
        {
            lock (_gate)
            {
                return _entries.FirstOrDefault(candidate => candidate.Delay == delay && !candidate.Task.IsCompleted)
                    ?? throw new InvalidOperationException($"No pending delay exists for {delay}.");
            }
        }

        public sealed class DelayEntry
        {
            private readonly CancellationTokenRegistration _registration;
            private readonly TaskCompletionSource _source =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public DelayEntry(TimeSpan delay, CancellationToken cancellationToken)
            {
                Delay = delay;
                _registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                    _source);
            }

            public TimeSpan Delay { get; }

            public Task Task => _source.Task;

            public void Release()
            {
                _registration.Dispose();
                _source.TrySetResult();
            }
        }
    }
}
