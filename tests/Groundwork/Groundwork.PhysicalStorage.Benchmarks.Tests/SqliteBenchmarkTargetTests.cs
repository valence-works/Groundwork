using System.Text;
using System.Text.Json;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class SqliteBenchmarkTargetTests : IAsyncDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), $"groundwork-benchmark-test-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task SQLite_target_passes_correctness_scope_occ_and_native_plan_gates(PhysicalStorageForm form)
    {
        await using var target = new SqliteBenchmarkTarget(form, Guid.NewGuid().ToString("N")[..8], scratch, 5);
        await target.InitializeAsync(CancellationToken.None);
        await target.SeedAsync(
            BenchmarkProfiles.ReproducibleSeed,
            new BenchmarkDataShape(250, 0, 100),
            CancellationToken.None);

        var correctness = await target.RunCorrectnessGateAsync(CancellationToken.None);
        var beforePlans = await target.CaptureStorageAsync(CancellationToken.None);
        var plans = await target.CaptureNativePlansAsync(
            BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.IndexedQuery]),
            NativePlanAssertionMode.RequireDeclaredIndex,
            CancellationToken.None);
        var afterPlans = await target.CaptureStorageAsync(CancellationToken.None);

        Assert.True(correctness.ScopeIsolation);
        Assert.True(correctness.OptimisticConcurrency);
        Assert.True(correctness.UnitOfWorkRollback);
        Assert.True(correctness.BoundedQuery);
        Assert.True(correctness.MixedOrdering);
        Assert.Equal(2, plans.Count);
        Assert.All(plans, plan => Assert.Contains(plan.IndexName, plan.NativePlan, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(beforePlans.PrimaryRows, afterPlans.PrimaryRows);
        Assert.Equal(beforePlans.LinkedRows, afterPlans.LinkedRows);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task SQLite_plan_gate_rejects_a_scan_without_changing_the_measured_shape(PhysicalStorageForm form)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        await using var target = new SqliteBenchmarkTarget(form, instance, scratch, 5);
        await target.InitializeAsync(CancellationToken.None);
        await target.SeedAsync(
            BenchmarkProfiles.ReproducibleSeed,
            new BenchmarkDataShape(250, 0, 100),
            CancellationToken.None);
        var model = BenchmarkModelFactory.CompileRelational(
            form,
            instance,
            SqliteGroundworkCapabilities.Provider,
            SqliteGroundworkCapabilities.PhysicalNames);
        await ReplaceIndexWithScanShapeAsync(DatabasePath(instance, form), model);
        var beforePlans = await target.CaptureStorageAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            target.CaptureNativePlansAsync(
                BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.IndexedQuery]),
                NativePlanAssertionMode.RequireDeclaredIndex,
                CancellationToken.None));
        var afterPlans = await target.CaptureStorageAsync(CancellationToken.None);

        Assert.Contains("native-plan gate rejected", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SCAN", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforePlans.PrimaryRows, afterPlans.PrimaryRows);
        Assert.Equal(beforePlans.LinkedRows, afterPlans.LinkedRows);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task SQLite_schema_drift_blocks_target_initialization_before_timing(PhysicalStorageForm form)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        await using var target = new SqliteBenchmarkTarget(form, instance, scratch, 5);

        var exception = await Assert.ThrowsAsync<GroundworkRuntimeSchemaAdmissionException>(() =>
            target.InitializeAsync(
                async (connectionString, model, cancellationToken) =>
                    await DropIndexAsync(
                        new SqliteConnectionStringBuilder(connectionString).DataSource,
                        model.Route.Indexes.Single().Name.Identifier,
                        cancellationToken),
                CancellationToken.None));

        Assert.Contains("admission", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Backfill_measurement_is_followed_by_projected_query_validation(PhysicalStorageForm form)
    {
        await using IPhysicalStorageBenchmarkTarget target =
            new SqliteBenchmarkTarget(form, Guid.NewGuid().ToString("N")[..8], scratch, 5);
        await target.InitializeAsync(CancellationToken.None);
        await target.PrepareIterationAsync(BenchmarkWorkload.BackfillMigration, 0, CancellationToken.None);

        var execution = await target.ExecuteAsync(
            BenchmarkWorkload.BackfillMigration,
            0,
            operations: 1,
            concurrency: 1,
            CancellationToken.None);
        await target.ValidateIterationAsync(BenchmarkWorkload.BackfillMigration, CancellationToken.None);

        Assert.Equal(5, execution.LogicalMutations);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Concurrent_create_retains_full_parallel_production_store_call_evidence(PhysicalStorageForm form)
    {
        await using var target = new SqliteBenchmarkTarget(form, Guid.NewGuid().ToString("N")[..8], scratch, 5);
        await target.InitializeAsync(CancellationToken.None);
        await target.SeedAsync(
            BenchmarkProfiles.ReproducibleSeed,
            new BenchmarkDataShape(25, BenchmarkPayloadProfiles.For(BenchmarkWorkload.ConcurrentCreate), 1_000),
            CancellationToken.None);

        var execution = await target.ExecuteAsync(
            BenchmarkWorkload.ConcurrentCreate,
            iteration: 0,
            operations: 3,
            concurrency: 4,
            CancellationToken.None);

        var evidence = Assert.IsType<ConcurrentLoadEvidence>(execution.ConcurrentLoad);
        Assert.Equal(4, evidence.RequestedParallelism);
        Assert.Equal(3, evidence.WaveCount);
        Assert.Equal(3, evidence.FullyParallelWaveCount);
        Assert.Equal(12, evidence.Attempts);
        Assert.Equal(12, evidence.Completions);
        Assert.Equal(3, evidence.SuccessfulOperations);
        Assert.Equal(9, evidence.ConflictOperations);
        Assert.Equal(4, evidence.PeakInFlightProductionStoreCalls);
        Assert.True(evidence.MeetsConfiguredParallelism(4));
        Assert.Equal(12, execution.Operations);
        Assert.Equal(12, execution.OperationLatencyNanoseconds.Count);
    }

    [Fact]
    public async Task Storage_growth_consumes_the_declared_profile_instead_of_a_hidden_1KiB_override()
    {
        var reviewedInstance = Guid.NewGuid().ToString("N")[..8];
        var reviewedProfile = BenchmarkPayloadProfiles.For(BenchmarkWorkload.StorageGrowth);
        await using var reviewed = new SqliteBenchmarkTarget(
            PhysicalStorageForm.SharedDocuments,
            reviewedInstance,
            scratch,
            5);
        await reviewed.InitializeAsync(CancellationToken.None);
        await reviewed.SeedAsync(
            BenchmarkProfiles.ReproducibleSeed,
            new BenchmarkDataShape(
                25,
                reviewedProfile,
                BenchmarkSelectivityPolicy.IndexedQueryAcceptanceBasisPoints),
            CancellationToken.None);
        var reviewedExecution = await reviewed.ExecuteAsync(
            BenchmarkWorkload.StorageGrowth,
            iteration: 0,
            operations: 1,
            concurrency: 1,
            CancellationToken.None);
        var returnedPayload = Assert.Single(
            Assert.IsType<BenchmarkObservableResultVector>(reviewedExecution.ObservableResultVector).Results).Payload;
        Assert.NotNull(returnedPayload);
        var returnedPadding = AssertPaddingBytes(returnedPayload, reviewedProfile.PaddingBytes);

        var persistedPadding = await ReadStorageGrowthPaddingAsync(
            DatabasePath(reviewedInstance, PhysicalStorageForm.SharedDocuments),
            PhysicalStorageForm.SharedDocuments,
            reviewedInstance);
        Assert.Equal(reviewedProfile.PaddingBytes, persistedPadding.Utf8ByteCount);
        Assert.Equal(returnedPadding, persistedPadding.Value);
        Assert.Equal(reviewedProfile.PaddingBytes, Encoding.UTF8.GetByteCount(persistedPadding.Value));

        await using var legacy = new SqliteBenchmarkTarget(
            PhysicalStorageForm.SharedDocuments,
            Guid.NewGuid().ToString("N")[..8],
            scratch,
            5);
        await legacy.InitializeAsync(CancellationToken.None);
        await legacy.SeedAsync(
            BenchmarkProfiles.ReproducibleSeed,
            new BenchmarkDataShape(
                25,
                BenchmarkPayloadProfiles.CreateLegacyPadding(0),
                BenchmarkSelectivityPolicy.IndexedQueryAcceptanceBasisPoints),
            CancellationToken.None);
        var legacyExecution = await legacy.ExecuteAsync(
            BenchmarkWorkload.StorageGrowth,
            iteration: 0,
            operations: 1,
            concurrency: 1,
            CancellationToken.None);

        Assert.True(reviewedExecution.LogicalPayloadBytes >= 1_024);
        Assert.True(legacyExecution.LogicalPayloadBytes < 1_024);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
        return ValueTask.CompletedTask;
    }

    private string DatabasePath(string instance, PhysicalStorageForm form) =>
        Path.Combine(scratch, $"sqlite-{instance}-{form}.db");

    private static async Task DropIndexAsync(
        string databasePath,
        string indexName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP INDEX \"{indexName.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplaceIndexWithScanShapeAsync(
        string databasePath,
        BenchmarkPhysicalModel model)
    {
        var indexName = model.Route.Indexes.Single().Name.Identifier;
        var table = (model.Route.LinkedIndexStorage ?? model.Route.PrimaryStorage).Name.Identifier;
        var rank = model.Route.ProjectedColumns.Single(column => column.Definition.Path == "rank").Column.Identifier;
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            DROP INDEX "{indexName.Replace("\"", "\"\"", StringComparison.Ordinal)}";
            CREATE INDEX "{indexName.Replace("\"", "\"\"", StringComparison.Ordinal)}"
                ON "{table.Replace("\"", "\"\"", StringComparison.Ordinal)}"
                ("{rank.Replace("\"", "\"\"", StringComparison.Ordinal)}");
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static string AssertPaddingBytes(string payload, int expectedBytes)
    {
        using var document = JsonDocument.Parse(payload);
        var padding = document.RootElement.GetProperty("padding").GetString() ??
                      throw new InvalidOperationException("The benchmark payload has no padding value.");
        Assert.Equal(new string('x', expectedBytes), padding);
        Assert.Equal(expectedBytes, Encoding.UTF8.GetByteCount(padding));
        return padding;
    }

    private static async Task<PersistedPadding> ReadStorageGrowthPaddingAsync(
        string databasePath,
        PhysicalStorageForm form,
        string instance)
    {
        var model = BenchmarkModelFactory.CompileRelational(
            form,
            instance,
            SqliteGroundworkCapabilities.Provider,
            SqliteGroundworkCapabilities.PhysicalNames);
        var table = Quote(model.Route.PrimaryStorage.Name.Identifier);
        var canonicalJson = Quote(model.Route.Envelope.CanonicalJson.Identifier);
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT json_extract({canonicalJson}, '$.padding'),
                   length(CAST(json_extract({canonicalJson}, '$.padding') AS BLOB))
            FROM {table}
            WHERE json_extract({canonicalJson}, '$.category') = 'write';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The storage-growth write was not persisted.");
        var padding = new PersistedPadding(reader.GetString(0), reader.GetInt64(1));
        Assert.False(await reader.ReadAsync(), "Expected exactly one persisted storage-growth write.");
        return padding;
    }

    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record PersistedPadding(string Value, long Utf8ByteCount);
}
