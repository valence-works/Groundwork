using System.Text.Json;
using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Groundwork.DiagnosticRecords.Tests;
using Groundwork.Provider.Relational;
using Groundwork.PostgreSql.DiagnosticRecords;
using Npgsql;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

[Collection(PostgreSqlDiagnosticRecordCollection.Name)]
public sealed class PostgreSqlDiagnosticRecordStoreTests(PostgreSqlDiagnosticContainer container) :
    ServerDiagnosticRecordStoreConformanceTests,
    IAsyncLifetime
{
    private string? fixtureConnectionString;
    private PostgreSqlDiagnosticRecordStoreFixture? fixture;

    public async Task InitializeAsync()
    {
        fixtureConnectionString = await container.CreateSchemaAsync();
        fixture = await PostgreSqlDiagnosticRecordStoreFixture.CreateAsync(fixtureConnectionString, TestDefinition);
    }

    public async Task DisposeAsync()
    {
        if (fixtureConnectionString is not null)
            await container.DropSchemaAsync(fixtureConnectionString);
    }

    protected override IServerDiagnosticRecordStoreConformanceFixture CreateServerFixture() =>
        fixture ?? throw new InvalidOperationException("The PostgreSQL diagnostic fixture has not been initialized.");

    [Fact]
    public async Task Grouped_sum_overflow_reaches_exact_int64_materialization()
    {
        var relationalFixture = CreateServerFixture();
        var store = relationalFixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var occurredAt = DateTimeOffset.Parse("2026-07-12T12:00:01Z");
        static DiagnosticRecordInput Record(string id, DateTimeOffset at, long sequence) =>
            new(
                id,
                at,
                "{}",
                new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>>(StringComparer.Ordinal)
                {
                    ["service"] = [DiagnosticFieldValue.String("api")],
                    ["sequence"] = [DiagnosticFieldValue.Int64(sequence)]
                });
        await store.AppendAsync(DiagnosticRecordBatch.Create(
            scope,
            TestDefinition.Stream,
            new(relationalFixture.GetUtcNow(), "postgresql-grouped-sum-overflow"),
            [
                Record("max", occurredAt, long.MaxValue),
                Record("one", occurredAt.AddTicks(1), 1)
            ]));

        await Assert.ThrowsAsync<OverflowException>(() =>
            store.QueryGroupsAsync(new(
                scope,
                TestDefinition.Stream,
                "service-summary",
                10,
                new("start"),
                InputRecordLimit: 100)).AsTask());
    }

    [Fact]
    public void Grouped_query_command_selects_the_newest_scoped_records_before_reduction()
    {
        var fixture = (PostgreSqlDiagnosticRecordStoreFixture)CreateServerFixture();
        var store = Assert.IsType<PostgreSqlDiagnosticRecordStore>(fixture.OpenStore(TestDefinition));
        var command = store.Inner.BuildGroupQueryCommand(
            new(
                new("tenant-a", "shell-a"),
                TestDefinition.Stream,
                "service-summary",
                10,
                new("start"),
                InputRecordLimit: 2),
            snapshotHighWater: 42);

        Assert.Contains("input_window AS", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("r.cursor <= @snapshot", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("ORDER BY r.cursor DESC", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("LIMIT @inputLimit", command.CommandText, StringComparison.Ordinal);
        Assert.Equal(2, command.Parameters["inputLimit"]);
    }

    [Fact]
    public async Task Materializer_uses_native_binary_text_and_all_durable_tables()
    {
        var fixture = (PostgreSqlDiagnosticRecordStoreFixture)CreateServerFixture();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT tablename FROM pg_tables WHERE schemaname = current_schema() AND tablename LIKE 'groundwork_diagnostic_%' ORDER BY tablename;";
        var names = new List<string>();
        await using (var reader = await tables.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                names.Add(reader.GetString(0));
        }
        await using var collation = connection.CreateCommand();
        collation.CommandText = "SELECT a.attcollation::regcollation::text FROM pg_attribute a WHERE a.attrelid = 'groundwork_diagnostic_fields'::regclass AND a.attname = 'comparison_key';";

        Assert.Equal(
            [
                "groundwork_diagnostic_append_operations",
                "groundwork_diagnostic_definitions",
                "groundwork_diagnostic_fields",
                "groundwork_diagnostic_provider_state",
                "groundwork_diagnostic_records",
                "groundwork_diagnostic_streams",
                "groundwork_diagnostic_trim_operations"
            ],
            names);
        Assert.Contains("C", Assert.IsType<string>(await collation.ExecuteScalarAsync()), StringComparison.Ordinal);
        await using var state = connection.CreateCommand();
        state.CommandText = $"SELECT algorithm_manifest FROM {RelationalDiagnosticRecordSchema.DefinitionsTable} WHERE stream_id = @stream;";
        state.Parameters.AddWithValue("stream", TestDefinition.Stream.Value);
        Assert.Contains(
            DiagnosticStringComparisonKey.UnicodeOrdinalIgnoreCaseAlgorithmId,
            Assert.IsType<string>(await state.ExecuteScalarAsync()),
            StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PostgreSqlDiagnosticRecordMaterializer.MaterializeAsync(
            fixture.ConnectionString,
            TestDefinition with { SchemaVersion = TestDefinition.SchemaVersion + 1 }));

        var direct = new PostgreSqlDiagnosticRecordStore(
            fixture.ConnectionString,
            TestDefinition with { SchemaVersion = TestDefinition.SchemaVersion + 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await direct.InspectAsync(new(new("tenant-a", "shell-a"), TestDefinition.Stream)));
    }
}

internal sealed class PostgreSqlDiagnosticRecordStoreFixture : IServerDiagnosticRecordStoreConformanceFixture
{
    private readonly RelationalSessionFactory sessions;
    private readonly ManualServerTimeProvider timeProvider = new(TimeProvider.System.GetUtcNow());
    private readonly Dictionary<DiagnosticExecutionPoint, Queue<Func<CancellationToken, ValueTask>>> interceptors = [];
    private readonly SemaphoreSlim planSeedGate = new(1, 1);
    private bool planSeeded;

    private PostgreSqlDiagnosticRecordStoreFixture(string connectionString)
    {
        ConnectionString = connectionString;
        sessions = RelationalSessionFactory.Concurrent(() => new NpgsqlConnection(connectionString));
    }

    public static async Task<PostgreSqlDiagnosticRecordStoreFixture> CreateAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        CancellationToken cancellationToken = default)
    {
        await PostgreSqlDiagnosticRecordMaterializer.MaterializeAsync(
            connectionString,
            definition,
            cancellationToken: cancellationToken);
        return new(connectionString);
    }

    public string ConnectionString { get; }
    public string FieldsPrimaryAccessPath => "groundwork_diagnostic_fields_pkey";

    public IDiagnosticRecordStore OpenStore(DiagnosticRecordStreamDefinition definition) =>
        new PostgreSqlDiagnosticRecordStore(sessions, definition, timeProvider, InterceptAsync);

    public IDiagnosticRecordStore OpenIndependentStore(DiagnosticRecordStreamDefinition definition) =>
        new PostgreSqlDiagnosticRecordStore(ConnectionString, definition, timeProvider, InterceptAsync);

    public int PendingInterceptorCount
    {
        get
        {
            lock (interceptors)
                return interceptors.Values.Sum(queue => queue.Count);
        }
    }

    public void InterceptNext(DiagnosticExecutionPoint point, Func<CancellationToken, ValueTask> interceptor)
    {
        lock (interceptors)
        {
            if (!interceptors.TryGetValue(point, out var queue))
                interceptors[point] = queue = [];
            queue.Enqueue(interceptor);
        }
    }

    public DateTimeOffset GetUtcNow() => timeProvider.GetUtcNow();
    public void AdvanceTime(TimeSpan duration) => timeProvider.Advance(duration);
    public void SetWallClock(DateTimeOffset utcNow) => timeProvider.Set(utcNow);

    public async ValueTask<DiagnosticRecordNativePlan> ExplainGroupedQueryAsync(
        DiagnosticRecordStreamDefinition definition,
        DiagnosticRecordGroupQuery query,
        CancellationToken cancellationToken = default)
    {
        await EnsurePlanSeedAsync(definition, cancellationToken);
        return await PostgreSqlDiagnosticRecordStoreFactory
            .CreatePlanInspector(ConnectionString)
            .InspectGroupedQueryAsync(
                DiagnosticRecordConformanceDeployment.Create(definition),
                query,
                cancellationToken);
    }

    public async ValueTask<IReadOnlyList<string>> ExplainQueryAsync(
        DiagnosticRecordStreamDefinition definition,
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        await EnsurePlanSeedAsync(definition, cancellationToken);
        return await PostgreSqlDiagnosticRecordStoreFactory.ExplainQueryAsync(ConnectionString, definition, query, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<string>> ExplainTrimAsync(
        DiagnosticRecordStreamDefinition definition,
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsurePlanSeedAsync(definition, cancellationToken);
        return await PostgreSqlDiagnosticRecordStoreFactory.ExplainTrimAsync(ConnectionString, definition, request, cancellationToken);
    }

    public bool UsesSeek(
        IReadOnlyList<string> plan,
        string accessPath,
        IReadOnlyList<string> constrainedColumns)
    {
        foreach (var json in plan)
        {
            using var document = JsonDocument.Parse(json);
            if (FindSeek(document.RootElement, accessPath, constrainedColumns))
                return true;
            if (accessPath == FieldsPrimaryAccessPath &&
                constrainedColumns.Contains("cursor", StringComparer.Ordinal) &&
                FindCursorBoundedFieldMerge(document.RootElement, accessPath, constrainedColumns))
            {
                return true;
            }
        }
        return false;
    }

    public bool HasNativeScopedGroupedReduction(DiagnosticRecordNativePlan plan) =>
        HasNativeScopedGroupedReductionPlan(plan);

    internal static bool HasNativeScopedGroupedReductionPlan(DiagnosticRecordNativePlan plan)
    {
        if (plan.Provider != "postgresql" ||
            plan.Operation != DiagnosticRecordPlanOperation.GroupedQuery ||
            plan.Format != DiagnosticRecordNativePlanFormats.PostgreSqlExplainJson)
        {
            return false;
        }

        foreach (var json in plan.RawPlans)
        {
            using var document = JsonDocument.Parse(json);
            if (FindScopedAggregate(document.RootElement, document.RootElement))
                return true;
        }

        return false;
    }

    public ValueTask<IReadOnlyList<string>> ReadComparisonKeysAsync(
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        string field,
        CancellationToken cancellationToken = default) =>
        PostgreSqlDiagnosticRecordStoreFactory.ReadComparisonKeysAsync(ConnectionString, scope, stream, field, cancellationToken);

    public ValueTask<long> CountOperationRowsAsync(
        DiagnosticOperationKind kind,
        CancellationToken cancellationToken = default) =>
        PostgreSqlDiagnosticRecordStoreFactory.CountOperationRowsAsync(ConnectionString, kind, cancellationToken);

    public async Task MaterializeConcurrentlyAsync(DiagnosticRecordStreamDefinition definition, int count)
    {
        var stores = await Task.WhenAll(Enumerable.Range(0, count).Select(_ =>
            PostgreSqlDiagnosticRecordStoreFactory.CreateAsync(ConnectionString, definition)));
        Assert.Equal(count, stores.Length);
    }

    public async Task AssertPoolPressureAsync(DiagnosticRecordStreamDefinition definition)
    {
        const int maxPoolSize = 2;
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = maxPoolSize };
        NpgsqlConnection.ClearPool(new NpgsqlConnection(builder.ConnectionString));
        var poolSaturated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStaged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedSessionRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var intercepted = 0;
        var created = 0;
        var store = await PostgreSqlDiagnosticRecordStoreFactory.CreateAsync(
            builder.ConnectionString,
            definition,
            timeProvider,
            async (point, cancellationToken) =>
            {
                if (point == RelationalDiagnosticRecordExecutionPoint.AppendAfterRecordStagedBeforeCommit &&
                    Interlocked.CompareExchange(ref intercepted, 1, 0) == 0)
                {
                    firstStaged.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
            },
            () =>
            {
                if (Interlocked.Increment(ref created) == maxPoolSize + 1)
                    queuedSessionRequested.TrySetResult();
                var connection = new NpgsqlConnection(builder.ConnectionString);
                connection.StateChange += (_, args) =>
                {
                    if (args.CurrentState == System.Data.ConnectionState.Open)
                    {
                        var current = Interlocked.Increment(ref active);
                        var observed = Volatile.Read(ref maximumActive);
                        while (current > observed)
                        {
                            var prior = Interlocked.CompareExchange(ref maximumActive, current, observed);
                            if (prior == observed)
                                break;
                            observed = prior;
                        }
                        if (current == maxPoolSize)
                            poolSaturated.TrySetResult();
                    }
                    else if (args.OriginalState == System.Data.ConnectionState.Open)
                        Interlocked.Decrement(ref active);
                };
                return connection;
            });
        var now = GetUtcNow();
        Task<DiagnosticAppendResult> Append(int index) => store.AppendAsync(DiagnosticRecordBatch.Create(
            new("tenant-a", "shell-a"),
            definition.Stream,
            new(now, $"pool-operation-{index}"),
            [new($"pool-record-{index}", now, "{}")])).AsTask();

        var first = Append(1);
        await firstStaged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Append(2);
        await poolSaturated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var third = Append(3);

        try
        {
            await RelationalSessionPoolPressure.AssertWaitsForProviderPoolSessionAsync(
                third,
                queuedSessionRequested.Task,
                () => Volatile.Read(ref maximumActive),
                maxPoolSize);
        }
        finally
        {
            releaseFirst.TrySetResult();
        }
        await Task.WhenAll(first, second, third);
    }

    private ValueTask InterceptAsync(RelationalDiagnosticRecordExecutionPoint point, CancellationToken cancellationToken)
    {
        var conformancePoint = Enum.Parse<DiagnosticExecutionPoint>(point.ToString());
        Func<CancellationToken, ValueTask>? interceptor = null;
        lock (interceptors)
        {
            if (interceptors.TryGetValue(conformancePoint, out var queue) && queue.Count > 0)
                interceptor = queue.Dequeue();
        }
        return interceptor?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
    }

    private async Task EnsurePlanSeedAsync(
        DiagnosticRecordStreamDefinition definition,
        CancellationToken cancellationToken)
    {
        if (planSeeded)
            return;
        await planSeedGate.WaitAsync(cancellationToken);
        try
        {
            if (planSeeded)
                return;
            var now = GetUtcNow();
            var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
            var store = OpenStore(definition);
            for (var batchIndex = 0; batchIndex < 4; batchIndex++)
            {
                var records = Enumerable.Range(batchIndex * 100, 100).Select(index => new DiagnosticRecordInput(
                    $"plan-record-{index}",
                    now.AddTicks(index),
                    "{}",
                    new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>>
                    {
                        ["service"] = [DiagnosticFieldValue.String($"service-{index % 20}")]
                    })).ToArray();
                await store.AppendAsync(DiagnosticRecordBatch.Create(
                    scope,
                    definition.Stream,
                    new(now, $"plan-seed-{batchIndex}"),
                    records),
                    cancellationToken);
            }
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var noise = connection.CreateCommand();
            noise.CommandText = $$"""
                INSERT INTO {{RelationalDiagnosticRecordSchema.RecordsTable}}
                    (tenant_id, scope_id, stream_id, cursor, record_id, occurred_at_ticks, payload_json)
                SELECT 'tenant-a', 'noise-scope-' || (n % 100), 'logs', ((n - 1) / 100) + 1, 'noise-record-' || n, n, '{}'
                FROM generate_series(1, 10000) AS n;
                INSERT INTO {{RelationalDiagnosticRecordSchema.FieldsTable}}
                    (tenant_id, scope_id, stream_id, cursor, field_name, value_ordinal, field_type, canonical_value, comparison_key, comparison_key_prefix, comparison_key_hash, search_key)
                SELECT 'tenant-a', 'noise-scope-' || (n % 100), 'logs', ((n - 1) / 100) + 1, 'service', 0, 0, 'bm9pc2U=', 'noise', 'noise', repeat('0', 64), '|006E|006F|0069|0073|0065'
                FROM generate_series(1, 10000) AS n;
                INSERT INTO {{RelationalDiagnosticRecordSchema.FieldsTable}}
                    (tenant_id, scope_id, stream_id, cursor, field_name, value_ordinal, field_type, canonical_value, comparison_key, comparison_key_prefix, comparison_key_hash, search_key)
                SELECT 'tenant-a', 'shell-a', 'logs', cursor_value, 'tags', value_ordinal, 0, 'dGFn', 'tag', 'tag', repeat('1', 64), '|0074|0061|0067'
                FROM generate_series(1, 500) AS cursor_value
                CROSS JOIN generate_series(0, 7) AS value_ordinal;
                ANALYZE {{RelationalDiagnosticRecordSchema.RecordsTable}};
                ANALYZE {{RelationalDiagnosticRecordSchema.FieldsTable}};
                """;
            await noise.ExecuteNonQueryAsync(cancellationToken);
            planSeeded = true;
        }
        finally
        {
            planSeedGate.Release();
        }
    }

    private static bool FindSeek(
        JsonElement element,
        string accessPath,
        IReadOnlyList<string> constrainedColumns)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var matchesIndex = element.TryGetProperty("Index Name", out var index) && index.GetString() == accessPath;
            var matchesNode = element.TryGetProperty("Node Type", out var node) && node.GetString()?.Contains("Index", StringComparison.Ordinal) == true;
            var condition = element.TryGetProperty("Index Cond", out var indexCondition) ? indexCondition.GetString() : null;
            var effectiveColumns = accessPath == "ix_groundwork_diagnostic_fields_scope_latest"
                ? constrainedColumns.Where(column => column != "value_ordinal")
                : constrainedColumns;
            if (matchesIndex && matchesNode && condition is not null && effectiveColumns.All(column => condition.Contains(column, StringComparison.Ordinal)))
                return true;
            foreach (var property in element.EnumerateObject())
            {
                if (FindSeek(property.Value, accessPath, constrainedColumns))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (FindSeek(item, accessPath, constrainedColumns))
                    return true;
            }
        }
        return false;
    }

    internal static bool FindCursorBoundedFieldMerge(
        JsonElement element,
        string fieldAccessPath,
        IReadOnlyList<string> constrainedColumns)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (IsExactCursorMerge(element, fieldAccessPath, constrainedColumns))
            {
                return true;
            }
            foreach (var property in element.EnumerateObject())
            {
                if (FindCursorBoundedFieldMerge(property.Value, fieldAccessPath, constrainedColumns))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (FindCursorBoundedFieldMerge(item, fieldAccessPath, constrainedColumns))
                    return true;
            }
        }
        return false;
    }

    private static bool IsExactCursorMerge(
        JsonElement element,
        string fieldAccessPath,
        IReadOnlyList<string> constrainedColumns)
    {
        if (!element.TryGetProperty("Node Type", out var node) ||
            node.GetString() != "Merge Join" ||
            !element.TryGetProperty("Merge Cond", out var condition) ||
            NormalizeMergeCondition(condition.GetString()) is not ("r.cursor=f.cursor" or "f.cursor=r.cursor") ||
            !element.TryGetProperty("Plans", out var plans) ||
            plans.ValueKind != JsonValueKind.Array ||
            plans.GetArrayLength() != 2)
        {
            return false;
        }

        return MatchesExactMergeArms(plans[0], plans[1], fieldAccessPath, constrainedColumns) ||
               MatchesExactMergeArms(plans[1], plans[0], fieldAccessPath, constrainedColumns);
    }

    private static bool MatchesExactMergeArms(
        JsonElement recordArm,
        JsonElement fieldArm,
        string fieldAccessPath,
        IReadOnlyList<string> constrainedColumns) =>
        TryResolveUnaryIndexArm(recordArm, out var recordSeek) &&
        MatchesIndexSeek(
            recordSeek,
            "ix_groundwork_diagnostic_records_scope_cursor",
            "r",
            ["tenant_id", "scope_id", "stream_id", "cursor"]) &&
        TryResolveUnaryIndexArm(fieldArm, out var fieldSeek) &&
        MatchesIndexSeek(
            fieldSeek,
            fieldAccessPath,
            "f",
            constrainedColumns.Where(column => column != "cursor").ToArray());

    private static bool TryResolveUnaryIndexArm(JsonElement element, out JsonElement indexSeek)
    {
        var current = element;
        while (current.ValueKind == JsonValueKind.Object)
        {
            if (current.TryGetProperty("Node Type", out var node) &&
                node.GetString() is "Index Scan" or "Index Only Scan")
            {
                indexSeek = current;
                return true;
            }
            if (!current.TryGetProperty("Node Type", out node) ||
                node.GetString() is not ("Sort" or "Incremental Sort" or "Materialize"))
            {
                break;
            }
            if (!current.TryGetProperty("Plans", out var plans) ||
                plans.ValueKind != JsonValueKind.Array ||
                plans.GetArrayLength() != 1)
            {
                break;
            }
            current = plans[0];
        }
        indexSeek = default;
        return false;
    }

    private static bool MatchesIndexSeek(
        JsonElement seek,
        string accessPath,
        string alias,
        IReadOnlyList<string> constrainedColumns)
    {
        if (!seek.TryGetProperty("Index Name", out var index) ||
            index.GetString() != accessPath ||
            !seek.TryGetProperty("Alias", out var observedAlias) ||
            observedAlias.GetString() != alias ||
            !seek.TryGetProperty("Index Cond", out var condition) ||
            condition.GetString() is not { } predicate)
        {
            return false;
        }

        return constrainedColumns.All(column => ContainsExactIdentifier(predicate, column));
    }

    private static bool ContainsExactIdentifier(string expression, string identifier)
    {
        for (var offset = 0; offset < expression.Length;)
        {
            var index = expression.IndexOf(identifier, offset, StringComparison.Ordinal);
            if (index < 0)
                return false;
            var before = index == 0 || !IsIdentifierCharacter(expression[index - 1]);
            var afterIndex = index + identifier.Length;
            var after = afterIndex == expression.Length || !IsIdentifierCharacter(expression[afterIndex]);
            if (before && after)
                return true;
            offset = index + identifier.Length;
        }
        return false;
    }

    private static bool FindScopedAggregate(JsonElement element, JsonElement planRoot)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("Node Type", out var nodeType) &&
                nodeType.GetString() == "Aggregate" &&
                ConsumesConnectedBoundedBaseRecords(element, planRoot))
            {
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (FindScopedAggregate(property.Value, planRoot))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (FindScopedAggregate(item, planRoot))
                    return true;
            }
        }

        return false;
    }

    private static bool ConsumesConnectedBoundedBaseRecords(
        JsonElement aggregate,
        JsonElement planRoot) =>
        ContainsCteScan(aggregate, "base_records") &&
        TryFindCteProducer(planRoot, "base_records", out var baseRecords) &&
        HasConnectedBoundedBaseRecordsJoin(baseRecords, planRoot);

    private static bool HasConnectedBoundedBaseRecordsJoin(
        JsonElement element,
        JsonElement planRoot)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("Node Type", out var nodeType) &&
                nodeType.GetString() is "Nested Loop" or "Hash Join" or "Merge Join" &&
                element.TryGetProperty("Plans", out var plans) &&
                plans.ValueKind == JsonValueKind.Array)
            {
                for (var inputIndex = 0; inputIndex < plans.GetArrayLength(); inputIndex++)
                {
                    if (!FindInPlanLineage(
                            plans[inputIndex],
                            planRoot,
                            IsBoundedScopedInputWindow,
                            []))
                    {
                        continue;
                    }

                    for (var fieldIndex = 0; fieldIndex < plans.GetArrayLength(); fieldIndex++)
                    {
                        if (fieldIndex != inputIndex &&
                            FindInPlanLineage(
                                plans[fieldIndex],
                                planRoot,
                                candidate => IsScopedRelationAccess(
                                    candidate,
                                    RelationalDiagnosticRecordSchema.FieldsTable),
                                []))
                        {
                            return true;
                        }
                    }
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (HasConnectedBoundedBaseRecordsJoin(property.Value, planRoot))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasConnectedBoundedBaseRecordsJoin(item, planRoot))
                    return true;
            }
        }

        return false;
    }

    private static bool ContainsCteScan(JsonElement element, string cteName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (HasPropertyValue(element, "Node Type", "CTE Scan") &&
                HasPropertyValue(element, "CTE Name", cteName))
            {
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (ContainsCteScan(property.Value, cteName))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsCteScan(item, cteName))
                    return true;
            }
        }

        return false;
    }

    private static bool IsBoundedScopedInputWindow(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("Node Type", out var nodeType) &&
        nodeType.GetString() == "Limit" &&
        ContainsNewestScopedRecordAccess(element);

    private static bool ContainsNewestScopedRecordAccess(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (IsScopedRelationAccess(element, RelationalDiagnosticRecordSchema.RecordsTable) &&
                HasPropertyValue(element, "Scan Direction", "Backward") &&
                HasCursorUpperBound(element))
            {
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (ContainsNewestScopedRecordAccess(property.Value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsNewestScopedRecordAccess(item))
                    return true;
            }
        }

        return false;
    }

    private static bool HasCursorUpperBound(JsonElement element) =>
        element.EnumerateObject().Any(property =>
            property.Name is "Index Cond" or "Recheck Cond" or "Filter" &&
            property.Value.ValueKind == JsonValueKind.String &&
            property.Value.GetString() is { } predicate &&
            ContainsExactIdentifier(predicate, "cursor") &&
            predicate.Contains("<=", StringComparison.Ordinal));

    private static bool FindInPlanLineage(
        JsonElement element,
        JsonElement planRoot,
        Func<JsonElement, bool> predicate,
        HashSet<string> visitedCtes)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (predicate(element))
                return true;

            if (element.TryGetProperty("CTE Name", out var cteNameElement) &&
                cteNameElement.GetString() is { } cteName &&
                visitedCtes.Add(cteName) &&
                TryFindCteProducer(planRoot, cteName, out var producer) &&
                FindInPlanLineage(producer, planRoot, predicate, visitedCtes))
            {
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (FindInPlanLineage(property.Value, planRoot, predicate, visitedCtes))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (FindInPlanLineage(item, planRoot, predicate, visitedCtes))
                    return true;
            }
        }

        return false;
    }

    private static bool TryFindCteProducer(
        JsonElement element,
        string cteName,
        out JsonElement producer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("Subplan Name", out var subplanName) &&
                subplanName.GetString() == $"CTE {cteName}")
            {
                producer = element;
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindCteProducer(property.Value, cteName, out producer))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindCteProducer(item, cteName, out producer))
                    return true;
            }
        }

        producer = default;
        return false;
    }

    private static bool HasPropertyValue(JsonElement element, string propertyName, string value) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        property.GetString() == value;

    private static bool IsScopedRelationAccess(JsonElement element, string relation) =>
        HasPropertyValue(element, "Relation Name", relation) &&
        HasPlanPredicateIdentifier(element, "tenant_id") &&
        HasPlanPredicateIdentifier(element, "scope_id") &&
        HasPlanPredicateIdentifier(element, "stream_id");

    private static bool HasPlanPredicateIdentifier(JsonElement element, string identifier)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is "Index Cond" or "Recheck Cond" or "Filter" or "Hash Cond" or "Merge Cond" or "Join Filter" &&
                property.Value.ValueKind == JsonValueKind.String &&
                ContainsExactIdentifier(property.Value.GetString()!, identifier))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIdentifierCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value == '_';

    private static string NormalizeMergeCondition(string? condition) =>
        condition is null
            ? string.Empty
            : new(condition.Where(character =>
                !char.IsWhiteSpace(character) && character is not '(' and not ')').ToArray());
}

public sealed class PostgreSqlDiagnosticPlanRecognizerTests
{
    private const string FieldAccessPath = "groundwork_diagnostic_fields_pkey";
    private static readonly string[] Constraints =
        ["tenant_id", "scope_id", "stream_id", "cursor", "field_name"];

    [Theory]
    [InlineData("(r.cursor = f.cursor)", false)]
    [InlineData("(f.cursor = r.cursor)", false)]
    [InlineData("(r.cursor = f.cursor)", true)]
    [InlineData("(f.cursor = r.cursor)", true)]
    public void Cursor_merge_accepts_both_exact_scoped_arm_layouts(string condition, bool swapArms)
    {
        var record = RecordSeek();
        var field = FieldSeek();
        using var plan = JsonDocument.Parse(MergePlan(
            condition,
            swapArms ? field : record,
            swapArms ? record : field));

        Assert.True(PostgreSqlDiagnosticRecordStoreFixture.FindCursorBoundedFieldMerge(
            plan.RootElement,
            FieldAccessPath,
            Constraints));
    }

    [Fact]
    public void Grouped_reduction_recognizer_accepts_scoped_inputs_inside_aggregate_subtree()
    {
        var plan = GroupedPlan(
            $$"""
              {
                "Node Type": "Sort",
                "Plans": [
                  {
                    "Node Type": "Limit",
                    "Subplan Name": "CTE input_window",
                    "Plans": [{{NewestRecordSeek()}}]
                  },
                  {
                    "Node Type": "Nested Loop",
                    "Subplan Name": "CTE base_records",
                    "Plans": [
                      {"Node Type": "CTE Scan", "CTE Name": "input_window"},
                      {{RelationSeek(RelationalDiagnosticRecordSchema.FieldsTable, "g")}}
                    ]
                  },
                  {
                    "Node Type": "Aggregate",
                    "Plans": [{"Node Type": "CTE Scan", "CTE Name": "base_records"}]
                  }
                ]
              }
              """);

        Assert.True(PostgreSqlDiagnosticRecordStoreFixture.HasNativeScopedGroupedReductionPlan(plan));
    }

    [Fact]
    public void Grouped_reduction_recognizer_resolves_only_the_cte_producer_consumed_by_the_aggregate()
    {
        var plan = GroupedPlan(
            $$"""
              {
                "Node Type": "Sort",
                "Plans": [
                  {
                    "Node Type": "Limit",
                    "Subplan Name": "CTE input_window",
                    "Plans": [{{NewestRecordSeek()}}]
                  },
                  {
                    "Node Type": "Nested Loop",
                    "Subplan Name": "CTE base_records",
                    "Plans": [
                      {"Node Type": "CTE Scan", "CTE Name": "input_window"},
                      {{RelationSeek(RelationalDiagnosticRecordSchema.FieldsTable, "g")}}
                    ]
                  },
                  {
                    "Node Type": "Aggregate",
                    "Plans": [
                      {"Node Type": "CTE Scan", "CTE Name": "base_records"}
                    ]
                  }
                ]
              }
              """);

        Assert.True(PostgreSqlDiagnosticRecordStoreFixture.HasNativeScopedGroupedReductionPlan(plan));
    }

    [Fact]
    public void Grouped_reduction_recognizer_rejects_disconnected_evidence()
    {
        var plan = GroupedPlan(
            $$"""
              {
                "Node Type": "Append",
                "Plans": [
                  {"Node Type": "Aggregate", "Plans": [{"Node Type": "Result"}]},
                  {{RelationSeek(RelationalDiagnosticRecordSchema.RecordsTable, "r")}},
                  {{RelationSeek(RelationalDiagnosticRecordSchema.FieldsTable, "f")}}
                ]
              }
              """);

        Assert.False(PostgreSqlDiagnosticRecordStoreFixture.HasNativeScopedGroupedReductionPlan(plan));
    }

    [Fact]
    public void Grouped_reduction_recognizer_rejects_an_unreferenced_scoped_cte_producer()
    {
        var plan = GroupedPlan(
            $$"""
              {
                "Node Type": "Sort",
                "Plans": [
                  {
                    "Node Type": "Nested Loop",
                    "Subplan Name": "CTE unrelated",
                    "Plans": [
                      {{RelationSeek(RelationalDiagnosticRecordSchema.RecordsTable, "r")}},
                      {{RelationSeek(RelationalDiagnosticRecordSchema.FieldsTable, "g")}}
                    ]
                  },
                  {
                    "Node Type": "Limit",
                    "Subplan Name": "CTE input_window",
                    "Plans": [{{RelationSeek(RelationalDiagnosticRecordSchema.RecordsTable, "r")}}]
                  },
                  {
                    "Node Type": "Aggregate",
                    "Plans": [
                      {"Node Type": "CTE Scan", "CTE Name": "base_records"}
                    ]
                  }
                ]
              }
              """);

        Assert.False(PostgreSqlDiagnosticRecordStoreFixture.HasNativeScopedGroupedReductionPlan(plan));
    }

    [Fact]
    public void Grouped_reduction_recognizer_rejects_all_required_fragments_when_the_bounded_input_does_not_feed_the_field_join()
    {
        var plan = GroupedPlan(
            $$"""
              {
                "Node Type": "Sort",
                "Plans": [
                  {
                    "Node Type": "Limit",
                    "Subplan Name": "CTE input_window",
                    "Plans": [{{NewestRecordSeek()}}]
                  },
                  {
                    "Node Type": "Append",
                    "Subplan Name": "CTE base_records",
                    "Plans": [
                      {"Node Type": "CTE Scan", "CTE Name": "input_window"},
                      {
                        "Node Type": "Nested Loop",
                        "Plans": [
                          {{RelationSeek(RelationalDiagnosticRecordSchema.RecordsTable, "r")}},
                          {{RelationSeek(RelationalDiagnosticRecordSchema.FieldsTable, "g")}}
                        ]
                      }
                    ]
                  },
                  {
                    "Node Type": "Aggregate",
                    "Plans": [{"Node Type": "CTE Scan", "CTE Name": "base_records"}]
                  }
                ]
              }
              """);

        Assert.False(PostgreSqlDiagnosticRecordStoreFixture.HasNativeScopedGroupedReductionPlan(plan));
    }

    [Fact]
    public void Grouped_reduction_recognizer_rejects_a_forward_input_scan_even_with_disconnected_backward_evidence()
    {
        var plan = GroupedPlan(
            $$"""
              {
                "Node Type": "Sort",
                "Plans": [
                  {
                    "Node Type": "Nested Loop",
                    "Subplan Name": "CTE base_records",
                    "Plans": [
                      {
                        "Node Type": "Limit",
                        "Plans": [{{NewestRecordSeek("Forward")}}]
                      },
                      {{RelationSeek(RelationalDiagnosticRecordSchema.FieldsTable, "g")}}
                    ]
                  },
                  {{NewestRecordSeek()}},
                  {
                    "Node Type": "Aggregate",
                    "Plans": [{"Node Type": "CTE Scan", "CTE Name": "base_records"}]
                  }
                ]
              }
              """);

        Assert.False(PostgreSqlDiagnosticRecordStoreFixture.HasNativeScopedGroupedReductionPlan(plan));
    }

    [Fact]
    public void Grouped_reduction_recognizer_rejects_scope_predicates_disconnected_from_a_relation_access()
    {
        var plan = GroupedPlan(
            $$"""
              {
                "Node Type": "Aggregate",
                "Plans": [
                  {"Node Type":"Seq Scan","Relation Name":"{{RelationalDiagnosticRecordSchema.RecordsTable}}","Alias":"r"},
                  {{RelationSeek(RelationalDiagnosticRecordSchema.FieldsTable, "f")}},
                  {"Node Type":"Result","Filter":"tenant_id = 'tenant-a' AND scope_id = 'shell-a' AND stream_id = 'logs'"}
                ]
              }
              """);

        Assert.False(PostgreSqlDiagnosticRecordStoreFixture.HasNativeScopedGroupedReductionPlan(plan));
    }

    [Theory]
    [MemberData(nameof(AdversarialMergePlans))]
    public void Cursor_merge_rejects_uncorrelated_or_inexact_evidence(string json)
    {
        using var plan = JsonDocument.Parse(json);

        Assert.False(PostgreSqlDiagnosticRecordStoreFixture.FindCursorBoundedFieldMerge(
            plan.RootElement,
            FieldAccessPath,
            Constraints));
    }

    public static TheoryData<string> AdversarialMergePlans() => new()
    {
        MergePlan("(r.cursor = unrelated.cursor)", RecordSeek(), FieldSeek()),
        MergePlan("(r.cursor = f.cursor)", RecordSeek(alias: "x"), FieldSeek()),
        MergePlan("(r.cursor = f.cursor)", RecordSeek(), FieldSeek(accessPath: "unrelated")),
        MergePlan(
            "(r.cursor = f.cursor)",
            $$"""{"Node Type":"Append","Plans":[{{RecordSeek()}},{{FieldSeek()}}]}""",
            """{"Node Type":"Index Scan","Index Name":"unrelated","Alias":"x","Index Cond":"(cursor = 1)"}"""),
        MergePlan(
            "(r.cursor = f.cursor)",
            $$"""{"Node Type":"Result","Plans":[{{RecordSeek()}}]}""",
            FieldSeek()),
        MergePlan(
            "(r.cursor = f.cursor)",
            RecordSeek("tenant_id_shadow", "scope_id_shadow", "stream_id_shadow", "cursor_shadow"),
            FieldSeek("tenant_id_shadow", "scope_id_shadow", "stream_id_shadow", "field_name_shadow"))
    };

    private static string MergePlan(string condition, string outer, string inner) =>
        $$"""
          {
            "Node Type": "Merge Join",
            "Merge Cond": "{{condition}}",
            "Plans": [{{outer}}, {{inner}}]
          }
          """;

    private static string RecordSeek(
        string tenant = "tenant_id",
        string scope = "scope_id",
        string stream = "stream_id",
        string cursor = "cursor",
        string alias = "r") =>
        $$"""
          {
            "Node Type": "Index Scan",
            "Index Name": "ix_groundwork_diagnostic_records_scope_cursor",
            "Alias": "{{alias}}",
            "Index Cond": "({{tenant}} = 'tenant-a' AND {{scope}} = 'shell-a' AND {{stream}} = 'logs' AND {{cursor}} <= 10)"
          }
          """;

    private static string FieldSeek(
        string tenant = "tenant_id",
        string scope = "scope_id",
        string stream = "stream_id",
        string field = "field_name",
        string accessPath = FieldAccessPath,
        string alias = "f") =>
        $$"""
          {
            "Node Type": "Index Scan",
            "Index Name": "{{accessPath}}",
            "Alias": "{{alias}}",
            "Index Cond": "({{tenant}} = 'tenant-a' AND {{scope}} = 'shell-a' AND {{stream}} = 'logs' AND {{field}} = 'unicode')"
          }
          """;

    private static DiagnosticRecordNativePlan GroupedPlan(string root) =>
        new(
            "postgresql",
            DiagnosticRecordPlanOperation.GroupedQuery,
            DiagnosticRecordNativePlanFormats.PostgreSqlExplainJson,
            [$$"""[{"Plan":{{root}}}]"""]);

    private static string RelationSeek(string relation, string alias) =>
        $$"""
          {
            "Node Type": "Index Scan",
            "Relation Name": "{{relation}}",
            "Alias": "{{alias}}",
            "Index Cond": "({{alias}}.tenant_id = 'tenant-a' AND {{alias}}.scope_id = 'shell-a' AND {{alias}}.stream_id = 'logs')"
          }
          """;

    private static string NewestRecordSeek(string scanDirection = "Backward") =>
        $$"""
          {
            "Node Type": "Index Scan",
            "Scan Direction": "{{scanDirection}}",
            "Relation Name": "{{RelationalDiagnosticRecordSchema.RecordsTable}}",
            "Alias": "r",
            "Index Cond": "(r.tenant_id = 'tenant-a' AND r.scope_id = 'shell-a' AND r.stream_id = 'logs' AND r.cursor <= 10)"
          }
          """;
}
