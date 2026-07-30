namespace RoslynKit.Tests;

public sealed class WorkspaceDaemonSessionTests
{
    private const string TargetPath = "workspace.slnx";

    [Fact]
    public async Task ExecuteAsync_LoadsInitialStableGeneration()
    {
        var fingerprint = CleanFingerprint("head-a");
        var owner = new TrackingDisposable();
        var executions = 0;
        var generation = CreateGeneration(owner, "initial", () => executions++);
        var loads = 0;
        await using var session = CreateSession(
            Capture(Successful(fingerprint), Successful(fingerprint)),
            _ =>
            {
                loads++;
                return Task.FromResult(generation);
            });

        var result = await ExecuteAsync(session);

        Assert.True(result.IsSuccessful, result.Diagnostic);
        Assert.Equal(1, result.Generation);
        Assert.Equal("initial", result.ProcessResult!.Stdout);
        Assert.Equal(1, loads);
        Assert.Equal(1, executions);
        Assert.Equal(1, session.Generation);
        Assert.Equal(fingerprint, session.SuccessfulFingerprint);
        Assert.Null(session.LastInfrastructureDiagnostic);
        Assert.Equal(0, session.ActiveRequests);
        Assert.Equal(0, session.QueuedRequests);
        Assert.Equal(0, owner.DisposeCount);
    }

    [Fact]
    public async Task ExecuteAsync_ReusesUnchangedGeneration()
    {
        var fingerprint = CleanFingerprint("head-a");
        var owner = new TrackingDisposable();
        var executions = 0;
        var generation = CreateGeneration(owner, "reused", () => executions++);
        var loads = 0;
        await using var session = CreateSession(
            _ => Task.FromResult(Successful(fingerprint)),
            _ =>
            {
                loads++;
                return Task.FromResult(generation);
            });

        var first = await ExecuteAsync(session);
        var second = await ExecuteAsync(session);

        Assert.True(first.IsSuccessful, first.Diagnostic);
        Assert.True(second.IsSuccessful, second.Diagnostic);
        Assert.Equal(1, first.Generation);
        Assert.Equal(1, second.Generation);
        Assert.Equal(1, loads);
        Assert.Equal(2, executions);
        Assert.Equal(0, owner.DisposeCount);
    }

    [Fact]
    public async Task DirtyFingerprint_ReloadsAndDisposesPreviousGeneration()
    {
        var initialFingerprint = CleanFingerprint("head-a");
        var dirtyFingerprint = DirtyFingerprint("head-a", "src/App.cs", "blob-b");
        var initialOwner = new TrackingDisposable();
        var reloadedOwner = new TrackingDisposable();
        var initialGeneration = CreateGeneration(initialOwner, "initial");
        var reloadedGeneration = CreateGeneration(reloadedOwner, "reloaded");
        var generations = new Queue<WorkspaceDaemonGeneration>([initialGeneration, reloadedGeneration]);
        await using var session = CreateSession(
            Capture(
                Successful(initialFingerprint),
                Successful(initialFingerprint),
                Successful(dirtyFingerprint),
                Successful(dirtyFingerprint)),
            _ => Task.FromResult(generations.Dequeue()));

        var first = await ExecuteAsync(session);
        var second = await ExecuteAsync(session);

        Assert.True(first.IsSuccessful, first.Diagnostic);
        Assert.True(second.IsSuccessful, second.Diagnostic);
        Assert.Equal(2, second.Generation);
        Assert.Equal(2, session.Generation);
        Assert.Equal(dirtyFingerprint, session.SuccessfulFingerprint);
        Assert.Equal(1, initialOwner.DisposeCount);
        Assert.Equal(0, reloadedOwner.DisposeCount);
    }

    [Fact]
    public async Task FingerprintFailure_SkipsCachedGeneration()
    {
        var fingerprint = CleanFingerprint("head-a");
        const string diagnostic = "git status was unavailable";
        var owner = new TrackingDisposable();
        var executions = 0;
        var generation = CreateGeneration(owner, "initial", () => executions++);
        await using var session = CreateSession(
            Capture(
                Successful(fingerprint),
                Successful(fingerprint),
                Failed(GitWorktreeFingerprintFailureKind.GitFailure, diagnostic),
                Successful(fingerprint)),
            _ => Task.FromResult(generation));

        var initial = await ExecuteAsync(session);
        var failure = await ExecuteAsync(session);

        Assert.True(initial.IsSuccessful, initial.Diagnostic);
        Assert.False(failure.IsSuccessful);
        Assert.Equal(GitWorktreeFingerprintFailureKind.GitFailure, failure.InfrastructureFailureKind);
        Assert.Equal(diagnostic, failure.Diagnostic);
        Assert.Null(failure.ProcessResult);
        Assert.Null(failure.Generation);
        Assert.Equal(1, session.Generation);
        Assert.Equal(fingerprint, session.SuccessfulFingerprint);
        Assert.Equal(diagnostic, session.LastInfrastructureDiagnostic);
        Assert.Equal(1, executions);
        Assert.Equal(0, owner.DisposeCount);

        var recovered = await ExecuteAsync(session);

        Assert.True(recovered.IsSuccessful, recovered.Diagnostic);
        Assert.Equal(1, recovered.Generation);
        Assert.Equal(2, executions);
        Assert.Null(session.LastInfrastructureDiagnostic);
    }

    [Fact]
    public async Task LoadFailure_LeavesNoGenerationAndRecovers()
    {
        var fingerprint = CleanFingerprint("head-a");
        var recoveryOwner = new TrackingDisposable();
        var recoveryGeneration = CreateGeneration(recoveryOwner, "recovered");
        var loads = 0;
        await using var session = CreateSession(
            Capture(
                Successful(fingerprint),
                Successful(fingerprint),
                Successful(fingerprint)),
            _ =>
            {
                loads++;
                return loads == 1
                    ? Task.FromException<WorkspaceDaemonGeneration>(new InvalidOperationException("workspace load failed"))
                    : Task.FromResult(recoveryGeneration);
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(session));

        Assert.Contains("workspace load failed", exception.Message, StringComparison.Ordinal);
        Assert.Null(session.Generation);
        Assert.Null(session.SuccessfulFingerprint);
        Assert.Null(session.LastInfrastructureDiagnostic);

        var recovery = await ExecuteAsync(session);

        Assert.True(recovery.IsSuccessful, recovery.Diagnostic);
        Assert.Equal(1, recovery.Generation);
        Assert.Equal(1, session.Generation);
        Assert.Equal(fingerprint, session.SuccessfulFingerprint);
        Assert.Null(session.LastInfrastructureDiagnostic);
        Assert.Equal(2, loads);
        Assert.Equal(0, recoveryOwner.DisposeCount);
    }

    [Fact]
    public async Task ExecuteAsync_FirstPrePostMismatch_DelaysAndRetriesOnce()
    {
        var firstFingerprint = CleanFingerprint("head-a");
        var secondFingerprint = CleanFingerprint("head-b");
        var firstOwner = new TrackingDisposable();
        var retryOwner = new TrackingDisposable();
        var firstExecutions = 0;
        var retryExecutions = 0;
        var firstGeneration = CreateGeneration(firstOwner, "first", () => firstExecutions++);
        var retryGeneration = CreateGeneration(retryOwner, "retry", () => retryExecutions++);
        var generations = new Queue<WorkspaceDaemonGeneration>([firstGeneration, retryGeneration]);
        var delays = new List<TimeSpan>();
        await using var session = CreateSession(
            Capture(
                Successful(firstFingerprint),
                Successful(secondFingerprint),
                Successful(secondFingerprint),
                Successful(secondFingerprint)),
            _ => Task.FromResult(generations.Dequeue()),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await ExecuteAsync(session);

        Assert.True(result.IsSuccessful, result.Diagnostic);
        Assert.Equal(1, result.Generation);
        Assert.Equal(1, session.Generation);
        Assert.Equal(secondFingerprint, session.SuccessfulFingerprint);
        Assert.Equal([TimeSpan.FromMilliseconds(250)], delays);
        Assert.Equal(1, firstOwner.DisposeCount);
        Assert.Equal(0, retryOwner.DisposeCount);
        Assert.Equal(0, firstExecutions);
        Assert.Equal(1, retryExecutions);
    }

    [Fact]
    public async Task QuietPeriodChange_RestartsDelay()
    {
        var firstFingerprint = CleanFingerprint("head-a");
        var secondFingerprint = CleanFingerprint("head-b");
        var quietFingerprint = CleanFingerprint("head-c");
        var firstOwner = new TrackingDisposable();
        var retryOwner = new TrackingDisposable();
        var generations = new Queue<WorkspaceDaemonGeneration>([
            CreateGeneration(firstOwner, "first"),
            CreateGeneration(retryOwner, "retry"),
        ]);
        var delays = new List<TimeSpan>();
        await using var session = CreateSession(
            Capture(
                Successful(firstFingerprint),
                Successful(secondFingerprint),
                Successful(quietFingerprint),
                Successful(quietFingerprint),
                Successful(quietFingerprint)),
            _ => Task.FromResult(generations.Dequeue()),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await ExecuteAsync(session);

        Assert.True(result.IsSuccessful, result.Diagnostic);
        Assert.Equal(quietFingerprint, session.SuccessfulFingerprint);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250)],
            delays);
        Assert.Equal(1, firstOwner.DisposeCount);
        Assert.Equal(0, retryOwner.DisposeCount);
    }

    [Fact]
    public async Task SecondLoadMismatch_ServesRetryAndForcesReload()
    {
        var firstFingerprint = CleanFingerprint("head-a");
        var secondFingerprint = CleanFingerprint("head-b");
        var thirdFingerprint = CleanFingerprint("head-c");
        var firstOwner = new TrackingDisposable();
        var retryOwner = new TrackingDisposable();
        var reloadedOwner = new TrackingDisposable();
        var firstGeneration = CreateGeneration(firstOwner, "first");
        var retryGeneration = CreateGeneration(retryOwner, "retry");
        var reloadedGeneration = CreateGeneration(reloadedOwner, "reloaded");
        var generations = new Queue<WorkspaceDaemonGeneration>([
            firstGeneration,
            retryGeneration,
            reloadedGeneration,
        ]);
        var delays = new List<TimeSpan>();
        await using var session = CreateSession(
            Capture(
                Successful(firstFingerprint),
                Successful(secondFingerprint),
                Successful(secondFingerprint),
                Successful(thirdFingerprint),
                Successful(thirdFingerprint),
                Successful(thirdFingerprint)),
            _ => Task.FromResult(generations.Dequeue()),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var unstable = await ExecuteAsync(session);

        Assert.True(unstable.IsSuccessful, unstable.Diagnostic);
        Assert.Equal(1, unstable.Generation);
        Assert.Equal(1, session.Generation);
        Assert.Null(session.SuccessfulFingerprint);
        Assert.Equal([TimeSpan.FromMilliseconds(250)], delays);
        Assert.Equal(1, firstOwner.DisposeCount);
        Assert.Equal(0, retryOwner.DisposeCount);
        Assert.Equal(0, reloadedOwner.DisposeCount);

        var reloaded = await ExecuteAsync(session);

        Assert.True(reloaded.IsSuccessful, reloaded.Diagnostic);
        Assert.Equal(2, reloaded.Generation);
        Assert.Equal(2, session.Generation);
        Assert.Equal(thirdFingerprint, session.SuccessfulFingerprint);
        Assert.Equal(1, retryOwner.DisposeCount);
        Assert.Equal(0, reloadedOwner.DisposeCount);
    }

    [Fact]
    public async Task ExecuteAsync_DirtyReloadFailure_ClearsPreviousGeneration()
    {
        var initialFingerprint = CleanFingerprint("head-a");
        var dirtyFingerprint = DirtyFingerprint("head-a", "src/App.cs", "blob-b");
        var owner = new TrackingDisposable();
        var generation = CreateGeneration(owner, "initial");
        var loads = 0;
        await using var session = CreateSession(
            Capture(
                Successful(initialFingerprint),
                Successful(initialFingerprint),
                Successful(dirtyFingerprint)),
            _ =>
            {
                loads++;
                return loads == 1
                    ? Task.FromResult(generation)
                    : Task.FromException<WorkspaceDaemonGeneration>(new InvalidOperationException("dirty reload failed"));
            });

        var initial = await ExecuteAsync(session);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(session));

        Assert.True(initial.IsSuccessful, initial.Diagnostic);
        Assert.Contains("dirty reload failed", exception.Message, StringComparison.Ordinal);
        Assert.Null(session.Generation);
        Assert.Null(session.SuccessfulFingerprint);
        Assert.Null(session.LastInfrastructureDiagnostic);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task PostLoadFingerprintFailure_DiscardsBothGenerations()
    {
        var initialFingerprint = CleanFingerprint("head-a");
        var dirtyFingerprint = DirtyFingerprint("head-a", "src/App.cs", "blob-b");
        var initialOwner = new TrackingDisposable();
        var candidateOwner = new TrackingDisposable();
        var candidateExecutions = 0;
        var initialGeneration = CreateGeneration(initialOwner, "initial");
        var candidateGeneration = CreateGeneration(candidateOwner, "candidate", () => candidateExecutions++);
        var generations = new Queue<WorkspaceDaemonGeneration>([initialGeneration, candidateGeneration]);
        const string diagnostic = "post-load fingerprint failed";
        await using var session = CreateSession(
            Capture(
                Successful(initialFingerprint),
                Successful(initialFingerprint),
                Successful(dirtyFingerprint),
                Failed(GitWorktreeFingerprintFailureKind.GitFailure, diagnostic)),
            _ => Task.FromResult(generations.Dequeue()));

        var initial = await ExecuteAsync(session);
        var failure = await ExecuteAsync(session);

        Assert.True(initial.IsSuccessful, initial.Diagnostic);
        Assert.False(failure.IsSuccessful);
        Assert.Equal(GitWorktreeFingerprintFailureKind.GitFailure, failure.InfrastructureFailureKind);
        Assert.Equal(diagnostic, failure.Diagnostic);
        Assert.Equal(1, initialOwner.DisposeCount);
        Assert.Equal(1, candidateOwner.DisposeCount);
        Assert.Equal(0, candidateExecutions);
        Assert.Null(session.Generation);
        Assert.Null(session.SuccessfulFingerprint);
    }

    [Fact]
    public async Task ExecuteAsync_LimitsConcurrentCleanRequestsToThree()
    {
        var fingerprint = CleanFingerprint("head-a");
        var owner = new TrackingDisposable();
        var firstWaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourthStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFourth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;
        var firstWaveCount = 0;
        var generation = new WorkspaceDaemonGeneration(
            owner,
            async (_, cancellationToken) =>
            {
                var execution = Interlocked.Increment(ref executionCount);
                if (execution == 1)
                {
                    return CliProcessResult.Success("initial");
                }

                if (Interlocked.Increment(ref firstWaveCount) <= 3)
                {
                    if (Volatile.Read(ref firstWaveCount) == 3)
                    {
                        firstWaveStarted.TrySetResult();
                    }

                    await releaseFirstWave.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    fourthStarted.TrySetResult();
                    await releaseFourth.Task.WaitAsync(cancellationToken);
                }

                return CliProcessResult.Success("concurrent");
            });
        await using var session = CreateSession(
            _ => Task.FromResult(Successful(fingerprint)),
            _ => Task.FromResult(generation));
        _ = await ExecuteAsync(session);

        var requests = Enumerable.Range(0, 4).Select(_ => ExecuteAsync(session)).ToArray();
        await firstWaveStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => session.ActiveRequests == 3 && session.QueuedRequests == 1,
            TestContext.Current.CancellationToken);

        Assert.False(fourthStarted.Task.IsCompleted);
        releaseFirstWave.SetResult();
        await fourthStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        releaseFourth.SetResult();
        var results = await Task.WhenAll(requests);

        Assert.All(results, static result => Assert.True(result.IsSuccessful, result.Diagnostic));
        Assert.Equal(0, session.ActiveRequests);
        Assert.Equal(0, session.QueuedRequests);
    }

    [Fact]
    public async Task DirtyReload_WaitsForReadersAndBlocksOldLeases()
    {
        var initialFingerprint = CleanFingerprint("head-a");
        var dirtyFingerprint = DirtyFingerprint("head-a", "src/App.cs", "blob-b");
        var currentFingerprint = initialFingerprint;
        var initialOwner = new TrackingDisposable();
        var reloadedOwner = new TrackingDisposable();
        var activeOldGenerationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldGeneration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldExecutions = 0;
        var initialGeneration = new WorkspaceDaemonGeneration(
            initialOwner,
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref oldExecutions) == 2)
                {
                    activeOldGenerationStarted.TrySetResult();
                    await releaseOldGeneration.Task.WaitAsync(cancellationToken);
                }

                return new CliProcessResult(0, "old", string.Empty);
            });
        var reloadedGeneration = CreateGeneration(reloadedOwner, "new");
        var generations = new Queue<WorkspaceDaemonGeneration>([initialGeneration, reloadedGeneration]);
        var loads = 0;
        await using var session = CreateSession(
            _ => Task.FromResult(Successful(Volatile.Read(ref currentFingerprint))),
            _ =>
            {
                loads++;
                return Task.FromResult(generations.Dequeue());
            });
        _ = await ExecuteAsync(session);

        var activeOldRequest = ExecuteAsync(session);
        await activeOldGenerationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Volatile.Write(ref currentFingerprint, dirtyFingerprint);
        var dirtyRequest = ExecuteAsync(session);
        await WaitUntilAsync(
            () => session.State == WorkspaceDaemonSessionState.Reloading && session.ActiveRequests == 1,
            TestContext.Current.CancellationToken);
        var laterRequest = ExecuteAsync(session);
        await WaitUntilAsync(
            () => session.QueuedRequests == 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, initialOwner.DisposeCount);
        Assert.Equal(1, loads);
        releaseOldGeneration.SetResult();
        var results = await Task.WhenAll(activeOldRequest, dirtyRequest, laterRequest);

        Assert.Equal("old", results[0].ProcessResult!.Stdout);
        Assert.Equal("new", results[1].ProcessResult!.Stdout);
        Assert.Equal("new", results[2].ProcessResult!.Stdout);
        Assert.Equal(2, oldExecutions);
        Assert.Equal(2, loads);
        Assert.Equal(1, initialOwner.DisposeCount);
        Assert.Equal(2, session.Generation);
    }

    [Fact]
    public async Task PendingDirtyReload_PrecedesWaitingCleanRequest()
    {
        var initialFingerprint = CleanFingerprint("head-a");
        var dirtyFingerprint = DirtyFingerprint("head-a", "src/App.cs", "blob-b");
        var currentFingerprint = initialFingerprint;
        var initialOwner = new TrackingDisposable();
        var reloadedOwner = new TrackingDisposable();
        var activeReadersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActiveReaders = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldExecutions = 0;
        var activeReaders = 0;
        var initialGeneration = new WorkspaceDaemonGeneration(
            initialOwner,
            async (_, cancellationToken) =>
            {
                var execution = Interlocked.Increment(ref oldExecutions);
                if (execution > 1)
                {
                    if (Interlocked.Increment(ref activeReaders) == 3)
                    {
                        activeReadersStarted.TrySetResult();
                    }

                    await releaseActiveReaders.Task.WaitAsync(cancellationToken);
                }

                return new CliProcessResult(0, "old", string.Empty);
            });
        var reloadedGeneration = CreateGeneration(reloadedOwner, "new");
        var generations = new Queue<WorkspaceDaemonGeneration>([initialGeneration, reloadedGeneration]);
        var loads = 0;
        await using var session = CreateSession(
            _ => Task.FromResult(Successful(Volatile.Read(ref currentFingerprint))),
            _ =>
            {
                loads++;
                return Task.FromResult(generations.Dequeue());
            });
        _ = await ExecuteAsync(session);
        var activeRequests = Enumerable.Range(0, 3).Select(_ => ExecuteAsync(session)).ToArray();
        await activeReadersStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var waitingCleanRequest = ExecuteAsync(session);
        await WaitUntilAsync(() => session.QueuedRequests == 1, TestContext.Current.CancellationToken);
        Volatile.Write(ref currentFingerprint, dirtyFingerprint);
        var dirtyRequest = ExecuteAsync(session);
        await WaitUntilAsync(
            () => session.State == WorkspaceDaemonSessionState.Reloading && session.QueuedRequests == 2,
            TestContext.Current.CancellationToken);

        releaseActiveReaders.SetResult();
        _ = await Task.WhenAll(activeRequests);
        var remainingResults = await Task.WhenAll(waitingCleanRequest, dirtyRequest);

        Assert.All(remainingResults, static result => Assert.Equal("new", result.ProcessResult!.Stdout));
        Assert.Equal(4, oldExecutions);
        Assert.Equal(2, loads);
        Assert.Equal(1, initialOwner.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_WaitsForActiveGenerationBeforeDisposal()
    {
        var fingerprint = CleanFingerprint("head-a");
        var owner = new TrackingDisposable();
        var activeExecutionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var generation = new WorkspaceDaemonGeneration(
            owner,
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref executions) == 2)
                {
                    activeExecutionStarted.TrySetResult();
                    await releaseExecution.Task.WaitAsync(cancellationToken);
                }

                return new CliProcessResult(0, "workspace", string.Empty);
            });
        var session = CreateSession(
            _ => Task.FromResult(Successful(fingerprint)),
            _ => Task.FromResult(generation));
        _ = await ExecuteAsync(session);
        var activeRequest = ExecuteAsync(session);
        await activeExecutionStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var dispose = session.DisposeAsync().AsTask();

        Assert.False(dispose.IsCompleted);
        Assert.Equal(0, owner.DisposeCount);
        releaseExecution.SetResult();
        _ = await activeRequest;
        await dispose;

        Assert.Equal(1, owner.DisposeCount);
        Assert.Equal(WorkspaceDaemonSessionState.Disposed, session.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ExecuteAsync(session));
    }

    [Fact]
    public async Task DisposeAsync_RejectsGenerationLoadedAfterDisposalRequested()
    {
        var initialFingerprint = CleanFingerprint("head-a");
        var dirtyFingerprint = DirtyFingerprint("head-a", "src/App.cs", "blob-b");
        var initialOwner = new TrackingDisposable();
        var candidateOwner = new BlockingDisposable();
        var initialGeneration = CreateGeneration(initialOwner, "initial");
        var candidateGeneration = CreateGeneration(candidateOwner, "candidate");
        var candidateLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCandidateLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        var session = CreateSession(
            Capture(
                Successful(initialFingerprint),
                Successful(initialFingerprint),
                Successful(dirtyFingerprint),
                Successful(dirtyFingerprint)),
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref loads) == 1)
                {
                    return initialGeneration;
                }

                candidateLoadStarted.TrySetResult();
                await releaseCandidateLoad.Task.WaitAsync(cancellationToken);
                return candidateGeneration;
            });
        _ = await ExecuteAsync(session);
        var dirtyRequest = ExecuteAsync(session);
        await candidateLoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var dispose = session.DisposeAsync().AsTask();

        Assert.False(dispose.IsCompleted);
        releaseCandidateLoad.SetResult();
        await candidateOwner.DisposeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(dispose.IsCompleted);
        candidateOwner.ReleaseDispose.Set();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dirtyRequest);
        await dispose;

        Assert.Equal(1, initialOwner.DisposeCount);
        Assert.Equal(1, candidateOwner.DisposeCount);
        Assert.Equal(WorkspaceDaemonSessionState.Disposed, session.State);
        Assert.Null(session.Generation);
    }

    [Fact]
    public async Task Dispose_ReleasesGenerationAndRejectsExecution()
    {
        var fingerprint = CleanFingerprint("head-a");
        var owner = new TrackingDisposable();
        var generation = CreateGeneration(owner, "initial");
        var session = CreateSession(
            Capture(Successful(fingerprint), Successful(fingerprint)),
            _ => Task.FromResult(generation));

        var initial = await ExecuteAsync(session);
        await session.DisposeAsync();

        Assert.True(initial.IsSuccessful, initial.Diagnostic);
        Assert.Equal(1, owner.DisposeCount);
        Assert.Null(session.Generation);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => ExecuteAsync(session));
    }

    private static WorkspaceDaemonSession CreateSession(
        Func<CancellationToken, Task<GitWorktreeFingerprintResolution>> capture,
        Func<CancellationToken, Task<WorkspaceDaemonGeneration>> load,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        return new WorkspaceDaemonSession(
            TargetPath,
            capture,
            load,
            delay ?? (static (_, _) => Task.CompletedTask));
    }

    private static Task<WorkspaceDaemonSessionResult> ExecuteAsync(WorkspaceDaemonSession session)
    {
        var command = CliParser.Parse(["workspace", "--target", TargetPath]);
        return session.ExecuteAsync(command, TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected daemon session state was not reached.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    private static Func<CancellationToken, Task<GitWorktreeFingerprintResolution>> Capture(
        params GitWorktreeFingerprintResolution[] resolutions)
    {
        var remaining = new Queue<GitWorktreeFingerprintResolution>(resolutions);
        return _ => Task.FromResult(remaining.Dequeue());
    }

    private static GitWorktreeFingerprintResolution Successful(GitWorktreeFingerprint fingerprint)
    {
        return GitWorktreeFingerprintResolution.Successful(fingerprint);
    }

    private static GitWorktreeFingerprintResolution Failed(
        GitWorktreeFingerprintFailureKind failureKind,
        string diagnostic)
    {
        return GitWorktreeFingerprintResolution.Failed(failureKind, diagnostic);
    }

    private static GitWorktreeFingerprint CleanFingerprint(string headCommit)
    {
        return new GitWorktreeFingerprint(headCommit, [], []);
    }

    private static GitWorktreeFingerprint DirtyFingerprint(string headCommit, string path, string objectId)
    {
        return new GitWorktreeFingerprint(
            headCommit,
            [new GitStatusFingerprint(" M", path, null)],
            [new GitFileFingerprint(path, objectId)]);
    }

    private static WorkspaceDaemonGeneration CreateGeneration(
        IDisposable owner,
        string standardOutput,
        Action? execute = null)
    {
        return new WorkspaceDaemonGeneration(
            owner,
            (_, _) =>
            {
                execute?.Invoke();
                return Task.FromResult(new CliProcessResult(0, standardOutput, string.Empty));
            });
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class BlockingDisposable : IDisposable
    {
        public TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim ReleaseDispose { get; } = new(initialState: false);

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            DisposeStarted.TrySetResult();
            ReleaseDispose.Wait();
            ReleaseDispose.Dispose();
        }
    }
}
