using Groundwork.DiagnosticRecords;
using Xunit;

namespace Groundwork.DiagnosticRecords.Tests;

public abstract class DiagnosticRecordStoreConformanceTests : DiagnosticRecordContractTests
{
    protected static DiagnosticRecordStreamDefinition TestDefinition { get; } = new(
        new DiagnosticStreamId("logs"),
        SchemaVersion: 1,
        LogicalStorageName: "diagnostic_logs",
        Fields:
        [
            new("sequence", DiagnosticFieldType.Int64, DiagnosticFieldCardinality.Scalar,
                new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Equal, DiagnosticPredicateOperator.In, DiagnosticPredicateOperator.RangeInclusive },
                IsRequired: false, IsOrderable: true),
            new("category", DiagnosticFieldType.String, DiagnosticFieldCardinality.Scalar,
                new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Equal, DiagnosticPredicateOperator.In, DiagnosticPredicateOperator.RangeInclusive, DiagnosticPredicateOperator.Contains },
                IsOrderable: true, MaxStringBytes: 32),
            new("service", DiagnosticFieldType.String, DiagnosticFieldCardinality.Scalar,
                new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Equal, DiagnosticPredicateOperator.In, DiagnosticPredicateOperator.Contains },
                IsOrderable: true, SupportsLatestPerKey: true, CasePolicy: DiagnosticStringCasePolicy.AsciiIgnoreCase, MaxStringBytes: 64),
            new("tags", DiagnosticFieldType.String, DiagnosticFieldCardinality.Multiple,
                new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.In, DiagnosticPredicateOperator.Contains },
                CasePolicy: DiagnosticStringCasePolicy.AsciiIgnoreCase, MaxValues: 8, MaxStringBytes: 32),
            new("unicode", DiagnosticFieldType.String, DiagnosticFieldCardinality.Scalar,
                new HashSet<DiagnosticPredicateOperator>
                {
                    DiagnosticPredicateOperator.Equal, DiagnosticPredicateOperator.In,
                    DiagnosticPredicateOperator.Contains
                },
                IsOrderable: true, SupportsLatestPerKey: true,
                CasePolicy: DiagnosticStringCasePolicy.UnicodeOrdinalIgnoreCase,
                MaxStringBytes: 65_536)
        ],
        Limits: new(MaxBatchRecords: 100, MaxPayloadBytes: 4_096, MaxRecordIdBytes: 128, MaxFieldsPerRecord: 8, MaxQueryLimit: 100, MaxGroupedQueryInputRecords: 100, MaxPredicateNodes: 32, MaxPredicateValues: 9),
        MaxOperationClockSkew: TimeSpan.FromMinutes(5),
        AppendIdempotencyWindow: TimeSpan.FromDays(1),
        TrimIdempotencyWindow: TimeSpan.FromDays(1),
        LogicalHighWaterField: "sequence",
        GroupReductionProfiles:
        [
            new(
                "service-summary",
                "service",
                [
                    new("start", DiagnosticGroupReducerKind.MinTimestamp, DiagnosticRecordFieldNames.OccurredAt),
                    new("end", DiagnosticGroupReducerKind.MaxTimestamp, DiagnosticRecordFieldNames.OccurredAt),
                    new("spanCount", DiagnosticGroupReducerKind.SumInt64, "sequence"),
                    new("tags", DiagnosticGroupReducerKind.SetUnionString, "tags"),
                    new("status", DiagnosticGroupReducerKind.MaxInt64, "sequence"),
                    new(
                        "rootName",
                        DiagnosticGroupReducerKind.FirstBy,
                        "category",
                        "sequence",
                        DiagnosticSortDirection.Ascending,
                        DiagnosticGroupFirstByTieBreak.CursorAscending)
                ],
                [
                    new("status", new HashSet<DiagnosticPredicateOperator>
                    {
                        DiagnosticPredicateOperator.Equal,
                        DiagnosticPredicateOperator.In,
                        DiagnosticPredicateOperator.RangeInclusive
                    }),
                    new("tags", new HashSet<DiagnosticPredicateOperator>
                    {
                        DiagnosticPredicateOperator.Contains
                    })
                ],
                new HashSet<string> { "start", "end", "status" },
                100,
                8)
        ]);

    // Reaching an interception point is a must-fire path, so this ceiling only has to sit above the
    // bounded store's own hard timeout; it never gates a run that reaches the point.
    private static readonly TimeSpan InterceptionMustFireTimeout = TimeSpan.FromSeconds(30);

    protected abstract IDiagnosticRecordStoreConformanceFixture CreateFixture();

    private IDiagnosticRecordStore CreateStore() => OpenStore(CreateFixture());

    private static IDiagnosticRecordStore OpenStore(IDiagnosticRecordStoreConformanceFixture fixture) =>
        new BoundedDiagnosticRecordStore(fixture.OpenStore(TestDefinition));

    [Fact]
    public async Task Append_assigns_monotonic_cursors_within_scope_and_stream()
    {
        var fixture = CreateFixture();
        var store = OpenStore(fixture);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var stream = new DiagnosticStreamId("logs");
        var result = await store.AppendAsync(new DiagnosticRecordBatch(
            scope,
            stream,
            new DiagnosticOperationId(fixture.GetUtcNow(), "append-1"),
            DiagnosticRequestFingerprint.ForAppend(scope, stream,
            [
                new DiagnosticRecordInput("record-1", DateTimeOffset.Parse("2026-07-12T12:00:01Z"), "{\"message\":\"one\"}"),
                new DiagnosticRecordInput("record-2", DateTimeOffset.Parse("2026-07-12T12:00:02Z"), "{\"message\":\"two\"}")
            ]),
            [
                new DiagnosticRecordInput("record-1", DateTimeOffset.Parse("2026-07-12T12:00:01Z"), "{\"message\":\"one\"}"),
                new DiagnosticRecordInput("record-2", DateTimeOffset.Parse("2026-07-12T12:00:02Z"), "{\"message\":\"two\"}")
            ]));

        Assert.Equal(DiagnosticAppendStatus.Committed, result.Status);
        Assert.Equal(["1", "2"], result.Records.Select(x => x.Cursor.Value));
    }

    [Fact]
    public async Task Append_replay_returns_the_original_outcome_without_duplicate_records()
    {
        var store = CreateStore();
        var batch = Batch("append-retry", "record-1");

        var committed = await store.AppendAsync(batch);
        var replayed = await store.AppendAsync(batch);

        Assert.Equal(DiagnosticAppendStatus.Committed, committed.Status);
        Assert.Equal(DiagnosticAppendStatus.Replayed, replayed.Status);
        Assert.Equal(committed.Records, replayed.Records);
    }

    [Fact]
    public async Task Append_results_are_immutable_snapshots_and_cannot_corrupt_replay_outcomes()
    {
        var store = CreateStore();
        var batch = Batch("immutable-append", Record(
            "record-1",
            ("category", [DiagnosticFieldValue.String("original")])));
        var committed = await store.AppendAsync(batch);
        var writableView = Assert.IsAssignableFrom<IList<DiagnosticRecord>>(committed.Records);

        Assert.Throws<NotSupportedException>(() => writableView[0] = new(
            "corrupted",
            TimeProvider.System.GetUtcNow(),
            "{}",
            new("999")));
        var committedRecord = Assert.Single(committed.Records);
        var writableFields = Assert.IsAssignableFrom<IDictionary<string, IReadOnlyList<DiagnosticFieldValue>>>(committedRecord.Fields);
        Assert.Throws<NotSupportedException>(() => writableFields["category"] = [DiagnosticFieldValue.String("corrupted")]);
        var writableValues = Assert.IsAssignableFrom<IList<DiagnosticFieldValue>>(committedRecord.Fields!["category"]);
        Assert.Throws<NotSupportedException>(() => writableValues[0] = DiagnosticFieldValue.String("corrupted"));
        var replay = await store.AppendAsync(batch);

        Assert.Equal("record-1", Assert.Single(replay.Records).RecordId);
        Assert.Equal("1", Assert.Single(replay.Records).Cursor.Value);
    }

    [Fact]
    public async Task Append_rejects_same_operation_id_with_a_different_fingerprint()
    {
        var store = CreateStore();
        var first = Batch("append-conflict", "record-1");
        await store.AppendAsync(first);
        var conflicting = Batch("append-conflict", "record-2") with
        {
            OperationId = first.OperationId
        };

        var exception = await Assert.ThrowsAsync<DiagnosticOperationConflictException>(async () =>
            await store.AppendAsync(conflicting));

        Assert.Equal(DiagnosticOperationKind.Append, exception.OperationKind);
    }

    [Fact]
    public async Task Append_rejects_operation_ids_outside_the_declared_idempotency_window()
    {
        var store = CreateStore();
        var batch = Batch("expired", "record-1") with
        {
            OperationId = new DiagnosticOperationId(DateTimeOffset.UnixEpoch, "expired")
        };

        var exception = await Assert.ThrowsAsync<DiagnosticOperationExpiredException>(async () =>
            await store.AppendAsync(batch));

        Assert.Equal(DiagnosticOperationKind.Append, exception.OperationKind);
    }

    [Fact]
    public async Task Append_replay_expiry_is_measured_from_provider_commit_time_not_caller_issuance_time()
    {
        var fixture = CreateFixture();
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var stream = new DiagnosticStreamId("logs");
        var issuedNearWindowStart = fixture.GetUtcNow() - TestDefinition.AppendIdempotencyWindow + TimeSpan.FromMinutes(1);
        var batch = DiagnosticRecordBatch.Create(
            scope,
            stream,
            new(issuedNearWindowStart, "provider-receipt-window"),
            [new("record-1", fixture.GetUtcNow(), "{}")]);
        var store = OpenStore(fixture);

        await store.AppendAsync(batch);
        fixture.AdvanceTime(TimeSpan.FromMinutes(10));
        var replay = await store.AppendAsync(batch);
        fixture.AdvanceTime(TestDefinition.AppendIdempotencyWindow - TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1));

        Assert.Equal(DiagnosticAppendStatus.Replayed, replay.Status);
        await Assert.ThrowsAsync<DiagnosticOperationExpiredException>(async () =>
            await store.AppendAsync(batch));
    }

    [Fact]
    public async Task Expired_append_identity_never_becomes_admissible_again_after_retries_cleanup_or_clock_regression()
    {
        var fixture = CreateFixture();
        var committedAt = fixture.GetUtcNow();
        var operationId = new DiagnosticOperationId(
            committedAt + TestDefinition.MaxOperationClockSkew,
            "permanently-expired-append");
        var batch = DiagnosticRecordBatch.Create(
            new("tenant-a", "shell-a"),
            TestDefinition.Stream,
            operationId,
            [new("record-1", committedAt, "{}")]);
        var store = OpenStore(fixture);
        await store.AppendAsync(batch);

        fixture.AdvanceTime(TestDefinition.AppendIdempotencyWindow + TimeSpan.FromTicks(1));
        await Assert.ThrowsAsync<DiagnosticOperationExpiredException>(async () => await store.AppendAsync(batch));
        await Assert.ThrowsAsync<DiagnosticOperationExpiredException>(async () => await store.AppendAsync(batch));

        fixture.AdvanceTime(TestDefinition.MaxOperationClockSkew * 2 + TimeSpan.FromTicks(1));
        await Assert.ThrowsAsync<DiagnosticOperationExpiredException>(async () => await store.AppendAsync(batch));
        await Assert.ThrowsAsync<DiagnosticOperationExpiredException>(async () => await store.AppendAsync(batch));

        fixture.SetWallClock(committedAt);
        var restarted = OpenStore(fixture);
        await Assert.ThrowsAsync<DiagnosticOperationExpiredException>(async () => await restarted.AppendAsync(batch));
        var statistics = await restarted.InspectAsync(new(batch.Scope, batch.Stream));

        Assert.Equal(1, statistics.RetainedCount.Value);
        Assert.Equal("1", statistics.LifetimeCommittedCursorHighWater.GetValueOrDefault().Value);
    }

    [Fact]
    public async Task Append_rejects_a_fingerprint_that_does_not_describe_the_request()
    {
        var store = CreateStore();
        var batch = Batch("forged", "record-1");
        var forged = batch with { Records = [new DiagnosticRecordInput("record-2", TimeProvider.System.GetUtcNow(), "{}")] };

        await Assert.ThrowsAsync<DiagnosticRequestFingerprintMismatchException>(async () =>
            await store.AppendAsync(forged));
    }

    [Fact]
    public async Task Invalid_batch_is_rejected_before_any_cursor_is_allocated()
    {
        var store = CreateStore();
        var invalid = Batch("invalid", "duplicate", "duplicate");

        var exception = await Assert.ThrowsAsync<DiagnosticRecordValidationException>(async () =>
            await store.AppendAsync(invalid));
        var committed = await store.AppendAsync(Batch("valid-after-invalid", "record-1"));

        Assert.Contains(exception.Errors, x => x.Code == "append.record_id.duplicate");
        Assert.Equal("1", Assert.Single(committed.Records).Cursor.Value);
    }

    [Fact]
    public async Task Query_returns_records_in_cursor_order_and_declares_capability_from_its_handler()
    {
        var store = CreateStore();
        await store.AppendAsync(Batch("append-query", "record-1", "record-2", "record-3"));

        var page = await store.QueryAsync(new DiagnosticRecordQuery(
            new DiagnosticStorageScope("tenant-a", "shell-a"),
            new DiagnosticStreamId("logs"),
            10));

        Assert.Equal(["record-1", "record-2", "record-3"], page.Records.Select(x => x.RecordId));
        Assert.Same(store.Handlers.Query.Capabilities, store.Handlers.Capabilities.Query);
    }

    [Fact]
    public async Task Query_pages_are_immutable_snapshots()
    {
        var store = CreateStore();
        var batch = Batch("immutable-page", Record(
            "record-1",
            ("category", [DiagnosticFieldValue.String("original")])));
        await store.AppendAsync(batch);
        var page = await store.QueryAsync(new(new("tenant-a", "shell-a"), new("logs"), 10));
        var writableView = Assert.IsAssignableFrom<IList<DiagnosticRecord>>(page.Records);

        Assert.Throws<NotSupportedException>(() => writableView[0] = new(
            "corrupted",
            TimeProvider.System.GetUtcNow(),
            "{}",
            new("999")));
        var pageRecord = Assert.Single(page.Records);
        var writableFields = Assert.IsAssignableFrom<IDictionary<string, IReadOnlyList<DiagnosticFieldValue>>>(pageRecord.Fields);
        Assert.Throws<NotSupportedException>(() => writableFields["category"] = [DiagnosticFieldValue.String("corrupted")]);
        var writableValues = Assert.IsAssignableFrom<IList<DiagnosticFieldValue>>(pageRecord.Fields!["category"]);
        Assert.Throws<NotSupportedException>(() => writableValues[0] = DiagnosticFieldValue.String("corrupted"));
        var replay = await store.AppendAsync(batch);
        var nextRead = await store.QueryAsync(new(new("tenant-a", "shell-a"), new("logs"), 10));

        Assert.Equal(DiagnosticFieldValue.String("original"), Assert.Single(Assert.Single(replay.Records).Fields!["category"]));
        Assert.Equal(DiagnosticFieldValue.String("original"), Assert.Single(Assert.Single(nextRead.Records).Fields!["category"]));
    }

    [Fact]
    public async Task Identical_record_and_operation_ids_are_isolated_by_explicit_storage_scope()
    {
        var store = CreateStore();
        var first = Batch("shared-operation", "shared-record");
        var second = DiagnosticRecordBatch.Create(
            new DiagnosticStorageScope("tenant-b", "shell-a"),
            first.Stream,
            first.OperationId,
            first.Records);

        var firstResult = await store.AppendAsync(first);
        var secondResult = await store.AppendAsync(second);
        var firstPage = await store.QueryAsync(new(first.Scope, first.Stream, 10));
        var secondPage = await store.QueryAsync(new(second.Scope, second.Stream, 10));

        Assert.Equal("1", Assert.Single(firstResult.Records).Cursor.Value);
        Assert.Equal("1", Assert.Single(secondResult.Records).Cursor.Value);
        Assert.Equal("shared-record", Assert.Single(firstPage.Records).RecordId);
        Assert.Equal("shared-record", Assert.Single(secondPage.Records).RecordId);
    }

    [Fact]
    public async Task Same_tenant_record_operation_and_stream_ids_are_isolated_by_storage_scope_identity()
    {
        var store = CreateStore();
        var first = Batch("shared-operation", "shared-record");
        var second = DiagnosticRecordBatch.Create(
            new DiagnosticStorageScope(first.Scope.TenantId, "shell-b"),
            first.Stream,
            first.OperationId,
            first.Records);

        await store.AppendAsync(first);
        await store.AppendAsync(second);
        await store.TrimAsync(DiagnosticTrimRequest.Create(
            first.Scope,
            first.Stream,
            new(TimeProvider.System.GetUtcNow(), "scope-a-trim"),
            0));
        var firstStatistics = await store.InspectAsync(new(first.Scope, first.Stream));
        var secondStatistics = await store.InspectAsync(new(second.Scope, second.Stream));
        var secondPage = await store.QueryAsync(new(second.Scope, second.Stream, 10));

        Assert.Equal(0, firstStatistics.RetainedCount.Value);
        Assert.Equal(1, secondStatistics.RetainedCount.Value);
        Assert.Equal("shared-record", Assert.Single(secondPage.Records).RecordId);
        Assert.Equal("1", Assert.Single(secondPage.Records).Cursor.Value);
    }

    [Fact]
    public async Task Append_enforces_declared_field_bounds_before_allocating_a_cursor()
    {
        var store = CreateStore();
        var invalid = Batch("invalid-field", Record(
            "record-1",
            ("category", [DiagnosticFieldValue.String(new string('x', 33))])));

        var exception = await Assert.ThrowsAsync<DiagnosticRecordValidationException>(async () =>
            await store.AppendAsync(invalid));
        var valid = await store.AppendAsync(Batch("valid-field", Record(
            "record-2",
            ("category", [DiagnosticFieldValue.String("runtime")]))));

        Assert.Contains(exception.Errors, x => x.Code == "append.field.string_too_large");
        Assert.Equal("1", Assert.Single(valid.Records).Cursor.Value);
    }

    [Theory]
    [InlineData("Å")]
    [InlineData("İ")]
    [InlineData("ß")]
    [InlineData("é")]
    public async Task Append_rejects_non_ascii_case_insensitive_values_before_ordering_or_latest_per_key(
        string nonPortableValue)
    {
        var store = CreateStore();
        var invalid = Batch(
            $"non-ascii-append-{Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(nonPortableValue))}",
            Record("record-1", ("service", [DiagnosticFieldValue.String(nonPortableValue)])));

        var exception = await Assert.ThrowsAsync<DiagnosticRecordValidationException>(async () =>
            await store.AppendAsync(invalid));
        var valid = await store.AppendAsync(Batch("after-non-ascii-rejection", Record(
            "record-2",
            ("service", [DiagnosticFieldValue.String("ASCII")]))));

        Assert.Contains(exception.Errors, x => x.Code == "append.field.case_domain");
        Assert.Equal("1", Assert.Single(valid.Records).Cursor.Value);
    }

    [Fact]
    public async Task Query_executes_declared_predicates_with_case_policy_and_exact_count()
    {
        var store = CreateStore();
        await store.AppendAsync(Batch("predicate-data",
            Record("record-1", ("sequence", [DiagnosticFieldValue.Int64(1)]), ("service", [DiagnosticFieldValue.String("API")]), ("tags", [DiagnosticFieldValue.String("blue")])),
            Record("record-2", ("sequence", [DiagnosticFieldValue.Int64(2)]), ("service", [DiagnosticFieldValue.String("api")]), ("tags", [DiagnosticFieldValue.String("red")])),
            Record("record-3", ("sequence", [DiagnosticFieldValue.Int64(3)]), ("service", [DiagnosticFieldValue.String("worker")]), ("tags", [DiagnosticFieldValue.String("blue")]))));
        var predicate = new DiagnosticRecordPredicate.All(
        [
            DiagnosticRecordPredicate.RangeInclusive("sequence", DiagnosticFieldValue.Int64(1), DiagnosticFieldValue.Int64(2)),
            DiagnosticRecordPredicate.Equal("service", DiagnosticFieldValue.String("api")),
            DiagnosticRecordPredicate.In("tags", DiagnosticFieldValue.String("BLUE"))
        ]);

        var page = await store.QueryAsync(new(
            new("tenant-a", "shell-a"),
            new("logs"),
            Limit: 10,
            IncludeExactCount: true,
            Predicate: predicate));

        Assert.Equal("record-1", Assert.Single(page.Records).RecordId);
        Assert.Equal(1, page.ExactCount);
    }

    [Theory]
    [InlineData(DiagnosticPredicateOperator.Equal, "Å")]
    [InlineData(DiagnosticPredicateOperator.Contains, "İ")]
    [InlineData(DiagnosticPredicateOperator.Equal, "ß")]
    [InlineData(DiagnosticPredicateOperator.Contains, "é")]
    public async Task Query_rejects_non_ascii_case_insensitive_equality_and_contains_values(
        DiagnosticPredicateOperator operation,
        string nonPortableValue)
    {
        var store = CreateStore();
        var query = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"),
            TestDefinition.Stream,
            10,
            Predicate: new DiagnosticRecordPredicate.Comparison(
                "service",
                operation,
                [DiagnosticFieldValue.String(nonPortableValue)]));

        var exception = await Assert.ThrowsAsync<DiagnosticRecordValidationException>(async () =>
            await store.QueryAsync(query));

        Assert.Contains(exception.Errors, x => x.Code == "query.predicate.case_domain");
    }

    [Fact]
    public async Task Field_order_continuation_rejects_a_non_ascii_case_insensitive_key()
    {
        var store = CreateStore();
        var query = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"),
            TestDefinition.Stream,
            10,
            Order: new("service"));
        var continuation = new DiagnosticRecordContinuation(
            new("1"),
            new("1"),
            DiagnosticRequestFingerprint.ForQuery(query, TestDefinition),
            DiagnosticFieldValue.String("Å"));

        var exception = await Assert.ThrowsAsync<DiagnosticRecordValidationException>(async () =>
            await store.QueryAsync(query with { Continuation = continuation }));

        Assert.Contains(exception.Errors, x => x.Code == "query.continuation.case_domain");
    }

    [Fact]
    public async Task Field_plus_cursor_continuation_is_bound_to_the_first_page_snapshot()
    {
        var store = CreateStore();
        await store.AppendAsync(Batch("snapshot-seed",
            Record("record-1", ("sequence", [DiagnosticFieldValue.Int64(10)])),
            Record("record-2", ("sequence", [DiagnosticFieldValue.Int64(20)])),
            Record("record-3", ("sequence", [DiagnosticFieldValue.Int64(30)]))));
        var query = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"),
            new("logs"),
            Limit: 2,
            Order: new("sequence"),
            IncludeExactCount: true);

        var first = await store.QueryAsync(query);
        await store.AppendAsync(Batch("backdated-concurrent",
            Record("record-4", ("sequence", [DiagnosticFieldValue.Int64(25)]))));
        var second = await store.QueryAsync(query with { Continuation = first.Continuation });

        Assert.Equal(["record-1", "record-2"], first.Records.Select(x => x.RecordId));
        Assert.Equal("3", first.Continuation!.SnapshotHighWater.Value);
        Assert.Equal(DiagnosticFieldValue.Int64(20), first.Continuation.LastOrderValue);
        Assert.Equal(["record-3"], second.Records.Select(x => x.RecordId));
        Assert.Equal(3, second.ExactCount);
        Assert.Null(second.Continuation);
    }

    [Fact]
    public async Task Latest_per_logical_key_uses_declared_case_policy_and_durable_cursor()
    {
        var store = CreateStore();
        await store.AppendAsync(Batch("latest-seed",
            Record("api-old", ("service", [DiagnosticFieldValue.String("API")])),
            Record("api-new", ("service", [DiagnosticFieldValue.String("api")])),
            Record("worker", ("service", [DiagnosticFieldValue.String("worker")]))));

        var page = await store.QueryAsync(new(
            new("tenant-a", "shell-a"),
            new("logs"),
            Limit: 10,
            IncludeExactCount: true,
            LatestPerKeyField: "service"));

        Assert.Equal(["api-new", "worker"], page.Records.Select(x => x.RecordId));
        Assert.Equal(2, page.ExactCount);
    }

    [Fact]
    public async Task Ascii_ignore_case_key_drives_equality_contains_ordering_and_latest_per_key()
    {
        var store = CreateStore();
        await store.AppendAsync(Batch("ascii-case-vectors",
            Record(
                "alpha-old",
                ("service", [DiagnosticFieldValue.String("ALPHA")]),
                ("tags", [DiagnosticFieldValue.String("TimeOut-marker")])),
            Record(
                "beta",
                ("service", [DiagnosticFieldValue.String("beta")]),
                ("tags", [DiagnosticFieldValue.String("healthy")])),
            Record(
                "alpha-new",
                ("service", [DiagnosticFieldValue.String("alpha")]),
                ("tags", [DiagnosticFieldValue.String("healthy")]))));

        var equality = await store.QueryAsync(new(
            new("tenant-a", "shell-a"),
            TestDefinition.Stream,
            10,
            Predicate: DiagnosticRecordPredicate.Equal("service", DiagnosticFieldValue.String("aLpHa"))));
        var contains = await store.QueryAsync(new(
            new("tenant-a", "shell-a"),
            TestDefinition.Stream,
            10,
            Predicate: DiagnosticRecordPredicate.Contains("tags", "TIMEOUT")));
        var ordered = await store.QueryAsync(new(
            new("tenant-a", "shell-a"),
            TestDefinition.Stream,
            10,
            Order: new("service")));
        var latest = await store.QueryAsync(new(
            new("tenant-a", "shell-a"),
            TestDefinition.Stream,
            10,
            LatestPerKeyField: "service"));

        Assert.Equal(["alpha-old", "alpha-new"], equality.Records.Select(x => x.RecordId));
        Assert.Equal("alpha-old", Assert.Single(contains.Records).RecordId);
        Assert.Equal(["alpha-old", "alpha-new", "beta"], ordered.Records.Select(x => x.RecordId));
        Assert.Equal(["beta", "alpha-new"], latest.Records.Select(x => x.RecordId));
    }

    [Fact]
    public async Task Unicode_ordinal_ignore_case_preserves_long_values_and_drives_all_declared_query_shapes()
    {
        var store = CreateStore();
        var longPrefix = new string('x', 65_535);
        Assert.Equal(65_536, System.Text.Encoding.UTF8.GetByteCount($"{longPrefix}A"));
        await store.AppendAsync(Batch("unicode-case-vectors",
            Record("angstrom-old", ("unicode", [DiagnosticFieldValue.String("Ångström-Σ😀")])),
            Record("long-b", ("unicode", [DiagnosticFieldValue.String($"{longPrefix}b")])),
            Record("long-a", ("unicode", [DiagnosticFieldValue.String($"{longPrefix}A")])),
            Record("angstrom-new", ("unicode", [DiagnosticFieldValue.String("ångström-ς😀")]))));

        var equality = await store.QueryAsync(new(
            new("tenant-a", "shell-a"), TestDefinition.Stream, 10,
            Predicate: DiagnosticRecordPredicate.Equal(
                "unicode",
                DiagnosticFieldValue.String("ÅNGSTRÖM-σ😀"))));
        var membership = await store.QueryAsync(new(
            new("tenant-a", "shell-a"), TestDefinition.Stream, 10,
            Predicate: DiagnosticRecordPredicate.In(
                "unicode",
                DiagnosticFieldValue.String("ÅNGSTRÖM-σ😀"),
                DiagnosticFieldValue.String($"{longPrefix}a"))));
        var contains = await store.QueryAsync(new(
            new("tenant-a", "shell-a"), TestDefinition.Stream, 10,
            Predicate: DiagnosticRecordPredicate.Contains("unicode", "STRÖM-σ😀")));
        var containsEmpty = await store.QueryAsync(new(
            new("tenant-a", "shell-a"), TestDefinition.Stream, 10,
            Predicate: DiagnosticRecordPredicate.Contains("unicode", "")));
        var ordered = await store.QueryAsync(new(
            new("tenant-a", "shell-a"), TestDefinition.Stream, 10, Order: new("unicode")));
        var latest = await store.QueryAsync(new(
            new("tenant-a", "shell-a"), TestDefinition.Stream, 10,
            IncludeExactCount: true, LatestPerKeyField: "unicode"));

        Assert.Equal(["angstrom-old", "angstrom-new"], equality.Records.Select(record => record.RecordId));
        Assert.Equal(["angstrom-old", "long-a", "angstrom-new"], membership.Records.Select(record => record.RecordId));
        Assert.Equal(["angstrom-old", "angstrom-new"], contains.Records.Select(record => record.RecordId));
        Assert.Equal(
            ["angstrom-old", "long-b", "long-a", "angstrom-new"],
            containsEmpty.Records.Select(record => record.RecordId));
        Assert.Equal(
            ["long-a", "long-b", "angstrom-old", "angstrom-new"],
            ordered.Records.Select(record => record.RecordId));
        Assert.Equal(["long-b", "long-a", "angstrom-new"], latest.Records.Select(record => record.RecordId));
        Assert.Equal(3, latest.ExactCount);
        Assert.Equal($"{longPrefix}A", latest.Records.Single(record => record.RecordId == "long-a").Fields!["unicode"].Single().CanonicalValue);
    }

    [Fact]
    public async Task Well_formed_unicode_including_null_round_trips_without_narrowing()
    {
        var store = CreateStore();
        const string value = "before\0Å😀after";
        await store.AppendAsync(Batch("unicode-null", Record(
            "unicode-null-record",
            ("unicode", [DiagnosticFieldValue.String(value)]))));

        var page = await store.QueryAsync(new(
            new("tenant-a", "shell-a"), TestDefinition.Stream, 10,
            Predicate: DiagnosticRecordPredicate.Contains("unicode", "\0å😀")));

        Assert.Equal(value, Assert.Single(Assert.Single(page.Records).Fields!["unicode"]).CanonicalValue);
    }

    [Fact]
    public async Task Inspection_returns_exact_retained_and_lifetime_stream_metadata()
    {
        var store = CreateStore();
        var appended = await store.AppendAsync(Batch("inspect-seed",
            Record("record-1", ("sequence", [DiagnosticFieldValue.Int64(5)])),
            Record("record-2", ("sequence", [DiagnosticFieldValue.Int64(3)]))));

        var statistics = await store.InspectAsync(new(new("tenant-a", "shell-a"), new("logs")));

        Assert.Equal(2, statistics.RetainedCount.Value);
        Assert.Equal("2", statistics.MaxRetainedCursor.GetValueOrDefault().Value);
        Assert.Equal("2", statistics.LifetimeCommittedCursorHighWater.GetValueOrDefault().Value);
        Assert.Equal(DiagnosticFieldValue.Int64(5), statistics.LifetimeLogicalHighWater);
        Assert.Equal(statistics.LifetimeCommittedCursorHighWater, appended.CommittedCursorHighWater);
        Assert.Equal(statistics.LifetimeLogicalHighWater, appended.LogicalHighWater);
    }

    [Fact]
    public async Task Trim_retry_returns_original_exact_counts_and_preserves_lifetime_metadata()
    {
        var store = CreateStore();
        await store.AppendAsync(Batch("trim-seed",
            Record("record-1", ("sequence", [DiagnosticFieldValue.Int64(1)])),
            Record("record-2", ("sequence", [DiagnosticFieldValue.Int64(2)])),
            Record("record-3", ("sequence", [DiagnosticFieldValue.Int64(3)]))));
        var request = DiagnosticTrimRequest.Create(
            new("tenant-a", "shell-a"),
            new("logs"),
            new(TimeProvider.System.GetUtcNow(), "trim-1"),
            keepNewest: 1);

        var completed = await store.TrimAsync(request);
        var replayed = await store.TrimAsync(request);
        var remaining = await store.QueryAsync(new(request.Scope, request.Stream, 10));

        Assert.Equal(DiagnosticTrimStatus.Completed, completed.Status);
        Assert.Equal(DiagnosticTrimStatus.Replayed, replayed.Status);
        Assert.Equal(3, completed.ExaminedCount.Value);
        Assert.Equal(2, completed.DeletedCount.Value);
        Assert.Equal(completed with { Status = DiagnosticTrimStatus.Replayed }, replayed);
        Assert.Equal(1, completed.Statistics.RetainedCount.Value);
        Assert.Equal("3", completed.Statistics.LifetimeCommittedCursorHighWater.GetValueOrDefault().Value);
        Assert.Equal(DiagnosticFieldValue.Int64(3), completed.Statistics.LifetimeLogicalHighWater);
        Assert.Equal("record-3", Assert.Single(remaining.Records).RecordId);
    }

    [Fact]
    public async Task Trim_rejects_same_operation_id_with_a_different_boundary_without_mutation()
    {
        var store = CreateStore();
        await store.AppendAsync(Batch("trim-conflict-seed", "record-1", "record-2"));
        var first = DiagnosticTrimRequest.Create(new("tenant-a", "shell-a"), new("logs"), new(TimeProvider.System.GetUtcNow(), "trim-conflict"), 2);
        await store.TrimAsync(first);
        var conflicting = DiagnosticTrimRequest.Create(first.Scope, first.Stream, first.OperationId, 0);

        var exception = await Assert.ThrowsAsync<DiagnosticOperationConflictException>(async () =>
            await store.TrimAsync(conflicting));
        var statistics = await store.InspectAsync(new(first.Scope, first.Stream));

        Assert.Equal(DiagnosticOperationKind.Trim, exception.OperationKind);
        Assert.Equal(2, statistics.RetainedCount.Value);
    }

    [Fact]
    public async Task Restart_after_trim_recovers_lifetime_metadata_and_idempotency_ledgers()
    {
        var fixture = CreateFixture();
        var firstStore = OpenStore(fixture);
        var append = Batch("restart-append", Record("record-1", ("sequence", [DiagnosticFieldValue.Int64(9)])));
        var committed = await firstStore.AppendAsync(append);
        await firstStore.TrimAsync(DiagnosticTrimRequest.Create(
            append.Scope,
            append.Stream,
            new(TimeProvider.System.GetUtcNow(), "restart-trim"),
            keepNewest: 0));

        var restartedStore = OpenStore(fixture);
        var recovered = await restartedStore.InspectAsync(new(append.Scope, append.Stream));
        var replayed = await restartedStore.AppendAsync(append);
        var next = await restartedStore.AppendAsync(Batch("restart-next", "record-2"));

        Assert.Equal(0, recovered.RetainedCount.Value);
        Assert.Equal("1", recovered.LifetimeCommittedCursorHighWater.GetValueOrDefault().Value);
        Assert.Equal(DiagnosticFieldValue.Int64(9), recovered.LifetimeLogicalHighWater);
        Assert.Equal(DiagnosticAppendStatus.Replayed, replayed.Status);
        Assert.Equal(committed.Records, replayed.Records);
        Assert.Equal("2", Assert.Single(next.Records).Cursor.Value);
    }

    [Fact]
    public async Task Cancellation_during_append_before_commit_leaves_the_whole_batch_absent()
    {
        var fixture = CreateFixture();
        var store = OpenStore(fixture);

        await AssertCanceledAtAsync(fixture, DiagnosticExecutionPoint.AppendBeforeCommit, cancellationToken =>
            store.AppendAsync(Batch("canceled-batch", "record-1", "record-2"), cancellationToken).AsTask());
        var statistics = await store.InspectAsync(new(new("tenant-a", "shell-a"), new("logs")));
        var committed = await store.AppendAsync(Batch("after-cancellation", "record-3"));

        Assert.Equal(0, statistics.RetainedCount.Value);
        Assert.Equal("1", Assert.Single(committed.Records).Cursor.Value);
    }

    [Fact]
    public async Task Failure_after_a_record_is_staged_rolls_back_the_whole_batch_and_operation_ledger()
    {
        var fixture = CreateFixture();
        var batch = Batch("partial-write-failure", "record-1", "record-2");
        fixture.InterceptNext(
            DiagnosticExecutionPoint.AppendAfterRecordStagedBeforeCommit,
            _ => ValueTask.FromException(new IOException("Injected mid-batch failure.")));
        var store = OpenStore(fixture);

        await Assert.ThrowsAsync<IOException>(async () => await store.AppendAsync(batch));
        var restartedStore = OpenStore(fixture);
        var afterFailure = await restartedStore.InspectAsync(new(batch.Scope, batch.Stream));
        var retry = await restartedStore.AppendAsync(batch);

        Assert.Equal(0, afterFailure.RetainedCount.Value);
        Assert.Equal(DiagnosticAppendStatus.Committed, retry.Status);
        Assert.Equal(["1", "2"], retry.Records.Select(x => x.Cursor.Value));
    }

    [Fact]
    public async Task Append_acknowledgement_loss_is_queryable_and_retry_returns_original_outcome()
    {
        var fixture = CreateFixture();
        var request = Batch("lost-append-ack", "record-1", "record-2");
        fixture.InterceptNext(DiagnosticExecutionPoint.AppendAfterCommitBeforeAcknowledgement, _ =>
            ValueTask.FromException(new DiagnosticAcknowledgementLostException(DiagnosticOperationKind.Append, request.Stream, request.OperationId)));
        var store = OpenStore(fixture);

        var failure = await Assert.ThrowsAsync<DiagnosticAcknowledgementLostException>(async () =>
            await store.AppendAsync(request));
        var inspection = await store.InspectAsync(new(request.Scope, request.Stream));
        var replay = await store.AppendAsync(request);

        Assert.Equal(DiagnosticOperationKind.Append, failure.OperationKind);
        Assert.Equal(2, inspection.RetainedCount.Value);
        Assert.Equal(DiagnosticAppendStatus.Replayed, replay.Status);
        Assert.Equal(["1", "2"], replay.Records.Select(x => x.Cursor.Value));
    }

    [Fact]
    public async Task Trim_acknowledgement_loss_is_queryable_and_retry_returns_original_exact_counts()
    {
        var fixture = CreateFixture();
        var store = OpenStore(fixture);
        var append = Batch("lost-trim-ack-seed", "record-1", "record-2", "record-3");
        await store.AppendAsync(append);
        var request = DiagnosticTrimRequest.Create(
            append.Scope,
            append.Stream,
            new(TimeProvider.System.GetUtcNow(), "lost-trim-ack"),
            keepNewest: 1);
        fixture.InterceptNext(DiagnosticExecutionPoint.TrimAfterCommitBeforeAcknowledgement, _ =>
            ValueTask.FromException(new DiagnosticAcknowledgementLostException(DiagnosticOperationKind.Trim, request.Stream, request.OperationId)));

        await Assert.ThrowsAsync<DiagnosticAcknowledgementLostException>(async () =>
            await store.TrimAsync(request));
        var inspection = await store.InspectAsync(new(request.Scope, request.Stream));
        var replay = await store.TrimAsync(request);

        Assert.Equal(1, inspection.RetainedCount.Value);
        Assert.Equal(DiagnosticTrimStatus.Replayed, replay.Status);
        Assert.Equal(3, replay.ExaminedCount.Value);
        Assert.Equal(2, replay.DeletedCount.Value);
    }

    [Fact]
    public async Task Failure_after_a_record_is_staged_for_trim_rolls_back_records_statistics_and_operation_ledger()
    {
        var fixture = CreateFixture();
        var store = OpenStore(fixture);
        var occurredAt = DateTimeOffset.Parse("2026-07-12T12:00:01Z");
        var append = Batch(
            "trim-mid-transaction-seed",
            GroupedRecord("record-1", occurredAt, "old", 1, "old", "old"),
            GroupedRecord("record-2", occurredAt.AddSeconds(1), "new-a", 1, "new", "new-a"),
            GroupedRecord("record-3", occurredAt.AddSeconds(2), "new-b", 1, "new", "new-b"));
        await store.AppendAsync(append);
        var request = DiagnosticTrimRequest.Create(
            append.Scope,
            append.Stream,
            new(fixture.GetUtcNow(), "trim-mid-transaction-failure"),
            1);
        fixture.InterceptNext(
            DiagnosticExecutionPoint.TrimAfterRecordDeletedBeforeCommit,
            _ => ValueTask.FromException(new IOException("Injected mid-trim failure.")));

        await Assert.ThrowsAsync<IOException>(async () => await store.TrimAsync(request));
        var restarted = OpenStore(fixture);
        var afterFailure = await restarted.InspectAsync(new(request.Scope, request.Stream));
        var page = await restarted.QueryAsync(new(request.Scope, request.Stream, 10));
        DiagnosticRecordGroupPage? groups = null;
        if (restarted.Handlers.GroupedQuery.Capabilities.SupportsGroupedReduction)
        {
            groups = await restarted.QueryGroupsAsync(new(
                request.Scope,
                request.Stream,
                "service-summary",
                10,
                new("start"),
                InputRecordLimit: 2));
        }
        var retry = await restarted.TrimAsync(request);

        Assert.Equal(3, afterFailure.RetainedCount.Value);
        Assert.True(afterFailure.RetainedCount.Value > 2);
        Assert.Equal("3", afterFailure.MaxRetainedCursor.GetValueOrDefault().Value);
        Assert.Equal("3", afterFailure.LifetimeCommittedCursorHighWater.GetValueOrDefault().Value);
        Assert.Equal(["record-1", "record-2", "record-3"], page.Records.Select(x => x.RecordId));
        if (groups is not null)
            Assert.Equal(["new-a", "new-b"], groups.Groups.Select(group => group.GroupKey));
        Assert.Equal(DiagnosticTrimStatus.Completed, retry.Status);
        Assert.Equal(3, retry.ExaminedCount.Value);
        Assert.Equal(2, retry.DeletedCount.Value);
    }

    [Fact]
    public async Task Cancellation_after_a_record_is_staged_for_trim_rolls_back_across_restart_and_allows_retry()
    {
        var fixture = CreateFixture();
        var store = OpenStore(fixture);
        var append = Batch("trim-mid-cancellation-seed", "record-1", "record-2", "record-3");
        await store.AppendAsync(append);
        var request = DiagnosticTrimRequest.Create(
            append.Scope,
            append.Stream,
            new(fixture.GetUtcNow(), "trim-mid-transaction-cancellation"),
            1);

        await AssertCanceledAtAsync(
            fixture,
            DiagnosticExecutionPoint.TrimAfterRecordDeletedBeforeCommit,
            cancellationToken => store.TrimAsync(request, cancellationToken).AsTask());
        var restarted = OpenStore(fixture);
        var afterCancellation = await restarted.InspectAsync(new(request.Scope, request.Stream));
        var retry = await restarted.TrimAsync(request);

        Assert.Equal(3, afterCancellation.RetainedCount.Value);
        Assert.Equal("3", afterCancellation.MaxRetainedCursor.GetValueOrDefault().Value);
        Assert.Equal(DiagnosticTrimStatus.Completed, retry.Status);
        Assert.Equal(2, retry.DeletedCount.Value);
    }

    [Fact]
    public async Task Existing_record_identity_conflict_rejects_the_entire_batch_without_cursor_gaps()
    {
        var store = CreateStore();
        await store.AppendAsync(Batch("existing-seed", "record-1"));

        var exception = await Assert.ThrowsAsync<DiagnosticRecordValidationException>(async () =>
            await store.AppendAsync(Batch("existing-conflict", "record-2", "record-1")));
        var next = await store.AppendAsync(Batch("existing-after-conflict", "record-3"));
        var page = await store.QueryAsync(new(new("tenant-a", "shell-a"), new("logs"), 10));

        Assert.Contains(exception.Errors, x => x.Code == "append.record_id.exists");
        Assert.Equal("2", Assert.Single(next.Records).Cursor.Value);
        Assert.Equal(["record-1", "record-3"], page.Records.Select(x => x.RecordId));
    }

    [Fact]
    public async Task Concurrent_single_stream_batches_commit_atomically_with_unique_monotonic_cursors()
    {
        var fixture = CreateFixture();
        var tasks = Enumerable.Range(0, 50)
            .Select(index => OpenStore(fixture).AppendAsync(Batch(
                $"concurrent-{index}",
                $"record-{index}-a",
                $"record-{index}-b")).AsTask())
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var cursors = results.SelectMany(x => x.Records).Select(x => long.Parse(x.Cursor.Value)).Order().ToArray();
        var statistics = await OpenStore(fixture).InspectAsync(new(new("tenant-a", "shell-a"), new("logs")));

        Assert.Equal(Enumerable.Range(1, 100).Select(x => (long)x), cursors);
        Assert.All(results, x => Assert.Equal(2, x.Records.Count));
        Assert.Equal(100, statistics.RetainedCount.Value);
    }

    [Fact]
    public async Task Concurrent_same_operation_and_fingerprint_commit_once_then_replay_without_cursor_duplication()
    {
        var fixture = CreateFixture();
        var barrier = new AsyncConformanceBarrier(2);
        fixture.InterceptNext(DiagnosticExecutionPoint.AppendBeforeCommit, barrier.SignalAndWaitAsync);
        fixture.InterceptNext(DiagnosticExecutionPoint.AppendBeforeCommit, barrier.SignalAndWaitAsync);
        var batch = Batch("concurrent-identical-operation", "record-1", "record-2");

        var results = await Task.WhenAll(
            OpenStore(fixture).AppendAsync(batch).AsTask(),
            OpenStore(fixture).AppendAsync(batch).AsTask());
        var next = await OpenStore(fixture).AppendAsync(Batch("after-identical-race", "record-3"));
        var page = await OpenStore(fixture).QueryAsync(new(batch.Scope, batch.Stream, 10));

        Assert.Equal(1, results.Count(x => x.Status == DiagnosticAppendStatus.Committed));
        Assert.Equal(1, results.Count(x => x.Status == DiagnosticAppendStatus.Replayed));
        Assert.All(results, result => Assert.Equal(["1", "2"], result.Records.Select(x => x.Cursor.Value)));
        Assert.Equal("3", Assert.Single(next.Records).Cursor.Value);
        Assert.Equal(["record-1", "record-2", "record-3"], page.Records.Select(x => x.RecordId));
    }

    [Fact]
    public async Task Concurrent_same_operation_with_different_fingerprints_commits_once_and_conflicts_once()
    {
        var fixture = CreateFixture();
        var barrier = new AsyncConformanceBarrier(2);
        fixture.InterceptNext(DiagnosticExecutionPoint.AppendBeforeCommit, barrier.SignalAndWaitAsync);
        fixture.InterceptNext(DiagnosticExecutionPoint.AppendBeforeCommit, barrier.SignalAndWaitAsync);
        var operationId = new DiagnosticOperationId(fixture.GetUtcNow(), "concurrent-conflicting-operation");
        var first = DiagnosticRecordBatch.Create(
            new("tenant-a", "shell-a"),
            TestDefinition.Stream,
            operationId,
            [new("record-a", fixture.GetUtcNow(), "{}")]);
        var second = DiagnosticRecordBatch.Create(
            first.Scope,
            first.Stream,
            operationId,
            [new("record-b", fixture.GetUtcNow(), "{}")]);
        var tasks = new[]
        {
            OpenStore(fixture).AppendAsync(first).AsTask(),
            OpenStore(fixture).AppendAsync(second).AsTask()
        };

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (DiagnosticOperationConflictException)
        {
            // Assert the exact terminal task states below; either request may win the race.
        }
        var next = await OpenStore(fixture).AppendAsync(Batch("after-conflicting-race", "record-c"));
        var page = await OpenStore(fixture).QueryAsync(new(first.Scope, first.Stream, 10));

        var successful = Assert.Single(tasks.Where(x => x.IsCompletedSuccessfully));
        var conflict = Assert.Single(tasks.Where(x => x.IsFaulted));
        Assert.Equal(DiagnosticAppendStatus.Committed, (await successful).Status);
        Assert.IsType<DiagnosticOperationConflictException>(Assert.Single(conflict.Exception!.InnerExceptions));
        Assert.Equal("2", Assert.Single(next.Records).Cursor.Value);
        Assert.Equal(2, page.Records.Count);
        Assert.Contains(page.Records, x => x.RecordId is "record-a" or "record-b");
        Assert.Equal("record-c", page.Records[^1].RecordId);
    }

    [Fact]
    public async Task Concurrent_distinct_streams_isolate_records_operations_and_cursor_sequences()
    {
        var fixture = CreateFixture();
        var metricsDefinition = TestDefinition with
        {
            Stream = new("metrics"),
            LogicalStorageName = "diagnostic_metrics"
        };
        var logsStore = OpenStore(fixture);
        var metricsStore = new BoundedDiagnosticRecordStore(fixture.OpenStore(metricsDefinition));
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var operationId = new DiagnosticOperationId(fixture.GetUtcNow(), "shared-across-streams");
        var record = new DiagnosticRecordInput("shared-record", fixture.GetUtcNow(), "{}");
        var logsBatch = DiagnosticRecordBatch.Create(scope, TestDefinition.Stream, operationId, [record]);
        var metricsBatch = DiagnosticRecordBatch.Create(scope, metricsDefinition.Stream, operationId, [record]);

        var results = await Task.WhenAll(
            logsStore.AppendAsync(logsBatch).AsTask(),
            metricsStore.AppendAsync(metricsBatch).AsTask());
        var logs = await logsStore.QueryAsync(new(scope, TestDefinition.Stream, 10));
        var metrics = await metricsStore.QueryAsync(new(scope, metricsDefinition.Stream, 10));

        Assert.All(results, result => Assert.Equal("1", Assert.Single(result.Records).Cursor.Value));
        Assert.Equal("shared-record", Assert.Single(logs.Records).RecordId);
        Assert.Equal("shared-record", Assert.Single(metrics.Records).RecordId);
    }

    [Fact]
    public async Task Descending_cursor_continuation_excludes_concurrent_appends_from_the_snapshot()
    {
        var store = CreateStore();
        await store.AppendAsync(Batch("descending-seed", "record-1", "record-2", "record-3"));
        var query = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"),
            new("logs"),
            Limit: 2,
            Order: DiagnosticRecordOrder.CursorDescending,
            IncludeExactCount: true);

        var first = await store.QueryAsync(query);
        await store.AppendAsync(Batch("descending-concurrent", "record-4"));
        var second = await store.QueryAsync(query with { Continuation = first.Continuation });

        Assert.Equal(["record-3", "record-2"], first.Records.Select(x => x.RecordId));
        Assert.Equal(["record-1"], second.Records.Select(x => x.RecordId));
        Assert.Equal(3, second.ExactCount);
    }

    [Fact]
    public async Task Equal_field_values_are_tied_deterministically_by_cursor()
    {
        var store = CreateStore();
        await store.AppendAsync(Batch("order-ties",
            Record("record-1", ("sequence", [DiagnosticFieldValue.Int64(7)])),
            Record("record-2", ("sequence", [DiagnosticFieldValue.Int64(7)])),
            Record("record-3", ("sequence", [DiagnosticFieldValue.Int64(7)]))));

        var ascending = await store.QueryAsync(new(new("tenant-a", "shell-a"), new("logs"), 10, new("sequence")));
        var descending = await store.QueryAsync(new(new("tenant-a", "shell-a"), new("logs"), 10, new("sequence", DiagnosticSortDirection.Descending)));

        Assert.Equal(["record-1", "record-2", "record-3"], ascending.Records.Select(x => x.RecordId));
        Assert.Equal(["record-3", "record-2", "record-1"], descending.Records.Select(x => x.RecordId));
    }

    [Fact]
    public async Task Continuation_cannot_be_reused_with_a_different_query_shape()
    {
        var store = CreateStore();
        await store.AppendAsync(Batch("continuation-binding", "record-1", "record-2"));
        var query = new DiagnosticRecordQuery(new("tenant-a", "shell-a"), new("logs"), Limit: 1);
        var first = await store.QueryAsync(query);

        var exception = await Assert.ThrowsAsync<DiagnosticRecordValidationException>(async () =>
            await store.QueryAsync(query with
            {
                Order = DiagnosticRecordOrder.CursorDescending,
                Continuation = first.Continuation
            }));

        Assert.Contains(exception.Errors, x => x.Code == "query.continuation.query_mismatch");
    }

    [Fact]
    public async Task Any_predicate_supports_declared_scalar_and_multi_value_substring_semantics()
    {
        var store = CreateStore();
        await store.AppendAsync(Batch("contains-seed",
            Record("record-1", ("category", [DiagnosticFieldValue.String("workflow timeout")]), ("tags", [DiagnosticFieldValue.String("warning")])),
            Record("record-2", ("category", [DiagnosticFieldValue.String("normal")]), ("tags", [DiagnosticFieldValue.String("TIMEOUT-marker")])),
            Record("record-3", ("category", [DiagnosticFieldValue.String("normal")]), ("tags", [DiagnosticFieldValue.String("healthy")]))));
        var predicate = new DiagnosticRecordPredicate.Any(
        [
            DiagnosticRecordPredicate.Contains("category", "timeout"),
            DiagnosticRecordPredicate.Contains("tags", "timeout")
        ]);

        var page = await store.QueryAsync(new(new("tenant-a", "shell-a"), new("logs"), 10, Predicate: predicate));

        Assert.Equal(["record-1", "record-2"], page.Records.Select(x => x.RecordId));
    }

    [Fact]
    public async Task Trim_and_exact_statistics_are_isolated_to_the_explicit_tenant_scope()
    {
        var store = CreateStore();
        var tenantA = Batch("tenant-a-seed", "record-1", "record-2");
        var tenantB = DiagnosticRecordBatch.Create(
            new("tenant-b", "shell-a"),
            tenantA.Stream,
            new(TimeProvider.System.GetUtcNow(), "tenant-b-seed"),
            tenantA.Records);
        await store.AppendAsync(tenantA);
        await store.AppendAsync(tenantB);

        await store.TrimAsync(DiagnosticTrimRequest.Create(
            tenantA.Scope,
            tenantA.Stream,
            new(TimeProvider.System.GetUtcNow(), "tenant-a-trim"),
            0));
        var first = await store.InspectAsync(new(tenantA.Scope, tenantA.Stream));
        var second = await store.InspectAsync(new(tenantB.Scope, tenantB.Stream));

        Assert.Equal(0, first.RetainedCount.Value);
        Assert.Equal(2, second.RetainedCount.Value);
        Assert.Equal("2", second.LifetimeCommittedCursorHighWater.GetValueOrDefault().Value);
    }

    [Fact]
    public async Task Trim_rejects_operation_ids_outside_the_declared_idempotency_window()
    {
        var store = CreateStore();
        var request = DiagnosticTrimRequest.Create(
            new("tenant-a", "shell-a"),
            new("logs"),
            new(DateTimeOffset.UnixEpoch, "expired-trim"),
            0);

        var exception = await Assert.ThrowsAsync<DiagnosticOperationExpiredException>(async () =>
            await store.TrimAsync(request));

        Assert.Equal(DiagnosticOperationKind.Trim, exception.OperationKind);
    }

    [Fact]
    public async Task Trim_replay_expiry_is_measured_from_provider_commit_time_not_caller_issuance_time()
    {
        var fixture = CreateFixture();
        var store = OpenStore(fixture);
        var append = Batch("trim-receipt-seed", "record-1");
        await store.AppendAsync(append);
        var issuedNearWindowStart = fixture.GetUtcNow() - TestDefinition.TrimIdempotencyWindow + TimeSpan.FromMinutes(1);
        var request = DiagnosticTrimRequest.Create(
            append.Scope,
            append.Stream,
            new(issuedNearWindowStart, "provider-trim-receipt-window"),
            0);

        await store.TrimAsync(request);
        fixture.AdvanceTime(TimeSpan.FromMinutes(10));
        var replay = await store.TrimAsync(request);
        fixture.AdvanceTime(TestDefinition.TrimIdempotencyWindow - TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1));

        Assert.Equal(DiagnosticTrimStatus.Replayed, replay.Status);
        await Assert.ThrowsAsync<DiagnosticOperationExpiredException>(async () =>
            await store.TrimAsync(request));
    }

    [Fact]
    public async Task Expired_trim_identity_never_becomes_admissible_again_after_retries_cleanup_or_clock_regression()
    {
        var fixture = CreateFixture();
        var store = OpenStore(fixture);
        var append = Batch("permanent-trim-seed", "record-1");
        await store.AppendAsync(append);
        var committedAt = fixture.GetUtcNow();
        var request = DiagnosticTrimRequest.Create(
            append.Scope,
            append.Stream,
            new(committedAt + TestDefinition.MaxOperationClockSkew, "permanently-expired-trim"),
            0);
        await store.TrimAsync(request);

        fixture.AdvanceTime(TestDefinition.TrimIdempotencyWindow + TimeSpan.FromTicks(1));
        await Assert.ThrowsAsync<DiagnosticOperationExpiredException>(async () => await store.TrimAsync(request));
        await Assert.ThrowsAsync<DiagnosticOperationExpiredException>(async () => await store.TrimAsync(request));

        fixture.AdvanceTime(TestDefinition.MaxOperationClockSkew * 2 + TimeSpan.FromTicks(1));
        await Assert.ThrowsAsync<DiagnosticOperationExpiredException>(async () => await store.TrimAsync(request));
        await Assert.ThrowsAsync<DiagnosticOperationExpiredException>(async () => await store.TrimAsync(request));

        fixture.SetWallClock(committedAt);
        var restarted = OpenStore(fixture);
        await Assert.ThrowsAsync<DiagnosticOperationExpiredException>(async () => await restarted.TrimAsync(request));
        var statistics = await restarted.InspectAsync(new(request.Scope, request.Stream));

        Assert.Equal(0, statistics.RetainedCount.Value);
        Assert.Equal("1", statistics.LifetimeCommittedCursorHighWater.GetValueOrDefault().Value);
    }

    [Fact]
    public async Task Cancellation_is_observed_by_query_inspection_and_trim_without_mutation()
    {
        var fixture = CreateFixture();
        var store = OpenStore(fixture);
        var append = Batch("cancellation-seed", "record-1");
        await store.AppendAsync(append);
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.QueryAsync(new(append.Scope, append.Stream, 10), alreadyCanceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.InspectAsync(new(append.Scope, append.Stream), alreadyCanceled.Token));

        var trim = DiagnosticTrimRequest.Create(append.Scope, append.Stream, new(TimeProvider.System.GetUtcNow(), "canceled-trim"), 0);
        await AssertCanceledAtAsync(fixture, DiagnosticExecutionPoint.TrimBeforeCommit, cancellationToken =>
            store.TrimAsync(trim, cancellationToken).AsTask());
        var statistics = await store.InspectAsync(new(append.Scope, append.Stream));

        Assert.Equal(1, statistics.RetainedCount.Value);
    }

    [Theory]
    [InlineData(0, 3, 0)]
    [InlineData(1, 2, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(3, 0, 3)]
    [InlineData(4, 0, 3)]
    public async Task Keep_newest_boundaries_report_exact_deleted_and_retained_counts(
        int keepNewest,
        int expectedDeleted,
        int expectedRetained)
    {
        var store = CreateStore();
        var append = Batch($"boundary-seed-{keepNewest}", "record-1", "record-2", "record-3");
        await store.AppendAsync(append);

        var result = await store.TrimAsync(DiagnosticTrimRequest.Create(
            append.Scope,
            append.Stream,
            new(TimeProvider.System.GetUtcNow(), $"boundary-trim-{keepNewest}"),
            keepNewest));

        Assert.Equal(3, result.ExaminedCount.Value);
        Assert.Equal(expectedDeleted, result.DeletedCount.Value);
        Assert.Equal(expectedRetained, result.Statistics.RetainedCount.Value);
        Assert.Equal("3", result.Statistics.LifetimeCommittedCursorHighWater.GetValueOrDefault().Value);
    }

    [Fact]
    public async Task Occurrence_timestamp_range_includes_both_boundaries_and_orders_by_cursor_on_ties()
    {
        var store = CreateStore();
        var lower = DateTimeOffset.Parse("2026-07-12T12:00:00Z");
        var upper = lower.AddMinutes(1);
        await store.AppendAsync(Batch("time-range",
            Record("before") with { OccurredAt = lower.AddTicks(-1) },
            Record("lower-1") with { OccurredAt = lower },
            Record("lower-2") with { OccurredAt = lower },
            Record("upper") with { OccurredAt = upper },
            Record("after") with { OccurredAt = upper.AddTicks(1) }));

        var page = await store.QueryAsync(new(
            new("tenant-a", "shell-a"),
            new("logs"),
            Limit: 10,
            Order: new(DiagnosticRecordFieldNames.OccurredAt),
            Predicate: DiagnosticRecordPredicate.RangeInclusive(
                DiagnosticRecordFieldNames.OccurredAt,
                DiagnosticFieldValue.Timestamp(lower),
                DiagnosticFieldValue.Timestamp(upper))));

        Assert.Equal(["lower-1", "lower-2", "upper"], page.Records.Select(x => x.RecordId));
    }

    [Fact]
    public async Task Malformed_payload_is_rejected_before_any_cursor_is_allocated()
    {
        var store = CreateStore();
        var invalid = Batch("invalid-json", Record("record-1") with { Payload = "not-json" });

        var exception = await Assert.ThrowsAsync<DiagnosticRecordValidationException>(async () =>
            await store.AppendAsync(invalid));
        var committed = await store.AppendAsync(Batch("valid-after-json", "record-2"));

        Assert.Contains(exception.Errors, x => x.Code == "append.payload.invalid_json");
        Assert.Equal("1", Assert.Single(committed.Records).Cursor.Value);
    }

    [Fact]
    public async Task Field_order_excludes_missing_values_from_records_and_exact_count()
    {
        var store = CreateStore();
        await store.AppendAsync(Batch("missing-order",
            Record("with-sequence",
                ("sequence", [DiagnosticFieldValue.Int64(1)]),
                ("category", [DiagnosticFieldValue.String("\U00010000")])),
            Record("without-sequence", ("category", [DiagnosticFieldValue.String("\uE000")]))));

        var page = await store.QueryAsync(new(
            new("tenant-a", "shell-a"),
            new("logs"),
            10,
            Order: new("sequence"),
            IncludeExactCount: true));

        Assert.Equal("with-sequence", Assert.Single(page.Records).RecordId);
        Assert.Equal(1, page.ExactCount);

        var unicodeOrder = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"), new("logs"), 1, Order: new("category"));
        var first = await store.QueryAsync(unicodeOrder);
        var second = await store.QueryAsync(unicodeOrder with { Continuation = first.Continuation });
        var range = await store.QueryAsync(unicodeOrder with
        {
            Limit = 10,
            Continuation = null,
            Predicate = DiagnosticRecordPredicate.RangeInclusive(
                "category", DiagnosticFieldValue.String("\U00010000"), DiagnosticFieldValue.String("\uE000"))
        });

        Assert.Equal("with-sequence", Assert.Single(first.Records).RecordId);
        Assert.Equal("without-sequence", Assert.Single(second.Records).RecordId);
        Assert.Equal(["with-sequence", "without-sequence"], range.Records.Select(record => record.RecordId));
    }

    [Fact]
    public async Task Uninitialized_portable_field_value_is_rejected_as_structured_validation_error()
    {
        var store = CreateStore();
        var batch = Batch("default-field-value", Record("record-1", ("category", [default(DiagnosticFieldValue)])));

        var exception = await Assert.ThrowsAsync<DiagnosticRecordValidationException>(async () =>
            await store.AppendAsync(batch));

        Assert.Contains(exception.Errors, x => x.Code == "append.field.value_invalid");
    }

    [Fact]
    public async Task Grouped_reduction_preserves_exact_types_and_meaningful_fields_across_pages()
    {
        var fixture = CreateFixture();
        var store = OpenStore(fixture);
        if (!store.Handlers.GroupedQuery.Capabilities.SupportsGroupedReduction)
            return;
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var firstAt = DateTimeOffset.Parse("2026-07-12T12:00:01Z");
        await store.AppendAsync(DiagnosticRecordBatch.Create(
            scope,
            TestDefinition.Stream,
            new(fixture.GetUtcNow(), "typed-grouped-reduction"),
            [
                GroupedRecord("api-1", firstAt, "API", 1, null, "blue", "shared"),
                GroupedRecord("api-2", firstAt.AddSeconds(2), "api", 2, "later", "green", "shared"),
                GroupedRecord("worker-1", firstAt.AddSeconds(1), "worker", 3, "worker-root", "red")
            ]));
        var predicate = new DiagnosticRecordGroupPredicate.Comparison(
            "status",
            DiagnosticPredicateOperator.RangeInclusive,
            [DiagnosticFieldValue.Int64(2), DiagnosticFieldValue.Int64(3)]);

        var first = await store.QueryGroupsAsync(new(
            scope,
            TestDefinition.Stream,
            "service-summary",
            1,
            new("start"),
            InputRecordLimit: 100,
            Predicate: predicate));
        var api = Assert.Single(first.Groups);
        var apiStart = Assert.Single(api.Fields["start"]);
        var apiEnd = Assert.Single(api.Fields["end"]);
        var apiSpanCount = Assert.Single(api.Fields["spanCount"]);
        var apiStatus = Assert.Single(api.Fields["status"]);

        Assert.Equal("API", api.GroupKey);
        Assert.Equal(DiagnosticFieldType.Timestamp, apiStart.Type);
        Assert.Equal(firstAt, DateTimeOffset.Parse(apiStart.CanonicalValue));
        Assert.Equal(DiagnosticFieldType.Timestamp, apiEnd.Type);
        Assert.Equal(firstAt.AddSeconds(2), DateTimeOffset.Parse(apiEnd.CanonicalValue));
        Assert.Equal(DiagnosticFieldType.Int64, apiSpanCount.Type);
        Assert.Equal(DiagnosticFieldValue.Int64(3), apiSpanCount);
        Assert.Equal(
            ["blue", "green", "shared"],
            api.Fields["tags"].Select(value => value.CanonicalValue));
        Assert.All(api.Fields["tags"], value => Assert.Equal(DiagnosticFieldType.String, value.Type));
        Assert.Equal(DiagnosticFieldType.Int64, apiStatus.Type);
        Assert.Equal(DiagnosticFieldValue.Int64(2), apiStatus);
        Assert.Empty(api.Fields["rootName"]);
        Assert.NotNull(first.Continuation);

        var second = await store.QueryGroupsAsync(new(
            scope,
            TestDefinition.Stream,
            "service-summary",
            1,
            new("start"),
            InputRecordLimit: 100,
            Predicate: predicate,
            Continuation: first.Continuation));
        var worker = Assert.Single(second.Groups);
        var workerStart = Assert.Single(worker.Fields["start"]);
        var workerEnd = Assert.Single(worker.Fields["end"]);
        var workerSpanCount = Assert.Single(worker.Fields["spanCount"]);
        var workerTag = Assert.Single(worker.Fields["tags"]);
        var workerStatus = Assert.Single(worker.Fields["status"]);
        var workerRoot = Assert.Single(worker.Fields["rootName"]);

        Assert.Equal("worker", worker.GroupKey);
        Assert.Equal(DiagnosticFieldType.Timestamp, workerStart.Type);
        Assert.Equal(firstAt.AddSeconds(1), DateTimeOffset.Parse(workerStart.CanonicalValue));
        Assert.Equal(DiagnosticFieldType.Timestamp, workerEnd.Type);
        Assert.Equal(firstAt.AddSeconds(1), DateTimeOffset.Parse(workerEnd.CanonicalValue));
        Assert.Equal(DiagnosticFieldType.Int64, workerSpanCount.Type);
        Assert.Equal(DiagnosticFieldValue.Int64(3), workerSpanCount);
        Assert.Equal(DiagnosticFieldType.String, workerTag.Type);
        Assert.Equal("red", workerTag.CanonicalValue);
        Assert.Equal(DiagnosticFieldType.Int64, workerStatus.Type);
        Assert.Equal(DiagnosticFieldValue.Int64(3), workerStatus);
        Assert.Equal(DiagnosticFieldType.String, workerRoot.Type);
        Assert.Equal("worker-root", workerRoot.CanonicalValue);
        Assert.Null(second.Continuation);
    }

    [Fact]
    public async Task Grouped_continuation_uses_group_key_tie_break_for_equal_order_values()
    {
        var fixture = CreateFixture();
        var store = OpenStore(fixture);
        if (!store.Handlers.GroupedQuery.Capabilities.SupportsGroupedReduction)
            return;
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var occurredAt = DateTimeOffset.Parse("2026-07-12T12:00:01Z");
        await store.AppendAsync(DiagnosticRecordBatch.Create(
            scope,
            TestDefinition.Stream,
            new(fixture.GetUtcNow(), "equal-order-group-continuation"),
            [
                GroupedRecord("charlie", occurredAt, "charlie", 1, "root", "shared"),
                GroupedRecord("alpha", occurredAt, "alpha", 1, "root", "shared"),
                GroupedRecord("bravo", occurredAt, "bravo", 1, "root", "shared")
            ]));

        var groupKeys = new List<string>();
        DiagnosticRecordGroupContinuation? continuation = null;
        for (var pageIndex = 0; pageIndex < 3; pageIndex++)
        {
            var page = await store.QueryGroupsAsync(new(
                scope,
                TestDefinition.Stream,
                "service-summary",
                1,
                new("start"),
                Continuation: continuation,
                InputRecordLimit: 100));
            groupKeys.Add(Assert.Single(page.Groups).GroupKey);
            continuation = page.Continuation;
            if (pageIndex < 2)
                Assert.NotNull(continuation);
        }

        Assert.Equal(["alpha", "bravo", "charlie"], groupKeys);
        Assert.Equal(3, groupKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.Null(continuation);
    }

    [Fact]
    public async Task Trim_hook_observes_uncommitted_deletion_while_an_independent_store_sees_durable_records()
    {
        var fixture = CreateFixture();
        var store = OpenStore(fixture);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var stream = TestDefinition.Stream;
        await store.AppendAsync(DiagnosticRecordBatch.Create(
            scope,
            stream,
            new(fixture.GetUtcNow(), "staged-trim-seed"),
            [
                new("record-1", fixture.GetUtcNow(), "{}"),
                new("record-2", fixture.GetUtcNow(), "{}"),
                new("record-3", fixture.GetUtcNow(), "{}")
            ]));
        var trim = DiagnosticTrimRequest.Create(scope, stream, new(fixture.GetUtcNow(), "observe-staged-trim"), 1);
        fixture.InterceptNext(DiagnosticExecutionPoint.TrimAfterRecordDeletedBeforeCommit, async cancellationToken =>
        {
            var independent = new BoundedDiagnosticRecordStore(fixture.OpenIndependentStore(TestDefinition));
            var durablePage = await independent.QueryAsync(new(scope, stream, 10), cancellationToken);
            Assert.Equal(["record-1", "record-2", "record-3"], durablePage.Records.Select(record => record.RecordId));
            throw new IOException("Rollback the isolated trim transaction.");
        });

        await Assert.ThrowsAsync<IOException>(async () => await store.TrimAsync(trim));
        var afterRollback = await OpenStore(fixture).QueryAsync(new(scope, stream, 10));

        Assert.Equal(["record-1", "record-2", "record-3"], afterRollback.Records.Select(record => record.RecordId));
    }

    /// <summary>
    /// Runs an operation and cancels it exactly at <paramref name="point"/>. A bare timer cannot
    /// sequence this: on a loaded runner the operation may not reach the point before the timer
    /// fires, so cancellation lands before the staged work the test means to exercise, and the
    /// one-shot interceptor stays queued to poison the next operation. Waiting for the interceptor
    /// to signal that it has been entered both places the cancellation where it belongs and proves
    /// this operation consumed the interceptor, so no leftover can reach a later one.
    /// </summary>
    private static async Task AssertCanceledAtAsync(
        IDiagnosticRecordStoreConformanceFixture fixture,
        DiagnosticExecutionPoint point,
        Func<CancellationToken, Task> operation)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.InterceptNext(point, async cancellationToken =>
        {
            // A second entry means the queue stopped consuming interceptors, which is the drift
            // that would let this one reach a later operation. Fail there rather than leaving that
            // operation to block on the delay until the bounded store's timeout reports it as a
            // conformance failure of the store.
            if (!reached.TrySetResult())
                Assert.Fail($"The interceptor for {point} was entered twice; it is no longer single-consume.");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        using var cancellation = new CancellationTokenSource();
        var running = operation(cancellation.Token);

        // Reaching the point races the operation itself so that a store that fails or completes
        // without interception surfaces that outcome instead of the deadline expiring.
        if (await Task.WhenAny(reached.Task, running).WaitAsync(InterceptionMustFireTimeout) == running)
        {
            await running;
            Assert.Fail($"The operation completed without reaching {point}.");
        }

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    private static DiagnosticRecordBatch Batch(string operationNonce, params string[] recordIds)
    {
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var stream = new DiagnosticStreamId("logs");
        var records = recordIds.Select((id, index) => new DiagnosticRecordInput(
                id,
                DateTimeOffset.Parse("2026-07-12T12:00:01Z").AddSeconds(index),
                $"{{\"id\":\"{id}\"}}"))
                .ToArray();
        return DiagnosticRecordBatch.Create(
            scope,
            stream,
            new DiagnosticOperationId(TimeProvider.System.GetUtcNow(), operationNonce),
            records);
    }

    private static DiagnosticRecordBatch Batch(string operationNonce, params DiagnosticRecordInput[] records)
    {
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var stream = new DiagnosticStreamId("logs");
        return DiagnosticRecordBatch.Create(scope, stream, new(TimeProvider.System.GetUtcNow(), operationNonce), records);
    }

    private static DiagnosticRecordInput Record(
        string id,
        params (string Name, IReadOnlyList<DiagnosticFieldValue> Values)[] fields) =>
        new(
            id,
            DateTimeOffset.Parse("2026-07-12T12:00:01Z"),
            $"{{\"id\":\"{id}\"}}",
            fields.ToDictionary(x => x.Name, x => x.Values, StringComparer.Ordinal));

    private static DiagnosticRecordInput GroupedRecord(
        string id,
        DateTimeOffset occurredAt,
        string service,
        long sequence,
        string? category,
        params string[] tags)
    {
        var fields = new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>>(StringComparer.Ordinal)
        {
            ["service"] = [DiagnosticFieldValue.String(service)],
            ["sequence"] = [DiagnosticFieldValue.Int64(sequence)],
            ["tags"] = tags.Select(DiagnosticFieldValue.String).ToArray()
        };
        if (category is not null)
            fields.Add("category", [DiagnosticFieldValue.String(category)]);
        return new(id, occurredAt, "{}", fields);
    }
}
