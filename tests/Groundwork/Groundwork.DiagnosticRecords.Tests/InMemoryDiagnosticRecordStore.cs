using Groundwork.DiagnosticRecords;
using System.Collections.Frozen;

namespace Groundwork.DiagnosticRecords.Tests;

internal sealed class InMemoryDiagnosticRecordStoreFixture : IDiagnosticRecordStoreConformanceFixture
{
    private readonly InMemoryDiagnosticRecordStoreState _state = new();
    private readonly Dictionary<DiagnosticExecutionPoint, Queue<Func<CancellationToken, ValueTask>>> _interceptors = [];
    private readonly ManualTimeProvider _timeProvider = new(TimeProvider.System.GetUtcNow());

    public IDiagnosticRecordStore OpenStore(DiagnosticRecordStreamDefinition definition) =>
        new InMemoryDiagnosticRecordStore(definition, _state, InterceptAsync, _timeProvider);

    public DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();

    public void AdvanceTime(TimeSpan duration) => _timeProvider.Advance(duration);

    public void SetWallClock(DateTimeOffset utcNow) => _timeProvider.SetUtcNow(utcNow);

    public IReadOnlyList<string>? GetStagedTrimRecordIds(DiagnosticStorageScope scope, DiagnosticStreamId stream)
    {
        lock (_state.SyncRoot)
            return _state.StagedTrimRecords.TryGetValue(InMemoryDiagnosticRecordStore.StreamKey(scope, stream), out var records)
                ? Array.AsReadOnly(records.Select(x => x.RecordId).ToArray())
                : null;
    }

    public void InterceptNext(DiagnosticExecutionPoint point, Func<CancellationToken, ValueTask> interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        lock (_interceptors)
        {
            if (!_interceptors.TryGetValue(point, out var queue))
                _interceptors[point] = queue = [];
            queue.Enqueue(interceptor);
        }
    }

    private ValueTask InterceptAsync(DiagnosticExecutionPoint point, CancellationToken cancellationToken)
    {
        Func<CancellationToken, ValueTask>? interceptor = null;
        lock (_interceptors)
        {
            if (_interceptors.TryGetValue(point, out var queue) && queue.Count > 0)
                interceptor = queue.Dequeue();
        }
        return interceptor?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
    }
}

internal sealed class InMemoryDiagnosticRecordStoreState
{
    public object SyncRoot { get; } = new();
    public Dictionary<string, long> Cursors { get; } = [];
    public Dictionary<string, List<DiagnosticRecord>> Records { get; } = [];
    public Dictionary<string, DiagnosticFieldValue> LogicalHighWaters { get; } = [];
    public Dictionary<string, DiagnosticOperationLedgerEntry<DiagnosticAppendResult>> AppendResults { get; } = [];
    public Dictionary<string, DiagnosticOperationLedgerEntry<DiagnosticTrimResult>> TrimResults { get; } = [];
    public Dictionary<string, List<DiagnosticRecord>> StagedTrimRecords { get; } = [];
    public DateTimeOffset? ProviderClockHighWater { get; set; }
}

internal sealed record DiagnosticOperationLedgerEntry<T>(
    DiagnosticRequestFingerprint Fingerprint,
    T Result,
    DateTimeOffset OutcomeExpiresAt,
    DateTimeOffset TombstoneUntil);

internal sealed class InMemoryDiagnosticRecordStore : IDiagnosticRecordStore, IDiagnosticAppendHandler, IDiagnosticQueryHandler, IDiagnosticInspectHandler, IDiagnosticTrimHandler
{
    private readonly DiagnosticRecordStreamDefinition _definition;
    private readonly InMemoryDiagnosticRecordStoreState _state;
    private readonly Func<DiagnosticExecutionPoint, CancellationToken, ValueTask> _interceptAsync;
    private readonly TimeProvider _timeProvider;

    public InMemoryDiagnosticRecordStore(
        DiagnosticRecordStreamDefinition definition,
        InMemoryDiagnosticRecordStoreState state,
        Func<DiagnosticExecutionPoint, CancellationToken, ValueTask> interceptAsync,
        TimeProvider timeProvider)
    {
        _definition = DiagnosticRecordStreamDefinitionSnapshot.Capture(definition);
        DiagnosticRecordStreamDefinitionValidator.ValidateAndThrow(_definition);
        _state = state;
        _interceptAsync = interceptAsync;
        _timeProvider = timeProvider;
        Handlers = new(this, this, this, this);
    }

    public DiagnosticRecordStoreHandlers Handlers { get; }

    public DiagnosticQueryHandlerCapabilities Capabilities { get; } = new(
        Enum.GetValues<DiagnosticPredicateOperator>().ToFrozenSet(),
        SupportsCursorOrder: true,
        SupportsFieldOrder: true,
        SupportsSnapshotContinuation: true,
        SupportsExactCount: true,
        SupportsLatestPerKey: true);

    public async ValueTask<DiagnosticAppendResult> AppendAsync(
        DiagnosticRecordBatch batch,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        batch = DiagnosticRecordRequestSnapshot.Capture(batch);
        DiagnosticRecordRequestValidator.Validate(batch, _definition);
        var operationKey = OperationKey(batch.Scope, batch.Stream, batch.OperationId);
        lock (_state.SyncRoot)
        {
            if (TryReplayAppend(operationKey, batch, GetProviderNow(), out var replay))
                return replay;
        }
        DiagnosticRecordRequestValidator.ValidateNewOperationAdmission(batch, _definition, GetProviderNow());

        await _interceptAsync(DiagnosticExecutionPoint.AppendBeforeCommit, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var stagedRecords = new List<DiagnosticRecordInput>(batch.Records.Count);
        foreach (var record in batch.Records)
        {
            stagedRecords.Add(record);
            await _interceptAsync(DiagnosticExecutionPoint.AppendAfterRecordStagedBeforeCommit, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        DiagnosticAppendResult result;
        lock (_state.SyncRoot)
        {
            if (TryReplayAppend(operationKey, batch, GetProviderNow(), out var replay))
                return replay;
            var streamRecords = GetRecords(batch.Scope, batch.Stream);
            var existing = streamRecords.Select(x => x.RecordId).ToHashSet(StringComparer.Ordinal);
            var conflicts = batch.Records.Select(x => x.RecordId).Where(existing.Contains).Distinct(StringComparer.Ordinal).ToArray();
            if (conflicts.Length > 0)
                throw new DiagnosticRecordValidationException(conflicts.Select(x =>
                    new DiagnosticValidationError("append.record_id.exists", $"Record id '{x}' already exists in this scope and stream.", "records")).ToArray());

            var records = DiagnosticRecordSnapshot.Capture(stagedRecords
                .Select(x => new DiagnosticRecord(
                    x.RecordId,
                    x.OccurredAt,
                    x.Payload,
                    new DiagnosticCursor(NextCursor(batch.Scope, batch.Stream).ToString()),
                    x.Fields))
                .ToArray());
            streamRecords.AddRange(records);
            var key = StreamKey(batch.Scope, batch.Stream);
            var logicalHighWater = UpdateLogicalHighWater(key, records);
            result = new(
                DiagnosticAppendStatus.Committed,
                records,
                records[^1].Cursor,
                logicalHighWater);
            var committedAt = GetProviderNow();
            _state.AppendResults.Add(operationKey, new(
                batch.RequestFingerprint,
                result,
                committedAt + _definition.AppendIdempotencyWindow,
                TombstoneUntil(batch.OperationId, committedAt, _definition.AppendIdempotencyWindow)));
        }
        await _interceptAsync(DiagnosticExecutionPoint.AppendAfterCommitBeforeAcknowledgement, cancellationToken);
        return result;
    }

    public ValueTask<DiagnosticRecordPage> QueryAsync(
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query = DiagnosticRecordQuerySnapshot.Capture(query, _definition.Limits.MaxPredicateNodes);
        DiagnosticRecordQueryValidator.Validate(query, _definition, this);
        DiagnosticRecord[] records;
        DiagnosticCursor snapshot;
        lock (_state.SyncRoot)
        {
            records = GetRecords(query.Scope, query.Stream).ToArray();
            snapshot = query.Continuation?.SnapshotHighWater ?? new DiagnosticCursor(_state.Cursors.GetValueOrDefault(StreamKey(query.Scope, query.Stream)).ToString());
        }
        var snapshotCursor = ParseCursor(snapshot);
        IEnumerable<DiagnosticRecord> selected = records.Where(x => ParseCursor(x.Cursor) <= snapshotCursor);
        if (query.Predicate is not null)
            selected = selected.Where(x => Matches(x, query.Predicate));
        if (query.LatestPerKeyField is { } latestField)
        {
            var latestDefinition = Field(latestField);
            selected = selected
                .Where(x => Scalar(x, latestField) is not null)
                .GroupBy(
                    x => ComparisonKey(Scalar(x, latestField)!.Value, latestDefinition.CasePolicy),
                    StringComparer.Ordinal)
                .Select(x => x.MaxBy(y => ParseCursor(y.Cursor))!);
        }

        var order = query.Order ?? DiagnosticRecordOrder.CursorAscending;
        if (order.Field is { } orderedField && Field(orderedField).MissingValueBehavior == DiagnosticMissingValueBehavior.Excluded)
            selected = selected.Where(x => Scalar(x, orderedField) is not null);
        var exactCount = query.IncludeExactCount ? selected.LongCount() : (long?)null;
        selected = ApplyOrder(selected, order);
        if (query.Continuation is { } continuation)
            selected = selected.Where(x => IsAfter(x, continuation, order));
        var window = selected.Take(query.Limit + 1).ToArray();
        var pageRecords = DiagnosticRecordSnapshot.Capture(window.Take(query.Limit).ToArray());
        DiagnosticRecordContinuation? next = null;
        if (window.Length > query.Limit)
        {
            var last = pageRecords[^1];
            next = new(
                snapshot,
                last.Cursor,
                DiagnosticRequestFingerprint.ForQuery(query with { Continuation = null }, _definition),
                order.Field is null ? null : Scalar(last, order.Field));
        }

        return ValueTask.FromResult(new DiagnosticRecordPage(pageRecords, next, exactCount));
    }

    public ValueTask<DiagnosticStreamStatistics> InspectAsync(
        DiagnosticStreamInspectionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiagnosticRecordRequestValidator.Validate(request, _definition);
        lock (_state.SyncRoot)
            return ValueTask.FromResult(Statistics(request.Scope, request.Stream));
    }

    public async ValueTask<DiagnosticTrimResult> TrimAsync(
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiagnosticRecordRequestValidator.Validate(request, _definition);
        var operationKey = OperationKey(request.Scope, request.Stream, request.OperationId);
        lock (_state.SyncRoot)
        {
            if (TryReplayTrim(operationKey, request, GetProviderNow(), out var replay))
                return replay;
        }
        DiagnosticRecordRequestValidator.ValidateNewOperationAdmission(request, _definition, GetProviderNow());

        await _interceptAsync(DiagnosticExecutionPoint.TrimBeforeCommit, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var streamKey = StreamKey(request.Scope, request.Stream);
        List<DiagnosticRecord> stagedRecords;
        int examined;
        int stagedDeleteCount;
        lock (_state.SyncRoot)
        {
            stagedRecords = [.. GetRecords(request.Scope, request.Stream)];
            examined = stagedRecords.Count;
            stagedDeleteCount = Math.Max(0, examined - request.KeepNewest);
            if (stagedDeleteCount > 0)
                stagedRecords.RemoveRange(0, stagedDeleteCount);
            _state.StagedTrimRecords[streamKey] = stagedRecords;
        }
        try
        {
            if (stagedDeleteCount > 0)
            {
                await _interceptAsync(DiagnosticExecutionPoint.TrimAfterRecordDeletedBeforeCommit, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }

            DiagnosticTrimResult result;
            lock (_state.SyncRoot)
            {
                if (TryReplayTrim(operationKey, request, GetProviderNow(), out var replay))
                    return replay;
                var durableRecords = GetRecords(request.Scope, request.Stream);
                durableRecords.Clear();
                durableRecords.AddRange(stagedRecords);
                result = new(
                    DiagnosticTrimStatus.Completed,
                    new(examined),
                    new(stagedDeleteCount),
                    Statistics(request.Scope, request.Stream));
                var committedAt = GetProviderNow();
                _state.TrimResults.Add(operationKey, new(
                    request.RequestFingerprint,
                    result,
                    committedAt + _definition.TrimIdempotencyWindow,
                    TombstoneUntil(request.OperationId, committedAt, _definition.TrimIdempotencyWindow)));
            }
            await _interceptAsync(DiagnosticExecutionPoint.TrimAfterCommitBeforeAcknowledgement, cancellationToken);
            return result;
        }
        finally
        {
            lock (_state.SyncRoot)
                _state.StagedTrimRecords.Remove(streamKey);
        }
    }

    private bool TryReplayAppend(
        string operationKey,
        DiagnosticRecordBatch batch,
        DateTimeOffset providerNow,
        out DiagnosticAppendResult replay)
    {
        if (!_state.AppendResults.TryGetValue(operationKey, out var prior))
        {
            replay = null!;
            return false;
        }
        if (providerNow >= prior.OutcomeExpiresAt)
        {
            if (providerNow > prior.TombstoneUntil)
                _state.AppendResults.Remove(operationKey);
            throw new DiagnosticOperationExpiredException(DiagnosticOperationKind.Append, batch.OperationId);
        }
        if (prior.Fingerprint != batch.RequestFingerprint)
            throw new DiagnosticOperationConflictException(DiagnosticOperationKind.Append, batch.OperationId);
        replay = prior.Result with { Status = DiagnosticAppendStatus.Replayed };
        return true;
    }

    private bool TryReplayTrim(
        string operationKey,
        DiagnosticTrimRequest request,
        DateTimeOffset providerNow,
        out DiagnosticTrimResult replay)
    {
        if (!_state.TrimResults.TryGetValue(operationKey, out var prior))
        {
            replay = null!;
            return false;
        }
        if (providerNow >= prior.OutcomeExpiresAt)
        {
            if (providerNow > prior.TombstoneUntil)
                _state.TrimResults.Remove(operationKey);
            throw new DiagnosticOperationExpiredException(DiagnosticOperationKind.Trim, request.OperationId);
        }
        if (prior.Fingerprint != request.RequestFingerprint)
            throw new DiagnosticOperationConflictException(DiagnosticOperationKind.Trim, request.OperationId);
        replay = prior.Result with { Status = DiagnosticTrimStatus.Replayed };
        return true;
    }

    private DiagnosticStreamStatistics Statistics(DiagnosticStorageScope scope, DiagnosticStreamId stream)
    {
        var key = StreamKey(scope, stream);
        var records = GetRecords(scope, stream);
        var maxRetained = records.Count == 0 ? (DiagnosticCursor?)null : records.MaxBy(x => ParseCursor(x.Cursor))!.Cursor;
        var highWater = _state.Cursors.TryGetValue(key, out var cursor) ? new DiagnosticCursor(cursor.ToString()) : (DiagnosticCursor?)null;
        _state.LogicalHighWaters.TryGetValue(key, out var logicalHighWater);
        return new(
            new(records.Count),
            maxRetained,
            highWater,
            logicalHighWater == default ? null : logicalHighWater);
    }

    private DiagnosticFieldValue? UpdateLogicalHighWater(string streamKey, IReadOnlyList<DiagnosticRecord> records)
    {
        if (_definition.LogicalHighWaterField is not { } fieldName)
            return null;
        var field = Field(fieldName);
        foreach (var value in records.Select(x => Scalar(x, fieldName)).Where(x => x is not null).Select(x => x!.Value))
        {
            if (!_state.LogicalHighWaters.TryGetValue(streamKey, out var current) || value.CompareTo(current, field.CasePolicy) > 0)
                _state.LogicalHighWaters[streamKey] = value;
        }
        return _state.LogicalHighWaters.TryGetValue(streamKey, out var highWater) ? highWater : null;
    }

    private IEnumerable<DiagnosticRecord> ApplyOrder(IEnumerable<DiagnosticRecord> records, DiagnosticRecordOrder order)
    {
        if (order.Field is null)
            return order.Direction == DiagnosticSortDirection.Ascending
                ? records.OrderBy(x => ParseCursor(x.Cursor))
                : records.OrderByDescending(x => ParseCursor(x.Cursor));
        var field = Field(order.Field);
        var withValue = records.Where(x => Scalar(x, order.Field) is not null);
        var comparer = Comparer<DiagnosticFieldValue>.Create((left, right) => left.CompareTo(right, field.CasePolicy));
        return order.Direction == DiagnosticSortDirection.Ascending
            ? withValue.OrderBy(x => Scalar(x, order.Field)!.Value, comparer).ThenBy(x => ParseCursor(x.Cursor))
            : withValue.OrderByDescending(x => Scalar(x, order.Field)!.Value, comparer).ThenByDescending(x => ParseCursor(x.Cursor));
    }

    private bool IsAfter(DiagnosticRecord record, DiagnosticRecordContinuation continuation, DiagnosticRecordOrder order)
    {
        var cursorComparison = ParseCursor(record.Cursor).CompareTo(ParseCursor(continuation.LastCursor));
        if (order.Field is null)
            return order.Direction == DiagnosticSortDirection.Ascending ? cursorComparison > 0 : cursorComparison < 0;
        var field = Field(order.Field);
        var value = Scalar(record, order.Field);
        if (value is null || continuation.LastOrderValue is null)
            return false;
        var valueComparison = value.Value.CompareTo(continuation.LastOrderValue.Value, field.CasePolicy);
        if (valueComparison == 0)
            return order.Direction == DiagnosticSortDirection.Ascending ? cursorComparison > 0 : cursorComparison < 0;
        return order.Direction == DiagnosticSortDirection.Ascending ? valueComparison > 0 : valueComparison < 0;
    }

    private bool Matches(DiagnosticRecord record, DiagnosticRecordPredicate predicate) => predicate switch
    {
        DiagnosticRecordPredicate.All all => all.Predicates.All(x => Matches(record, x)),
        DiagnosticRecordPredicate.Any any => any.Predicates.Any(x => Matches(record, x)),
        DiagnosticRecordPredicate.Comparison comparison => Matches(record, comparison),
        _ => false
    };

    private bool Matches(DiagnosticRecord record, DiagnosticRecordPredicate.Comparison comparison)
    {
        IReadOnlyList<DiagnosticFieldValue> values;
        if (StringComparer.Ordinal.Equals(comparison.Field, DiagnosticRecordFieldNames.OccurredAt))
            values = [DiagnosticFieldValue.Timestamp(record.OccurredAt)];
        else
        {
            if (record.Fields is null || !record.Fields.TryGetValue(comparison.Field, out var storedValues))
                return false;
            values = storedValues;
        }
        var field = Field(comparison.Field);
        return comparison.Operator switch
        {
            DiagnosticPredicateOperator.Equal => values.Any(x => x.CompareTo(comparison.Values[0], field.CasePolicy) == 0),
            DiagnosticPredicateOperator.In => values.Any(x => comparison.Values.Any(y => x.CompareTo(y, field.CasePolicy) == 0)),
            DiagnosticPredicateOperator.RangeInclusive => values.Any(x =>
                x.CompareTo(comparison.Values[0], field.CasePolicy) >= 0 &&
                x.CompareTo(comparison.Values[1], field.CasePolicy) <= 0),
            DiagnosticPredicateOperator.Contains => values.Any(x => SearchKey(x, field.CasePolicy).Contains(
                SearchKey(comparison.Values[0], field.CasePolicy),
                StringComparison.Ordinal)),
            _ => false
        };
    }

    private DiagnosticFieldDefinition Field(string name) =>
        DiagnosticRecordFieldResolver.Resolve(_definition, name)!;

    private static DiagnosticFieldValue? Scalar(DiagnosticRecord record, string field)
    {
        if (StringComparer.Ordinal.Equals(field, DiagnosticRecordFieldNames.OccurredAt))
            return DiagnosticFieldValue.Timestamp(record.OccurredAt);
        return record.Fields is not null && record.Fields.TryGetValue(field, out var values) && values.Count > 0 ? values[0] : null;
    }

    private static string SearchKey(DiagnosticFieldValue value, DiagnosticStringCasePolicy casePolicy) =>
        value.Type == DiagnosticFieldType.String
            ? DiagnosticStringComparisonKey.CreateSearchKey(value.CanonicalValue, casePolicy)
            : value.CanonicalValue;

    private static string ComparisonKey(DiagnosticFieldValue value, DiagnosticStringCasePolicy casePolicy) =>
        value.Type == DiagnosticFieldType.String
            ? DiagnosticStringComparisonKey.Create(value.CanonicalValue, casePolicy)
            : value.CanonicalValue;

    private static long ParseCursor(DiagnosticCursor cursor) => long.Parse(cursor.Value, System.Globalization.CultureInfo.InvariantCulture);

    private long NextCursor(DiagnosticStorageScope scope, DiagnosticStreamId stream)
    {
        var key = StreamKey(scope, stream);
        var next = _state.Cursors.GetValueOrDefault(key) + 1;
        _state.Cursors[key] = next;
        return next;
    }

    private List<DiagnosticRecord> GetRecords(DiagnosticStorageScope scope, DiagnosticStreamId stream)
    {
        var key = StreamKey(scope, stream);
        if (!_state.Records.TryGetValue(key, out var records))
            _state.Records[key] = records = [];
        return records;
    }

    internal static string StreamKey(DiagnosticStorageScope scope, DiagnosticStreamId stream) =>
        $"{scope.TenantId.Length}:{scope.TenantId}{scope.ScopeId.Length}:{scope.ScopeId}{stream.Value.Length}:{stream.Value}";

    private static string OperationKey(DiagnosticStorageScope scope, DiagnosticStreamId stream, DiagnosticOperationId operationId) =>
        $"{StreamKey(scope, stream)}/{operationId.IssuedAt.ToUniversalTime():O}/{operationId.Nonce}";

    private DateTimeOffset GetProviderNow()
    {
        lock (_state.SyncRoot)
        {
            var wallClock = _timeProvider.GetUtcNow();
            if (_state.ProviderClockHighWater is null || wallClock > _state.ProviderClockHighWater)
                _state.ProviderClockHighWater = wallClock;
            return _state.ProviderClockHighWater.Value;
        }
    }

    private DateTimeOffset TombstoneUntil(
        DiagnosticOperationId operationId,
        DateTimeOffset committedAt,
        TimeSpan idempotencyWindow)
    {
        var outcomeExpiry = committedAt + idempotencyWindow;
        var admissionHorizon = operationId.IssuedAt + idempotencyWindow + _definition.MaxOperationClockSkew;
        return outcomeExpiry >= admissionHorizon ? outcomeExpiry : admissionHorizon;
    }
}
