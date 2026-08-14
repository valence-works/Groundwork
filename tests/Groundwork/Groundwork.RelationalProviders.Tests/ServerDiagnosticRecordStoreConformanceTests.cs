using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Groundwork.DiagnosticRecords.Tests;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public abstract class ServerDiagnosticRecordStoreConformanceTests : RelationalDiagnosticRecordStoreConformanceTests
{
    protected sealed override IRelationalDiagnosticRecordStoreConformanceFixture CreateRelationalFixture() =>
        CreateServerFixture();

    protected abstract IServerDiagnosticRecordStoreConformanceFixture CreateServerFixture();

    [Fact]
    public async Task Independent_stores_racing_the_same_operation_commit_once_and_replay_once()
    {
        var fixture = CreateServerFixture();
        var firstStaged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.InterceptNext(DiagnosticExecutionPoint.AppendAfterRecordStagedBeforeCommit, async cancellationToken =>
        {
            firstStaged.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
        });
        var first = fixture.OpenStore(TestDefinition);
        var second = fixture.OpenIndependentStore(TestDefinition);
        var now = fixture.GetUtcNow();
        var batch = DiagnosticRecordBatch.Create(
            new("tenant-a", "shell-a"),
            TestDefinition.Stream,
            new(now, "independent-same-operation-race"),
            [new("record-1", now, "{}")]);

        var firstAppend = first.AppendAsync(batch).AsTask();
        Task<DiagnosticAppendResult> secondAppend;
        try
        {
            await firstStaged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            secondAppend = await StartAppendBlockedAtStreamLockAsync(fixture, second, batch);
        }
        finally
        {
            releaseFirst.TrySetResult();
        }
        var results = await Task.WhenAll(firstAppend, secondAppend);
        var page = await second.QueryAsync(new(batch.Scope, batch.Stream, 10));

        Assert.Equal(1, results.Count(result => result.Status == DiagnosticAppendStatus.Committed));
        Assert.Equal(1, results.Count(result => result.Status == DiagnosticAppendStatus.Replayed));
        Assert.Equal("1", Assert.Single(page.Records).Cursor.Value);
    }

    [Fact]
    public async Task Independent_stores_racing_different_fingerprints_commit_once_and_conflict_once()
    {
        var fixture = CreateServerFixture();
        var firstStaged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.InterceptNext(DiagnosticExecutionPoint.AppendAfterRecordStagedBeforeCommit, async cancellationToken =>
        {
            firstStaged.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
        });
        var first = fixture.OpenStore(TestDefinition);
        var second = fixture.OpenIndependentStore(TestDefinition);
        var now = fixture.GetUtcNow();
        var operationId = new DiagnosticOperationId(now, "independent-conflicting-operation-race");
        var firstBatch = DiagnosticRecordBatch.Create(
            new("tenant-a", "shell-a"), TestDefinition.Stream, operationId, [new("record-a", now, "{}")]);
        var secondBatch = DiagnosticRecordBatch.Create(
            firstBatch.Scope, firstBatch.Stream, operationId, [new("record-b", now, "{}")]);

        var firstAppend = first.AppendAsync(firstBatch).AsTask();
        Task<DiagnosticAppendResult> secondAppend;
        try
        {
            await firstStaged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            secondAppend = await StartAppendBlockedAtStreamLockAsync(fixture, second, secondBatch);
        }
        finally
        {
            releaseFirst.TrySetResult();
        }
        var committed = await firstAppend;
        await Assert.ThrowsAsync<DiagnosticOperationConflictException>(async () => await secondAppend);
        var page = await second.QueryAsync(new(firstBatch.Scope, firstBatch.Stream, 10));

        Assert.Equal(DiagnosticAppendStatus.Committed, committed.Status);
        Assert.Equal("record-a", Assert.Single(page.Records).RecordId);
        Assert.Equal("1", Assert.Single(page.Records).Cursor.Value);
    }

    [Fact]
    public async Task Provider_commit_time_follows_same_stream_writer_order()
    {
        var fixture = CreateServerFixture();
        var firstBeforeLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.InterceptNext(DiagnosticExecutionPoint.AppendBeforeStreamLock, async cancellationToken =>
        {
            firstBeforeLock.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
        });
        var firstStore = fixture.OpenStore(TestDefinition);
        var secondStore = fixture.OpenIndependentStore(TestDefinition);
        var initialNow = fixture.GetUtcNow();
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var firstBatch = DiagnosticRecordBatch.Create(
            scope,
            TestDefinition.Stream,
            new(initialNow, "prelock-clock-first"),
            [new("prelock-record-first", initialNow, "{}")]);
        var secondBatch = DiagnosticRecordBatch.Create(
            scope,
            TestDefinition.Stream,
            new(initialNow, "prelock-clock-second"),
            [new("prelock-record-second", initialNow, "{}")]);

        var firstAppend = firstStore.AppendAsync(firstBatch).AsTask();
        await firstBeforeLock.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.AdvanceTime(TimeSpan.FromMinutes(1));
        Assert.Equal(DiagnosticAppendStatus.Committed, (await secondStore.AppendAsync(secondBatch)).Status);
        releaseFirst.TrySetResult();
        Assert.Equal(DiagnosticAppendStatus.Committed, (await firstAppend).Status);

        fixture.SetWallClock(initialNow + TestDefinition.AppendIdempotencyWindow + TimeSpan.FromTicks(1));
        var replay = await secondStore.AppendAsync(firstBatch);

        Assert.Equal(DiagnosticAppendStatus.Replayed, replay.Status);
    }

    [Fact]
    public async Task Staged_write_does_not_block_an_unrelated_scope()
    {
        var fixture = CreateServerFixture();
        var firstStaged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.InterceptNext(DiagnosticExecutionPoint.AppendAfterRecordStagedBeforeCommit, async cancellationToken =>
        {
            firstStaged.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
        });
        var first = fixture.OpenStore(TestDefinition);
        var second = fixture.OpenIndependentStore(TestDefinition);
        var now = fixture.GetUtcNow();
        var firstAppend = first.AppendAsync(DiagnosticRecordBatch.Create(
            new("tenant:a", "b"), TestDefinition.Stream, new(now, "blocked-write"),
            [
                new("record-1", now, "{}")
            ])).AsTask();

        await firstStaged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondAppend = second.AppendAsync(DiagnosticRecordBatch.Create(
            new("tenant", "a:b"), TestDefinition.Stream, new(now, "independent-write"),
            [
                new("record-2", now, "{}")
            ])).AsTask();

        var completed = await secondAppend.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DiagnosticAppendStatus.Committed, completed.Status);
        releaseFirst.TrySetResult();
        Assert.Equal(DiagnosticAppendStatus.Committed, (await firstAppend).Status);
    }

    [Fact]
    public async Task Concurrent_factories_materialize_idempotently()
    {
        var fixture = CreateServerFixture();

        await fixture.MaterializeConcurrentlyAsync(TestDefinition, 8);
    }

    [Fact]
    public async Task Provider_pool_limits_pressure_without_a_groundwork_serialization_gate()
    {
        var fixture = CreateServerFixture();

        await fixture.AssertPoolPressureAsync(TestDefinition);
    }

    // A bare "still pending after N ms" check false-passes on a loaded runner: the append may not
    // have reached the stream lock inside the window, so the assertion holds for the wrong reason.
    // Instead, first require the append to reach the stream-lock request point (must-fire, short
    // deadline), then require it to stay pending over a generous must-not-fire budget that exits
    // early only on violation. The one-shot interceptor lands on the fixture-wide queue shared by
    // every store, so callers must not have another append still on its way to the stream lock.
    private static async Task<Task<DiagnosticAppendResult>> StartAppendBlockedAtStreamLockAsync(
        IServerDiagnosticRecordStoreConformanceFixture fixture,
        IDiagnosticRecordStore store,
        DiagnosticRecordBatch batch)
    {
        var streamLockRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.InterceptNext(DiagnosticExecutionPoint.AppendBeforeStreamLock, _ =>
        {
            streamLockRequested.TrySetResult();
            return ValueTask.CompletedTask;
        });
        var append = store.AppendAsync(batch).AsTask();

        async Task AssertStillPendingAsync(Task allowedToComplete)
        {
            if (await Task.WhenAny(append, allowedToComplete) != append)
                return;
            var result = await append;
            Assert.Fail($"The append was expected to block on the stream lock but completed as {result.Status}.");
        }

        var streamLockReached = streamLockRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertStillPendingAsync(streamLockReached);
        await streamLockReached;
        await AssertStillPendingAsync(Task.Delay(TimeSpan.FromSeconds(1)));
        return append;
    }
}

public interface IServerDiagnosticRecordStoreConformanceFixture : IRelationalDiagnosticRecordStoreConformanceFixture
{
    Task MaterializeConcurrentlyAsync(DiagnosticRecordStreamDefinition definition, int count);
    Task AssertPoolPressureAsync(DiagnosticRecordStreamDefinition definition);
}

internal sealed class ManualServerTimeProvider(DateTimeOffset initialNow) : TimeProvider
{
    private readonly object sync = new();
    private DateTimeOffset now = initialNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (sync)
            return now;
    }

    public void Advance(TimeSpan duration)
    {
        lock (sync)
            now += duration;
    }

    public void Set(DateTimeOffset value)
    {
        lock (sync)
            now = value;
    }
}
