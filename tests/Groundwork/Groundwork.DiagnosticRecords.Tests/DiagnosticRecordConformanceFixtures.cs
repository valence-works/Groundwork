using Groundwork.Core.Manifests;
using Groundwork.DiagnosticRecords;

namespace Groundwork.DiagnosticRecords.Tests;

public interface IDiagnosticRecordStoreConformanceFixture
{
    IDiagnosticRecordStore OpenStore(DiagnosticRecordStreamDefinition definition);

    /// <summary>
    /// Opens a store with its own sessions and connection pool. Implementations must route the
    /// returned store through the same interception pipeline as <see cref="OpenStore"/>: server
    /// conformance tests observe an independent store's execution points to synchronize races.
    /// </summary>
    IDiagnosticRecordStore OpenIndependentStore(DiagnosticRecordStreamDefinition definition) => OpenStore(definition);
    void InterceptNext(DiagnosticExecutionPoint point, Func<CancellationToken, ValueTask> interceptor);
    DateTimeOffset GetUtcNow();
    void AdvanceTime(TimeSpan duration);
    void SetWallClock(DateTimeOffset utcNow);
}

/// <summary>
/// Provider fixture extension used by every relational diagnostic-record implementation to prove
/// that bounded reads and retention stay on native, scoped access paths.
/// </summary>
public interface IRelationalDiagnosticRecordStoreConformanceFixture : IDiagnosticRecordStoreConformanceFixture
{
    string FieldsPrimaryAccessPath { get; }

    ValueTask<DiagnosticRecordNativePlan> ExplainGroupedQueryAsync(
        DiagnosticRecordStreamDefinition definition,
        DiagnosticRecordGroupQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<string>> ExplainQueryAsync(
        DiagnosticRecordStreamDefinition definition,
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<string>> ExplainTrimAsync(
        DiagnosticRecordStreamDefinition definition,
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default);

    bool UsesSeek(
        IReadOnlyList<string> plan,
        string accessPath,
        IReadOnlyList<string> constrainedColumns);

    bool HasNativeScopedGroupedReduction(DiagnosticRecordNativePlan plan);

    ValueTask<IReadOnlyList<string>> ReadComparisonKeysAsync(
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        string field,
        CancellationToken cancellationToken = default);

    ValueTask<long> CountOperationRowsAsync(
        DiagnosticOperationKind kind,
        CancellationToken cancellationToken = default);
}

public static class DiagnosticRecordConformanceDeployment
{
    public static DiagnosticRecordDeploymentManifest Create(
        DiagnosticRecordStreamDefinition definition) =>
        new(
            new StorageManifest(
                new("diagnostic-conformance"),
                new("tests"),
                new("1"),
                [],
                new HashSet<string>(),
                []),
            [definition]);
}

public enum DiagnosticExecutionPoint
{
    AppendBeforeCommit,
    AppendBeforeStreamLock,
    /// <summary>
    /// Runs after one record has been staged inside the provider's atomic append transaction and
    /// before that transaction commits. Concrete fixtures must place this hook after durable work
    /// has begun so a thrown exception proves rollback rather than only preflight failure.
    /// </summary>
    AppendAfterRecordStagedBeforeCommit,
    AppendAfterCommitBeforeAcknowledgement,
    TrimBeforeCommit,
    /// <summary>
    /// Runs after at least one record is staged for deletion inside the provider's atomic trim
    /// transaction and before commit. A thrown exception or cancellation must roll back the
    /// records, stream statistics, and trim-operation ledger together.
    /// </summary>
    TrimAfterRecordDeletedBeforeCommit,
    TrimAfterCommitBeforeAcknowledgement
}

internal sealed class BoundedDiagnosticRecordStore :
    IDiagnosticRecordStore,
    IDiagnosticAppendHandler,
    IDiagnosticQueryHandler,
    IDiagnosticGroupedQueryHandler,
    IDiagnosticInspectHandler,
    IDiagnosticTrimHandler
{
    private readonly DiagnosticRecordStoreHandlers _inner;
    private readonly TimeSpan _operationTimeout;
    private readonly TimeSpan _hardTimeout;

    public BoundedDiagnosticRecordStore(
        IDiagnosticRecordStore inner,
        TimeSpan? operationTimeout = null,
        TimeSpan? hardTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner.Handlers;
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(10);
        _hardTimeout = hardTimeout ?? TimeSpan.FromSeconds(12);
        if (_operationTimeout <= TimeSpan.Zero || _hardTimeout <= _operationTimeout)
            throw new ArgumentOutOfRangeException(nameof(hardTimeout), "The hard timeout must be greater than a positive operation timeout.");
        Handlers = new(this, this, this, this) { GroupedQuery = this };
    }

    public DiagnosticRecordStoreHandlers Handlers { get; }
    public DiagnosticQueryHandlerCapabilities Capabilities => _inner.Query.Capabilities;
    DiagnosticGroupedQueryHandlerCapabilities IDiagnosticGroupedQueryHandler.Capabilities =>
        _inner.GroupedQuery.Capabilities;

    public ValueTask<DiagnosticAppendResult> AppendAsync(
        DiagnosticRecordBatch batch,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(token => _inner.Append.AppendAsync(batch, token), "append", cancellationToken);

    public ValueTask<DiagnosticRecordPage> QueryAsync(
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(token => _inner.Query.QueryAsync(query, token), "query", cancellationToken);

    public ValueTask<DiagnosticRecordGroupPage> QueryGroupsAsync(
        DiagnosticRecordGroupQuery query,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(token => _inner.GroupedQuery.QueryGroupsAsync(query, token), "query groups", cancellationToken);

    public ValueTask<DiagnosticStreamStatistics> InspectAsync(
        DiagnosticStreamInspectionRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(token => _inner.Inspect.InspectAsync(request, token), "inspect", cancellationToken);

    public ValueTask<DiagnosticTrimResult> TrimAsync(
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(token => _inner.Trim.TrimAsync(request, token), "trim", cancellationToken);

    private async ValueTask<T> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_operationTimeout);
        try
        {
            return await operation(deadline.Token).AsTask().WaitAsync(_hardTimeout, CancellationToken.None);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Diagnostic record-store conformance {operationName} exceeded {_operationTimeout}.", exception);
        }
        catch (TimeoutException exception)
        {
            deadline.Cancel();
            throw new TimeoutException($"Diagnostic record-store conformance {operationName} did not stop within {_hardTimeout}.", exception);
        }
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly object _syncRoot = new();
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_syncRoot)
            return _utcNow;
    }

    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        lock (_syncRoot)
            _utcNow += duration;
    }

    public void SetUtcNow(DateTimeOffset utcNow)
    {
        lock (_syncRoot)
            _utcNow = utcNow;
    }
}

internal sealed class AsyncConformanceBarrier(int participantCount)
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _remaining = participantCount;

    public async ValueTask SignalAndWaitAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Decrement(ref _remaining) == 0)
            _released.TrySetResult();
        await _released.Task.WaitAsync(cancellationToken);
    }
}
