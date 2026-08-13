using System.Collections.Concurrent;
using System.Xml.Linq;
using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Groundwork.DiagnosticRecords.Tests;
using Groundwork.Provider.Relational;
using Groundwork.SqlServer.DiagnosticRecords;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

[Collection(SqlServerDiagnosticRecordCollection.Name)]
public sealed class SqlServerDiagnosticRecordStoreTests(SqlServerDiagnosticContainer container) :
    ServerDiagnosticRecordStoreConformanceTests,
    IAsyncLifetime
{
    private string? fixtureConnectionString;
    private SqlServerDiagnosticRecordStoreFixture? fixture;

    public async Task InitializeAsync()
    {
        fixtureConnectionString = await container.CreateDatabaseAsync();
        fixture = await SqlServerDiagnosticRecordStoreFixture.CreateAsync(fixtureConnectionString, TestDefinition);
    }

    public async Task DisposeAsync()
    {
        if (fixtureConnectionString is not null)
            await container.DropDatabaseAsync(fixtureConnectionString);
    }

    protected override IServerDiagnosticRecordStoreConformanceFixture CreateServerFixture() =>
        fixture ?? throw new InvalidOperationException("The SQL Server diagnostic fixture has not been initialized.");

    [Fact]
    public async Task Grouped_sum_overflow_reaches_exact_int64_materialization()
    {
        await Assert.ThrowsAsync<OverflowException>(QueryGroupedInt64SumOverflowAsync);
    }

    [Fact]
    public void Grouped_query_command_selects_the_newest_scoped_records_before_reduction()
    {
        var fixture = (SqlServerDiagnosticRecordStoreFixture)CreateServerFixture();
        var store = Assert.IsType<SqlServerDiagnosticRecordStore>(fixture.OpenStore(TestDefinition));
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
        Assert.Contains("SELECT TOP (@inputLimit)", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("r.[cursor] <= @snapshot", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("ORDER BY r.[cursor] DESC", command.CommandText, StringComparison.Ordinal);
        Assert.Equal(2, command.Parameters["inputLimit"]);
    }

    [Fact]
    public async Task Materializer_uses_native_binary_utf8_text_and_all_durable_tables()
    {
        var fixture = (SqlServerDiagnosticRecordStoreFixture)CreateServerFixture();
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sys.tables WHERE name LIKE 'groundwork_diagnostic_%' ORDER BY name;";
        var names = new List<string>();
        await using (var reader = await tables.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                names.Add(reader.GetString(0));
        }
        await using var collation = connection.CreateCommand();
        collation.CommandText = "SELECT collation_name FROM sys.columns WHERE object_id = OBJECT_ID(N'groundwork_diagnostic_fields') AND name = N'comparison_key';";

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
        Assert.Equal("Latin1_General_100_BIN2_UTF8", await collation.ExecuteScalarAsync());
        await using var state = connection.CreateCommand();
        state.CommandText = $"SELECT algorithm_manifest FROM {RelationalDiagnosticRecordSchema.DefinitionsTable} WHERE stream_id = @stream;";
        state.Parameters.AddWithValue("@stream", TestDefinition.Stream.Value);
        Assert.Contains(
            DiagnosticStringComparisonKey.UnicodeOrdinalIgnoreCaseAlgorithmId,
            Assert.IsType<string>(await state.ExecuteScalarAsync()),
            StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqlServerDiagnosticRecordMaterializer.MaterializeAsync(
            fixture.ConnectionString,
            TestDefinition with { SchemaVersion = TestDefinition.SchemaVersion + 1 }));

        var direct = new SqlServerDiagnosticRecordStore(
            fixture.ConnectionString,
            TestDefinition with { SchemaVersion = TestDefinition.SchemaVersion + 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await direct.InspectAsync(new(new("tenant-a", "shell-a"), TestDefinition.Stream)));

    }

    [Fact]
    public async Task Materializer_rejects_a_database_without_row_versioned_read_committed_isolation()
    {
        var connectionString = await container.CreateDatabaseAsync(enableReadCommittedSnapshot: false);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString));

            Assert.Contains("READ_COMMITTED_SNAPSHOT ON", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await container.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task Concurrent_database_lifecycles_are_isolated_and_leave_no_databases_behind()
    {
        const int workerCount = 4;
        const int iterationsPerWorker = 4;
        var databaseNames = new ConcurrentBag<string>();
        using var stressTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var workers = Enumerable.Range(0, workerCount).Select(async _ =>
        {
            try
            {
                for (var iteration = 0; iteration < iterationsPerWorker; iteration++)
                {
                    var connectionString = await container.CreateDatabaseAsync(cancellationToken: stressTimeout.Token);
                    var databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
                    databaseNames.Add(databaseName);
                    try
                    {
                        await using var connection = new SqlConnection(connectionString);
                        await connection.OpenAsync(stressTimeout.Token);
                        await using var command = connection.CreateCommand();
                        command.CommandText = """
                            SELECT DB_NAME(), is_read_committed_snapshot_on
                            FROM sys.databases
                            WHERE name = DB_NAME();
                            """;
                        await using var reader = await command.ExecuteReaderAsync(stressTimeout.Token);

                        Assert.True(await reader.ReadAsync(stressTimeout.Token));
                        Assert.Equal(databaseName, reader.GetString(0));
                        Assert.True(reader.GetBoolean(1));
                    }
                    finally
                    {
                        await container.DropDatabaseAsync(connectionString);
                    }
                }
            }
            catch
            {
                await stressTimeout.CancelAsync();
                throw;
            }
        }).ToArray();

        await Task.WhenAll(workers);

        Assert.Equal(workerCount * iterationsPerWorker, databaseNames.Distinct(StringComparer.Ordinal).Count());
        await using var master = new SqlConnection(container.ConnectionString);
        await master.OpenAsync(stressTimeout.Token);
        await using var leaked = master.CreateCommand();
        var parameters = databaseNames.Select((name, index) =>
        {
            var parameter = leaked.Parameters.AddWithValue($"@name{index}", name);
            return parameter.ParameterName;
        });
        leaked.CommandText = $"SELECT COUNT(*) FROM sys.databases WHERE name IN ({string.Join(", ", parameters)});";

        Assert.Equal(0, Convert.ToInt32(
            await leaked.ExecuteScalarAsync(stressTimeout.Token),
            System.Globalization.CultureInfo.InvariantCulture));
    }
}

internal sealed class SqlServerDiagnosticRecordStoreFixture : IServerDiagnosticRecordStoreConformanceFixture
{
    private readonly RelationalSessionFactory sessions;
    private readonly ManualServerTimeProvider timeProvider = new(TimeProvider.System.GetUtcNow());
    private readonly Dictionary<DiagnosticExecutionPoint, Queue<Func<CancellationToken, ValueTask>>> interceptors = [];
    private readonly SemaphoreSlim planSeedGate = new(1, 1);
    private bool planSeeded;
    private bool latestPlanNoiseSeeded;

    private SqlServerDiagnosticRecordStoreFixture(string connectionString)
    {
        ConnectionString = connectionString;
        sessions = RelationalSessionFactory.Concurrent(() => new SqlConnection(connectionString));
    }

    public static async Task<SqlServerDiagnosticRecordStoreFixture> CreateAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        CancellationToken cancellationToken = default)
    {
        await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(
            connectionString,
            definition,
            cancellationToken: cancellationToken);
        return new(connectionString);
    }

    public string ConnectionString { get; }
    public string FieldsPrimaryAccessPath => "pk_groundwork_diagnostic_fields";

    public IDiagnosticRecordStore OpenStore(DiagnosticRecordStreamDefinition definition) =>
        new SqlServerDiagnosticRecordStore(sessions, definition, timeProvider, InterceptAsync);

    // Independent stores use their own session factory but share the fixture's interception
    // pipeline so conformance tests can observe their execution points.
    public IDiagnosticRecordStore OpenIndependentStore(DiagnosticRecordStreamDefinition definition) =>
        new SqlServerDiagnosticRecordStore(
            RelationalSessionFactory.Concurrent(() => new SqlConnection(ConnectionString)),
            definition,
            timeProvider,
            InterceptAsync,
            (snapshot, cancellationToken) => SqlServerDiagnosticRecordMaterializer.AdmitAsync(
                ConnectionString,
                snapshot,
                cancellationToken));

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
        return await SqlServerDiagnosticRecordStoreFactory
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
        if (query.LatestPerKeyField is not null)
            await EnsureLatestPlanNoiseAsync(cancellationToken);
        return await SqlServerDiagnosticRecordStoreFactory.ExplainQueryAsync(ConnectionString, definition, query, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<string>> ExplainTrimAsync(
        DiagnosticRecordStreamDefinition definition,
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsurePlanSeedAsync(definition, cancellationToken);
        return await SqlServerDiagnosticRecordStoreFactory.ExplainTrimAsync(ConnectionString, definition, request, cancellationToken);
    }

    public bool UsesSeek(
        IReadOnlyList<string> plan,
        string accessPath,
        IReadOnlyList<string> constrainedColumns)
    {
        var acceptablePaths = accessPath == "ix_groundwork_diagnostic_records_scope_cursor"
            ? new[] { accessPath, "pk_groundwork_diagnostic_records" }
            : [accessPath];
        foreach (var xml in plan)
        {
            var document = XDocument.Parse(xml);
            var ns = document.Root!.Name.Namespace;
            foreach (var operation in document.Descendants(ns + "RelOp")
                         .Where(node => node.Attribute("PhysicalOp")?.Value.Contains("Seek", StringComparison.OrdinalIgnoreCase) == true))
            {
                var index = operation.Descendants(ns + "IndexScan").Descendants(ns + "Object")
                    .FirstOrDefault(node => acceptablePaths.Any(path =>
                        node.Attribute("Index")?.Value.Contains(path, StringComparison.Ordinal) == true));
                if (index is null)
                    continue;
                var seekColumns = operation.Descendants(ns + "SeekPredicates")
                    .Descendants(ns + "ColumnReference")
                    .Select(node => node.Attribute("Column")?.Value.Trim('[', ']'))
                    .Where(name => name is not null)
                    .ToHashSet(StringComparer.Ordinal);
                var effectiveColumns = accessPath == "ix_groundwork_diagnostic_fields_scope_latest"
                    ? constrainedColumns.Where(column => column != "value_ordinal")
                    : constrainedColumns;
                if (effectiveColumns.All(seekColumns.Contains))
                    return true;
            }
        }
        return false;
    }

    public bool HasNativeScopedGroupedReduction(DiagnosticRecordNativePlan plan) =>
        HasNativeScopedGroupedReductionPlan(plan);

    internal static bool HasNativeScopedGroupedReductionPlan(DiagnosticRecordNativePlan plan)
    {
        if (plan.Provider != "sqlserver" ||
            plan.Operation != DiagnosticRecordPlanOperation.GroupedQuery ||
            plan.Format != DiagnosticRecordNativePlanFormats.SqlServerShowplanXml)
        {
            return false;
        }

        foreach (var xml in plan.RawPlans)
        {
            var document = XDocument.Parse(xml);
            var ns = document.Root!.Name.Namespace;
            foreach (var aggregate in document.Descendants(ns + "RelOp").Where(operation =>
                operation.Attribute("LogicalOp")?.Value == "Aggregate" ||
                operation.Attribute("PhysicalOp")?.Value.Contains(
                    "Aggregate",
                    StringComparison.Ordinal) == true))
            {
                if (HasConnectedBoundedReduction(aggregate, ns))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasConnectedBoundedReduction(XElement aggregate, XNamespace ns) =>
        aggregate.Descendants(ns + "RelOp")
            .Where(operation =>
                operation.Attribute("LogicalOp")?.Value.Contains("Join", StringComparison.Ordinal) == true ||
                operation.Attribute("PhysicalOp")?.Value.Contains("Join", StringComparison.Ordinal) == true ||
                operation.Attribute("PhysicalOp")?.Value.Contains("Loops", StringComparison.Ordinal) == true)
            .Any(join =>
            {
                var inputs = DirectRelOpInputs(join, ns).ToArray();
                for (var inputIndex = 0; inputIndex < inputs.Length; inputIndex++)
                {
                    if (!HasBoundedScopedInputWindow(inputs[inputIndex], ns))
                        continue;
                    for (var fieldIndex = 0; fieldIndex < inputs.Length; fieldIndex++)
                    {
                        if (fieldIndex != inputIndex &&
                            HasScopedTableAccess(
                                inputs[fieldIndex],
                                ns,
                                RelationalDiagnosticRecordSchema.FieldsTable))
                        {
                            return true;
                        }
                    }
                }
                return false;
            });

    private static IEnumerable<XElement> DirectRelOpInputs(XElement operation, XNamespace ns) =>
        operation.Elements().SelectMany(element => element.Elements(ns + "RelOp"));

    private static bool HasBoundedScopedInputWindow(XElement input, XNamespace ns) =>
        input.DescendantsAndSelf(ns + "RelOp").Any(operation =>
            operation.Attribute("LogicalOp")?.Value == "Top" &&
            operation.Descendants(ns + "TopExpression").DescendantsAndSelf().Attributes().Any(attribute =>
                attribute.Value.Contains("@inputLimit", StringComparison.Ordinal)) &&
            HasNewestScopedSnapshotRecordAccess(operation, ns));

    private static bool HasNewestScopedSnapshotRecordAccess(XElement operation, XNamespace ns) =>
        operation.DescendantsAndSelf(ns + "RelOp").Any(access =>
            HasScopedTableAccess(access, ns, RelationalDiagnosticRecordSchema.RecordsTable) &&
            access.Descendants(ns + "IndexScan").Any(scan =>
                scan.Attribute("ScanDirection")?.Value == "BACKWARD") &&
            access.Descendants()
                .Where(node => node.Name == ns + "SeekPredicates" || node.Name == ns + "Predicate")
                .Any(predicate =>
                {
                    var columns = predicate.Descendants(ns + "ColumnReference")
                        .Select(column => NormalizeSqlIdentifier(column.Attribute("Column")?.Value))
                        .ToHashSet(StringComparer.Ordinal);
                    return columns.Contains("cursor") && columns.Contains("@snapshot");
                }));

    private static bool HasScopedTableAccess(XElement aggregate, XNamespace ns, string table) =>
        aggregate.DescendantsAndSelf(ns + "RelOp").Any(operation =>
        {
            var hasTable = operation.Descendants(ns + "Object")
                .Any(node => NormalizeSqlIdentifier(node.Attribute("Table")?.Value) == table);
            if (!hasTable)
                return false;
            var scopedPredicateColumns = operation
                .Descendants()
                .Where(node => node.Name == ns + "SeekPredicates" || node.Name == ns + "Predicate")
                .SelectMany(node => node.Descendants(ns + "ColumnReference"))
                .Select(node => NormalizeSqlIdentifier(node.Attribute("Column")?.Value))
                .ToHashSet(StringComparer.Ordinal);
            return scopedPredicateColumns.IsSupersetOf(["tenant_id", "scope_id", "stream_id"]);
        });

    public ValueTask<IReadOnlyList<string>> ReadComparisonKeysAsync(
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        string field,
        CancellationToken cancellationToken = default) =>
        SqlServerDiagnosticRecordStoreFactory.ReadComparisonKeysAsync(ConnectionString, scope, stream, field, cancellationToken);

    public ValueTask<long> CountOperationRowsAsync(
        DiagnosticOperationKind kind,
        CancellationToken cancellationToken = default) =>
        SqlServerDiagnosticRecordStoreFactory.CountOperationRowsAsync(ConnectionString, kind, cancellationToken);

    public async Task MaterializeConcurrentlyAsync(DiagnosticRecordStreamDefinition definition, int count)
    {
        var stores = await Task.WhenAll(Enumerable.Range(0, count).Select(_ =>
            SqlServerDiagnosticRecordStoreFactory.CreateAsync(ConnectionString, definition)));
        Assert.Equal(count, stores.Length);
    }

    public async Task AssertPoolPressureAsync(DiagnosticRecordStreamDefinition definition)
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 2 };
        SqlConnection.ClearPool(new SqlConnection(builder.ConnectionString));
        var openedTwice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var intercepted = 0;
        var store = await SqlServerDiagnosticRecordStoreFactory.CreateAsync(
            builder.ConnectionString,
            definition,
            timeProvider,
            async (point, cancellationToken) =>
            {
                if (point == RelationalDiagnosticRecordExecutionPoint.AppendAfterRecordStagedBeforeCommit &&
                    Interlocked.CompareExchange(ref intercepted, 1, 0) == 0)
                    await releaseFirst.Task.WaitAsync(cancellationToken);
            },
            () =>
            {
                var connection = new SqlConnection(builder.ConnectionString);
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
                        if (current == 2)
                            openedTwice.TrySetResult();
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
        while (Volatile.Read(ref intercepted) == 0)
            await Task.Delay(10).WaitAsync(TimeSpan.FromSeconds(10));
        var second = Append(2);
        await openedTwice.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var third = Append(3);
        await Task.Delay(200);

        try
        {
            Assert.Equal(2, Volatile.Read(ref maximumActive));
            Assert.False(third.IsCompleted);
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
            planSeeded = true;
        }
        finally
        {
            planSeedGate.Release();
        }
    }

    private async Task EnsureLatestPlanNoiseAsync(CancellationToken cancellationToken)
    {
        if (latestPlanNoiseSeeded)
            return;
        await planSeedGate.WaitAsync(cancellationToken);
        try
        {
            if (latestPlanNoiseSeeded)
                return;
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO {RelationalDiagnosticRecordSchema.FieldsTable}
                    (tenant_id, scope_id, stream_id, [cursor], field_name, value_ordinal, field_type, canonical_value, comparison_key, comparison_key_prefix, comparison_key_hash, search_key)
                SELECT 'noise-tenant', CONCAT('noise-scope-', n % 100), 'logs', n, 'service', 0, 0, 'bm9pc2U=', 'noise', 'noise', REPLICATE('0', 64), '|006E|006F|0069|0073|0065'
                FROM (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(digit)
                CROSS JOIN (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(digit)
                CROSS JOIN (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(digit)
                CROSS JOIN (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) d(digit)
                CROSS APPLY (VALUES (a.digit + 10 * b.digit + 100 * c.digit + 1000 * d.digit + 1)) number(n);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            latestPlanNoiseSeeded = true;
        }
        finally
        {
            planSeedGate.Release();
        }
    }

    private static string? NormalizeSqlIdentifier(string? identifier) =>
        identifier?.Trim().Trim('[', ']');
}

public sealed class SqlServerDiagnosticPlanRecognizerTests
{
    [Fact]
    public void Grouped_reduction_recognizer_accepts_scoped_inputs_inside_aggregate_subtree()
    {
        var plan = GroupedPlan(
            $"""
             <RelOp LogicalOp="Aggregate" PhysicalOp="Stream Aggregate">
               <StreamAggregate>
                 {ScopedInputs()}
               </StreamAggregate>
             </RelOp>
             """);

        Assert.True(SqlServerDiagnosticRecordStoreFixture.HasNativeScopedGroupedReductionPlan(plan));
    }

    [Fact]
    public void Grouped_reduction_recognizer_rejects_disconnected_evidence()
    {
        var plan = GroupedPlan(
            $"""
             <RelOp LogicalOp="Concatenation" PhysicalOp="Concatenation">
               <Concat>
                 <RelOp LogicalOp="Aggregate" PhysicalOp="Stream Aggregate">
                   <StreamAggregate />
                 </RelOp>
                 <RelOp LogicalOp="Inner Join" PhysicalOp="Nested Loops">
                   <NestedLoops>
                     {ScopedInputs()}
                   </NestedLoops>
                 </RelOp>
               </Concat>
             </RelOp>
             """);

        Assert.False(SqlServerDiagnosticRecordStoreFixture.HasNativeScopedGroupedReductionPlan(plan));
    }

    [Fact]
    public void Grouped_reduction_recognizer_rejects_scope_predicates_disconnected_from_a_table_access()
    {
        var plan = GroupedPlan(
            $"""
             <RelOp LogicalOp="Aggregate" PhysicalOp="Stream Aggregate">
               <StreamAggregate>
                 <RelOp LogicalOp="Table Scan" PhysicalOp="Table Scan">
                   <TableScan><Object Table="[{RelationalDiagnosticRecordSchema.RecordsTable}]" /></TableScan>
                 </RelOp>
                 <RelOp LogicalOp="Index Scan" PhysicalOp="Index Seek">
                   <IndexScan>
                     <Object Table="[{RelationalDiagnosticRecordSchema.FieldsTable}]" />
                     <SeekPredicates>{ScopeColumns()}</SeekPredicates>
                   </IndexScan>
                 </RelOp>
                 <Predicate>{ScopeColumns()}</Predicate>
               </StreamAggregate>
             </RelOp>
             """);

        Assert.False(SqlServerDiagnosticRecordStoreFixture.HasNativeScopedGroupedReductionPlan(plan));
    }

    [Fact]
    public void Grouped_reduction_recognizer_rejects_all_required_fragments_when_the_top_does_not_feed_the_field_join()
    {
        var plan = GroupedPlan(
            $"""
             <RelOp LogicalOp="Aggregate" PhysicalOp="Stream Aggregate">
               <StreamAggregate>
                 <RelOp LogicalOp="Concatenation" PhysicalOp="Concatenation">
                   <Concat>
                     <RelOp LogicalOp="Inner Join" PhysicalOp="Nested Loops">
                       <NestedLoops>
                         {BoundedRecordInput()}
                         <RelOp LogicalOp="Constant Scan" PhysicalOp="Constant Scan" />
                       </NestedLoops>
                     </RelOp>
                     <RelOp LogicalOp="Inner Join" PhysicalOp="Nested Loops">
                       <NestedLoops>
                         <RelOp LogicalOp="Index Scan" PhysicalOp="Index Seek">
                           <IndexScan>
                             <Object Table="[{RelationalDiagnosticRecordSchema.RecordsTable}]" />
                             <SeekPredicates>{RecordScopeColumns()}</SeekPredicates>
                           </IndexScan>
                         </RelOp>
                         {ScopedFieldInput()}
                       </NestedLoops>
                     </RelOp>
                   </Concat>
                 </RelOp>
               </StreamAggregate>
             </RelOp>
             """);

        Assert.False(SqlServerDiagnosticRecordStoreFixture.HasNativeScopedGroupedReductionPlan(plan));
    }

    [Fact]
    public void Grouped_reduction_recognizer_rejects_forward_or_snapshot_free_input_with_disconnected_valid_fragments()
    {
        foreach (var invalidInput in new[]
                 {
                     BoundedRecordInput(scanDirection: "FORWARD"),
                     BoundedRecordInput(includeSnapshot: false)
                 })
        {
            var plan = GroupedPlan(
                $"""
                 <RelOp LogicalOp="Aggregate" PhysicalOp="Stream Aggregate">
                   <StreamAggregate>
                     <RelOp LogicalOp="Concatenation" PhysicalOp="Concatenation">
                       <Concat>
                         <RelOp LogicalOp="Inner Join" PhysicalOp="Nested Loops">
                           <NestedLoops>
                             {invalidInput}
                             {ScopedFieldInput()}
                           </NestedLoops>
                         </RelOp>
                         {BoundedRecordInput()}
                       </Concat>
                     </RelOp>
                   </StreamAggregate>
                 </RelOp>
                 """);

            Assert.False(SqlServerDiagnosticRecordStoreFixture.HasNativeScopedGroupedReductionPlan(plan));
        }
    }

    private static DiagnosticRecordNativePlan GroupedPlan(string operation) =>
        new(
            "sqlserver",
            DiagnosticRecordPlanOperation.GroupedQuery,
            DiagnosticRecordNativePlanFormats.SqlServerShowplanXml,
            [
                $"""
                 <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
                   <BatchSequence><Batch><Statements><StmtSimple><QueryPlan>{operation}</QueryPlan></StmtSimple></Statements></Batch></BatchSequence>
                 </ShowPlanXML>
                 """
            ]);

    private static string ScopedInputs() =>
        $"""
         <RelOp LogicalOp="Inner Join" PhysicalOp="Nested Loops">
           <NestedLoops>
             {BoundedRecordInput()}
             {ScopedFieldInput()}
           </NestedLoops>
         </RelOp>
         """;

    private static string BoundedRecordInput(
        string scanDirection = "BACKWARD",
        bool includeSnapshot = true) =>
        $"""
         <RelOp LogicalOp="Top" PhysicalOp="Top">
           <Top>
             <TopExpression><ScalarOperator ScalarString="@inputLimit" /></TopExpression>
             <RelOp LogicalOp="Index Scan" PhysicalOp="Index Seek">
               <IndexScan ScanDirection="{scanDirection}">
                 <Object Table="[{RelationalDiagnosticRecordSchema.RecordsTable}]" />
                 <SeekPredicates>{RecordScopeColumns(includeSnapshot)}</SeekPredicates>
               </IndexScan>
             </RelOp>
           </Top>
         </RelOp>
         """;

    private static string ScopedFieldInput() =>
        $"""
         <RelOp LogicalOp="Index Scan" PhysicalOp="Index Seek">
           <IndexScan>
             <Object Table="[{RelationalDiagnosticRecordSchema.FieldsTable}]" />
             <SeekPredicates>{ScopeColumns()}</SeekPredicates>
           </IndexScan>
         </RelOp>
         """;

    private static string RecordScopeColumns(bool includeSnapshot = true) =>
        $"""
        {ScopeColumns()}
        <ColumnReference Column="[cursor]" />
        {(includeSnapshot ? """<ColumnReference Column="@snapshot" />""" : "")}
        """;

    private static string ScopeColumns() =>
        """
        <ColumnReference Column="[tenant_id]" />
        <ColumnReference Column="[scope_id]" />
        <ColumnReference Column="[stream_id]" />
        """;
}
