using Groundwork.DiagnosticRecords;
using Xunit;

namespace Groundwork.DiagnosticRecords.Tests;

/// <summary>
/// Reusable relational-provider evidence layered over the provider-neutral behavioral suite.
/// SQL Server and PostgreSQL fixtures inherit this class unchanged when their dialects land.
/// </summary>
public abstract class RelationalDiagnosticRecordStoreConformanceTests : DiagnosticRecordStoreConformanceTests
{
    protected sealed override IDiagnosticRecordStoreConformanceFixture CreateFixture() => CreateRelationalFixture();

    protected abstract IRelationalDiagnosticRecordStoreConformanceFixture CreateRelationalFixture();

    [Fact]
    public async Task Grouped_reduction_plan_is_native_scoped_and_aggregated()
    {
        var fixture = CreateRelationalFixture();
        var plan = await fixture.ExplainGroupedQueryAsync(
            TestDefinition,
            new(
                new("tenant-a", "shell-a"),
                TestDefinition.Stream,
                "service-summary",
                10,
                new("start"),
                InputRecordLimit: 100));

        Assert.Equal(DiagnosticRecordPlanOperation.GroupedQuery, plan.Operation);
        Assert.True(
            fixture.HasNativeScopedGroupedReduction(plan),
            string.Join(Environment.NewLine, plan.RawPlans));
    }

    [Fact]
    public async Task Grouped_reduction_uses_the_newest_committed_raw_window_before_reduction_and_continuation()
    {
        var fixture = CreateRelationalFixture();
        var store = new BoundedDiagnosticRecordStore(fixture.OpenStore(TestDefinition));
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var occurredAt = DateTimeOffset.Parse("2026-07-12T12:00:01Z");
        await store.AppendAsync(DiagnosticRecordBatch.Create(
            scope,
            TestDefinition.Stream,
            new(fixture.GetUtcNow(), "newest-group-window-seed"),
            [
                GroupRecord("old-api", occurredAt, "old-api", 1, "old", "old-api"),
                GroupRecord("old-worker", occurredAt.AddSeconds(1), "old-worker", 1, "old", "old-worker"),
                GroupRecord("new-api", occurredAt.AddSeconds(2), "new-api", 1, "new", "new-api"),
                GroupRecord("new-worker", occurredAt.AddSeconds(3), "new-worker", 1, "new", "new-worker")
            ]));

        var query = new DiagnosticRecordGroupQuery(
            scope,
            TestDefinition.Stream,
            "service-summary",
            1,
            new("start"),
            InputRecordLimit: 2);
        var allNewest = await store.QueryGroupsAsync(query with { Take = 10 });

        Assert.Equal(["new-api", "new-worker"], allNewest.Groups.Select(group => group.GroupKey));
        Assert.Equal(4, (await store.InspectAsync(new(scope, TestDefinition.Stream))).RetainedCount.Value);

        var first = await store.QueryGroupsAsync(query);
        Assert.Equal("new-api", Assert.Single(first.Groups).GroupKey);
        Assert.NotNull(first.Continuation);
        await store.AppendAsync(DiagnosticRecordBatch.Create(
            scope,
            TestDefinition.Stream,
            new(fixture.GetUtcNow(), "newest-group-window-late-appends"),
            [
                GroupRecord("later", occurredAt.AddSeconds(4), "later", 1, "later", "later"),
                GroupRecord("backdated", occurredAt.AddSeconds(-1), "backdated", 1, "backdated", "backdated")
            ]));

        var second = await store.QueryGroupsAsync(query with { Continuation = first.Continuation });

        Assert.Equal("new-worker", Assert.Single(second.Groups).GroupKey);
        Assert.Null(second.Continuation);
        Assert.Equal(6, (await store.InspectAsync(new(scope, TestDefinition.Stream))).RetainedCount.Value);
    }

    [Fact]
    public async Task Grouped_set_union_fails_at_bound_plus_one()
    {
        var fixture = CreateRelationalFixture();
        var store = new BoundedDiagnosticRecordStore(fixture.OpenStore(TestDefinition));
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        await store.AppendAsync(DiagnosticRecordBatch.Create(
            scope,
            TestDefinition.Stream,
            new(fixture.GetUtcNow(), "grouped-union-overflow"),
            Enumerable.Range(0, 9)
                .Select(index => GroupRecord(
                    $"union-{index}",
                    DateTimeOffset.Parse("2026-07-12T12:00:01Z").AddSeconds(index),
                    "api",
                    1,
                    "root",
                    $"tag-{index}"))
                .ToArray()));

        var exception = await Assert.ThrowsAsync<DiagnosticRecordValidationException>(() =>
            store.QueryGroupsAsync(new(
                scope,
                TestDefinition.Stream,
                "service-summary",
                10,
                new("start"),
                InputRecordLimit: 100)).AsTask());

        Assert.Contains(exception.Errors, error => error.Code == "group_query.union.too_large");
    }

    [Fact]
    public async Task Grouped_union_predicates_probe_full_membership_before_page_scoped_overflow()
    {
        var fixture = CreateRelationalFixture();
        var store = new BoundedDiagnosticRecordStore(fixture.OpenStore(TestDefinition));
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var occurredAt = DateTimeOffset.Parse("2026-07-12T12:00:01Z");
        var records = new[]
        {
            GroupRecord("valid", occurredAt, "valid", 1, "root", "valid-marker")
        }.Concat(Enumerable.Range(0, 9)
            .Select(index => GroupRecord(
                $"nonselected-{index}",
                occurredAt.AddMinutes(1).AddSeconds(index),
                "nonselected-overflow",
                1,
                "root",
                $"nonselected-{index}")))
            .Concat(Enumerable.Range(0, 9)
                .Select(index => GroupRecord(
                    $"selected-{index}",
                    occurredAt.AddMinutes(2).AddSeconds(index),
                    "selected-overflow",
                    1,
                    "root",
                    $"selected-{index}")))
            .ToArray();
        await store.AppendAsync(DiagnosticRecordBatch.Create(
            scope,
            TestDefinition.Stream,
            new(fixture.GetUtcNow(), "grouped-predicate-over-bound-union"),
            records));

        var valid = await store.QueryGroupsAsync(new(
            scope,
            TestDefinition.Stream,
                "service-summary",
                1,
                new("start"),
                InputRecordLimit: 100,
                Predicate: new DiagnosticRecordGroupPredicate.Comparison(
                    "tags",
                    DiagnosticPredicateOperator.Contains,
                    [DiagnosticFieldValue.String("valid-marker")])));

        Assert.Equal("valid", Assert.Single(valid.Groups).GroupKey);

        var exception = await Assert.ThrowsAsync<DiagnosticRecordValidationException>(() =>
            store.QueryGroupsAsync(new(
                scope,
                TestDefinition.Stream,
                "service-summary",
                1,
                new("start"),
                InputRecordLimit: 100,
                Predicate: new DiagnosticRecordGroupPredicate.Comparison(
                    "tags",
                    DiagnosticPredicateOperator.Contains,
                    [DiagnosticFieldValue.String("selected-8")]))).AsTask());

        Assert.Contains(exception.Errors, error => error.Code == "group_query.union.too_large");
    }

    [Fact]
    public async Task Grouped_set_union_overflow_is_scoped_to_the_returned_page()
    {
        var fixture = CreateRelationalFixture();
        var store = new BoundedDiagnosticRecordStore(fixture.OpenStore(TestDefinition));
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var occurredAt = DateTimeOffset.Parse("2026-07-12T12:00:01Z");
        var records = new[]
        {
            GroupRecord("selected-valid", occurredAt, "a-valid", 1, "root", "shared")
        }.Concat(Enumerable.Range(0, 9)
            .Select(index => GroupRecord(
                $"later-overflow-{index}",
                occurredAt.AddMinutes(1).AddSeconds(index),
                "z-overflow",
                1,
                "root",
                $"tag-{index}")))
            .ToArray();
        await store.AppendAsync(DiagnosticRecordBatch.Create(
            scope,
            TestDefinition.Stream,
            new(fixture.GetUtcNow(), "grouped-page-scoped-union-overflow"),
            records));

        var first = await store.QueryGroupsAsync(new(
            scope,
            TestDefinition.Stream,
                "service-summary",
                1,
                new("start"),
                InputRecordLimit: 100));

        Assert.Equal("a-valid", Assert.Single(first.Groups).GroupKey);
        Assert.NotNull(first.Continuation);

        var exception = await Assert.ThrowsAsync<DiagnosticRecordValidationException>(() =>
            store.QueryGroupsAsync(new(
                scope,
                TestDefinition.Stream,
                "service-summary",
                1,
                new("start"),
                Continuation: first.Continuation,
                InputRecordLimit: 100)).AsTask());

        Assert.Contains(exception.Errors, error => error.Code == "group_query.union.too_large");
    }

    protected async Task QueryGroupedInt64SumOverflowAsync()
    {
        var fixture = CreateRelationalFixture();
        var store = new BoundedDiagnosticRecordStore(fixture.OpenStore(TestDefinition));
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var occurredAt = DateTimeOffset.Parse("2026-07-12T12:00:01Z");
        await store.AppendAsync(DiagnosticRecordBatch.Create(
            scope,
            TestDefinition.Stream,
            new(fixture.GetUtcNow(), "grouped-sum-overflow"),
            [
                GroupRecord("max", occurredAt, "api", long.MaxValue, "root", "shared"),
                GroupRecord("one", occurredAt.AddTicks(1), "api", 1, "later", "shared")
            ]));

        await store.QueryGroupsAsync(new(
            scope,
            TestDefinition.Stream,
            "service-summary",
            10,
            new("start"),
            InputRecordLimit: 100));
    }

    private static DiagnosticRecordInput GroupRecord(
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

    [Fact]
    public async Task Scoped_cursor_queries_use_the_scoped_cursor_access_path()
    {
        var fixture = CreateRelationalFixture();
        var query = new DiagnosticRecordQuery(new("tenant-a", "shell-a"), TestDefinition.Stream, 10);

        var plan = await fixture.ExplainQueryAsync(TestDefinition, query);

        Assert.True(fixture.UsesSeek(
            plan,
            "ix_groundwork_diagnostic_records_scope_cursor",
            ["tenant_id", "scope_id", "stream_id", "cursor"]), string.Join(Environment.NewLine, plan));
    }

    [Fact]
    public async Task Field_queries_use_the_scoped_field_access_path()
    {
        var fixture = CreateRelationalFixture();
        var query = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"),
            TestDefinition.Stream,
            10,
            Predicate: DiagnosticRecordPredicate.Equal("service", DiagnosticFieldValue.String("api")));

        var plan = await fixture.ExplainQueryAsync(TestDefinition, query);

        Assert.True(fixture.UsesSeek(
            plan,
            "ix_groundwork_diagnostic_fields_scope_value",
            ["tenant_id", "scope_id", "stream_id", "field_name", "field_type", "comparison_key_hash"]), string.Join(Environment.NewLine, plan));
    }

    [Fact]
    public async Task Long_unicode_equality_uses_the_bounded_hash_access_path()
    {
        var fixture = CreateRelationalFixture();
        var query = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"),
            TestDefinition.Stream,
            10,
            Predicate: DiagnosticRecordPredicate.Equal(
                "unicode",
                DiagnosticFieldValue.String(new string('Å', 32_766) + "😀")));

        var plan = await fixture.ExplainQueryAsync(TestDefinition, query);

        Assert.True(fixture.UsesSeek(
            plan,
            "ix_groundwork_diagnostic_fields_scope_value",
            ["tenant_id", "scope_id", "stream_id", "field_name", "field_type", "comparison_key_hash"]), string.Join(Environment.NewLine, plan));
    }

    [Fact]
    public async Task Contains_uses_a_server_native_scope_and_stream_bounded_field_scan()
    {
        var fixture = CreateRelationalFixture();
        var query = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"),
            TestDefinition.Stream,
            10,
            Predicate: DiagnosticRecordPredicate.Contains("unicode", "Å😀"));

        var plan = await fixture.ExplainQueryAsync(TestDefinition, query);

        var scopedFieldScan = fixture.UsesSeek(
                                  plan,
                                  "ix_groundwork_diagnostic_fields_scope_value",
                                  ["tenant_id", "scope_id", "stream_id", "field_name", "field_type"]) ||
                              fixture.UsesSeek(
                                  plan,
                                  "ix_groundwork_diagnostic_fields_scope_order",
                                  ["tenant_id", "scope_id", "stream_id", "field_name", "field_type"]) ||
                              fixture.UsesSeek(
                                  plan,
                                  fixture.FieldsPrimaryAccessPath,
                                  ["tenant_id", "scope_id", "stream_id", "cursor", "field_name"]);

        Assert.True(scopedFieldScan, string.Join(Environment.NewLine, plan));
    }

    [Fact]
    public async Task Latest_per_key_queries_use_the_scoped_latest_access_path()
    {
        var fixture = CreateRelationalFixture();
        var store = fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var records = Enumerable.Range(0, 100).Select(index => new DiagnosticRecordInput(
            $"latest-plan-{index}",
            fixture.GetUtcNow(),
            "{}",
            new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>>
            {
                ["service"] = [DiagnosticFieldValue.String($"service-{index % 10}")]
            })).ToArray();
        await store.AppendAsync(DiagnosticRecordBatch.Create(
            scope,
            TestDefinition.Stream,
            new(fixture.GetUtcNow(), "latest-plan-seed"),
            records));
        var query = new DiagnosticRecordQuery(
            scope,
            TestDefinition.Stream,
            10,
            LatestPerKeyField: "service");

        var plan = await fixture.ExplainQueryAsync(TestDefinition, query);

        var constraints = new[] { "tenant_id", "scope_id", "stream_id", "field_name", "field_type", "value_ordinal" };
        Assert.True(
            fixture.UsesSeek(plan, "ix_groundwork_diagnostic_fields_scope_latest", constraints) ||
            fixture.UsesSeek(plan, "ix_groundwork_diagnostic_fields_scope_order", constraints),
            string.Join(Environment.NewLine, plan));
    }

    [Fact]
    public async Task Keep_newest_trim_uses_the_scoped_cursor_access_path()
    {
        var fixture = CreateRelationalFixture();
        var request = DiagnosticTrimRequest.Create(
            new("tenant-a", "shell-a"),
            TestDefinition.Stream,
            new(fixture.GetUtcNow(), "explain-trim"),
            10);

        var plan = await fixture.ExplainTrimAsync(TestDefinition, request);

        Assert.True(fixture.UsesSeek(
            plan,
            "ix_groundwork_diagnostic_records_scope_cursor",
            ["tenant_id", "scope_id", "stream_id"]), string.Join(Environment.NewLine, plan));
    }

    [Fact]
    public async Task Ascii_ignore_case_comparison_keys_are_persisted_in_canonical_binary_form()
    {
        var fixture = CreateRelationalFixture();
        var store = fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        await store.AppendAsync(DiagnosticRecordBatch.Create(
            scope,
            TestDefinition.Stream,
            new(fixture.GetUtcNow(), "comparison-keys"),
            [new(
                "record-1",
                fixture.GetUtcNow(),
                "{}",
                new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>>
                {
                    ["service"] = [DiagnosticFieldValue.String("API-Z9")]
                })]));

        var keys = await fixture.ReadComparisonKeysAsync(scope, TestDefinition.Stream, "service");

        Assert.Equal(["api-z9"], keys);
    }

    [Fact]
    public async Task Expired_one_shot_operation_rows_are_cleaned_in_bounded_restart_safe_batches()
    {
        const int expiredOperations = 40;
        var fixture = CreateRelationalFixture();
        var store = fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var initialNow = fixture.GetUtcNow();
        for (var index = 0; index < expiredOperations; index++)
        {
            await store.AppendAsync(DiagnosticRecordBatch.Create(
                scope,
                TestDefinition.Stream,
                new(initialNow, $"cleanup-append-{index}"),
                [new($"cleanup-record-{index}", initialNow, "{}")]));
            await store.TrimAsync(DiagnosticTrimRequest.Create(
                scope,
                TestDefinition.Stream,
                new(initialNow, $"cleanup-trim-{index}"),
                expiredOperations));
        }

        fixture.AdvanceTime(TestDefinition.AppendIdempotencyWindow + TestDefinition.MaxOperationClockSkew + TimeSpan.FromTicks(1));
        var advancedNow = fixture.GetUtcNow();
        await store.AppendAsync(DiagnosticRecordBatch.Create(
            scope,
            TestDefinition.Stream,
            new(advancedNow, "cleanup-trigger-append"),
            [new("cleanup-trigger-record", advancedNow, "{}")]));

        Assert.Equal(9, await fixture.CountOperationRowsAsync(DiagnosticOperationKind.Append));
        Assert.Equal(8, await fixture.CountOperationRowsAsync(DiagnosticOperationKind.Trim));

        fixture.SetWallClock(initialNow);
        var restarted = fixture.OpenIndependentStore(TestDefinition);
        await restarted.TrimAsync(DiagnosticTrimRequest.Create(
            scope,
            TestDefinition.Stream,
            new(advancedNow, "cleanup-trigger-trim"),
            expiredOperations + 1));

        Assert.Equal(1, await fixture.CountOperationRowsAsync(DiagnosticOperationKind.Append));
        Assert.Equal(1, await fixture.CountOperationRowsAsync(DiagnosticOperationKind.Trim));
    }
}
