using System.Data.Common;
using System.Diagnostics;
using Groundwork.Core.PhysicalStorage;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkRunnerIsolationTests : IAsyncDisposable
{
    private readonly string output = Path.Combine(Path.GetTempPath(), $"groundwork-runner-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task Mandatory_indexed_query_plans_use_a_separately_initialized_identically_seeded_target()
    {
        var environment = new RecordingEnvironment();
        var configuration = BenchmarkProfiles.Smoke with
        {
            DatasetSize = 10,
            MigrationDatasetSize = 1,
            WarmupIterations = 1,
            MeasurementIterations = 5,
            OperationsPerIteration = 1,
            Providers = [BenchmarkProvider.Sqlite],
            StorageForms = [PhysicalStorageForm.SharedDocuments]
        };
        var runner = new BenchmarkRunner(null, () => environment);

        var result = await runner.RunAsync(
            new BenchmarkRunRequest(
                FindRepositoryRoot(),
                configuration,
                [BenchmarkWorkload.IndexedQuery],
                output,
                null,
                AllowContainers: false,
                RegressionConfirmationRun: false),
            CancellationToken.None);

        Assert.Equal(2, environment.Targets.Count);
        var measured = Assert.Single(environment.Targets, target => target.Instance.Contains("measure", StringComparison.Ordinal));
        var plans = Assert.Single(environment.Targets, target => target.Instance.Contains("plan", StringComparison.Ordinal));
        Assert.NotEqual(measured.Instance, plans.Instance);
        Assert.Equal(configuration.Seed, measured.Seed.Seed);
        Assert.Equal(configuration.DatasetSize, measured.Seed.Shape.DatasetSize);
        Assert.Equal(
            BenchmarkPayloadProfiles.For(BenchmarkWorkload.IndexedQuery),
            measured.Seed.Shape.PayloadProfile);
        Assert.Equal(measured.Seed, plans.Seed);
        Assert.Equal(1, measured.CorrectnessCalls);
        Assert.Equal(0, plans.CorrectnessCalls);
        Assert.Equal(0, measured.PlanCalls);
        Assert.Equal(1, plans.PlanCalls);
        Assert.Equal([NativePlanAssertionMode.RequireDeclaredIndex], plans.PlanAssertionModes);
        Assert.Equal(
            configuration.WarmupIterations + configuration.MeasurementIterations,
            measured.ExecuteCalls);
        Assert.Equal(
            configuration.MeasurementIterations,
            Assert.Single(result.Report.Cases).Samples.Count);
        Assert.Equal(0, plans.ExecuteCalls);
        Assert.True(measured.Disposed);
        Assert.True(plans.Disposed);
    }

    [Fact]
    public async Task Fifty_percent_shape_captures_a_non_gating_scan_characterization()
    {
        var environment = new RecordingEnvironment();
        var configuration = BenchmarkProfiles.Smoke with
        {
            DatasetSize = 10,
            MigrationDatasetSize = 1,
            WarmupIterations = 1,
            MeasurementIterations = 5,
            OperationsPerIteration = 1,
            Providers = [BenchmarkProvider.Sqlite],
            StorageForms = [PhysicalStorageForm.SharedDocuments]
        };
        var runner = new BenchmarkRunner(null, () => environment);

        await runner.RunAsync(
            new BenchmarkRunRequest(
                FindRepositoryRoot(),
                configuration,
                [BenchmarkWorkload.IndexedQuery],
                output,
                null,
                AllowContainers: false,
                RegressionConfirmationRun: false,
                DataShape: new BenchmarkDataShape(
                    configuration.DatasetSize,
                    0,
                    BenchmarkSelectivityPolicy.ScanCharacterizationBasisPoints)),
            CancellationToken.None);

        var plans = Assert.Single(environment.Targets, target => target.Instance.Contains("plan", StringComparison.Ordinal));
        Assert.Equal([NativePlanAssertionMode.ScanCharacterization], plans.PlanAssertionModes);
    }

    [Fact]
    public async Task Untimed_warmup_process_executes_only_warmup_iterations_and_emits_no_consumer_evidence()
    {
        var environment = new RecordingEnvironment();
        var configuration = BenchmarkProfiles.Smoke with
        {
            DatasetSize = 10,
            MigrationDatasetSize = 1,
            WarmupIterations = 2,
            MeasurementIterations = 5,
            OperationsPerIteration = 1,
            Providers = [BenchmarkProvider.Sqlite],
            StorageForms = [PhysicalStorageForm.SharedDocuments]
        };
        var runner = new BenchmarkRunner(null, () => environment);

        var result = await runner.RunAsync(
            new BenchmarkRunRequest(
                FindRepositoryRoot(),
                configuration,
                [BenchmarkWorkload.Insert],
                output,
                null,
                AllowContainers: false,
                RegressionConfirmationRun: false,
                DataShape: new BenchmarkDataShape(10, 0, 5_000),
                IndependentRun: 0,
                Role: BenchmarkExecutionRole.UntimedWarmup),
            CancellationToken.None);

        var target = Assert.Single(environment.Targets);
        Assert.Equal(configuration.WarmupIterations, target.ExecuteCalls);
        Assert.Empty(result.Report.Cases);
        Assert.False(File.Exists(Path.Combine(output, "reports", "consumer-evidence.json")));
    }

    [Fact]
    public async Task Mutation_only_run_creates_no_plan_target_or_plan_artifacts()
    {
        var environment = new RecordingEnvironment();
        var configuration = BenchmarkProfiles.Smoke with
        {
            DatasetSize = 10,
            MigrationDatasetSize = 1,
            WarmupIterations = 1,
            MeasurementIterations = 5,
            OperationsPerIteration = 1,
            Providers = [BenchmarkProvider.Sqlite],
            StorageForms = [PhysicalStorageForm.SharedDocuments]
        };
        var runner = new BenchmarkRunner(null, () => environment);

        var result = await runner.RunAsync(
            new BenchmarkRunRequest(
                FindRepositoryRoot(),
                configuration,
                [BenchmarkWorkload.Insert],
                output,
                null,
                AllowContainers: false,
                RegressionConfirmationRun: false),
            CancellationToken.None);

        var measured = Assert.Single(environment.Targets);
        Assert.Contains("measure", measured.Instance, StringComparison.Ordinal);
        Assert.Equal(0, measured.PlanCalls);
        Assert.Empty(Assert.Single(result.Report.Cases).PlanArtifacts);
        Assert.True(measured.Disposed);
    }

    [Fact]
    public async Task Concurrent_create_rejects_a_target_that_omits_synchronized_contention_evidence()
    {
        var environment = new RecordingEnvironment();
        var configuration = BenchmarkProfiles.Smoke with
        {
            DatasetSize = 10,
            MigrationDatasetSize = 1,
            WarmupIterations = 1,
            MeasurementIterations = 5,
            OperationsPerIteration = 1,
            Concurrency = 2,
            Providers = [BenchmarkProvider.Sqlite],
            StorageForms = [PhysicalStorageForm.SharedDocuments]
        };
        var runner = new BenchmarkRunner(null, () => environment);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            new BenchmarkRunRequest(
                FindRepositoryRoot(),
                configuration,
                [BenchmarkWorkload.ConcurrentCreate],
                output,
                null,
                AllowContainers: false,
                RegressionConfirmationRun: false),
            CancellationToken.None));

        Assert.Contains("concurrent-create requires complete synchronized-contention evidence", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Measured_run_continues_whole_samples_until_operation_and_steady_state_floors_are_met()
    {
        var clock = new ManualTimeProvider();
        var environment = new RecordingEnvironment(clock, TimeSpan.FromMilliseconds(500));
        var configuration = BenchmarkProfiles.Smoke with
        {
            DatasetSize = 10,
            MigrationDatasetSize = 1,
            WarmupIterations = 1,
            MeasurementIterations = 5,
            OperationsPerIteration = 25,
            MinimumMeasuredOperations = 100,
            MinimumSteadyStateDurationSeconds = 3,
            Providers = [BenchmarkProvider.Sqlite],
            StorageForms = [PhysicalStorageForm.SharedDocuments]
        };
        var runner = new BenchmarkRunner(null, () => environment, clock);

        var result = await runner.RunAsync(
            new BenchmarkRunRequest(
                FindRepositoryRoot(),
                configuration,
                [BenchmarkWorkload.Insert],
                output,
                null,
                AllowContainers: false,
                RegressionConfirmationRun: false),
            CancellationToken.None);

        var measured = Assert.Single(environment.Targets);
        var samples = Assert.Single(result.Report.Cases).Samples;
        Assert.Equal(configuration.WarmupIterations + 6, measured.ExecuteCalls);
        Assert.Equal(6, samples.Count);
        Assert.Equal(150, samples.Sum(sample => sample.Operations));
        Assert.Equal(150, samples.Sum(sample => sample.OperationLatencyNanoseconds.Count));
        Assert.All(samples, sample =>
            Assert.Equal(Enumerable.Repeat(100L, 25), sample.OperationLatencyNanoseconds));
        Assert.Equal(3_000_000_000, samples.Sum(sample => sample.ElapsedNanoseconds));
        var raw = await BenchmarkArtifactWriter.ReadRawAsync(output, CancellationToken.None);
        Assert.Equal(150, raw.Sum(record => record.Sample.OperationLatencyNanoseconds.Count));
    }

    [Fact]
    public async Task Measured_run_rejects_a_target_that_does_not_return_one_latency_per_operation()
    {
        var environment = new RecordingEnvironment(invalidOperationLatencies: true);
        var configuration = BenchmarkProfiles.Smoke with
        {
            DatasetSize = 10,
            MigrationDatasetSize = 1,
            WarmupIterations = 1,
            MeasurementIterations = 5,
            OperationsPerIteration = 2,
            Providers = [BenchmarkProvider.Sqlite],
            StorageForms = [PhysicalStorageForm.SharedDocuments]
        };
        var runner = new BenchmarkRunner(null, () => environment);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            new BenchmarkRunRequest(
                FindRepositoryRoot(),
                configuration,
                [BenchmarkWorkload.Insert],
                output,
                null,
                AllowContainers: false,
                RegressionConfirmationRun: false),
            CancellationToken.None));

        Assert.Contains("one positive raw latency observation per operation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Measured_query_run_ignores_a_target_round_trip_count_without_scoped_telemetry()
    {
        var environment = new RecordingEnvironment(reportedRoundTrips: 2);
        var configuration = BenchmarkProfiles.Smoke with
        {
            DatasetSize = 10,
            MigrationDatasetSize = 1,
            MeasurementIterations = 5,
            OperationsPerIteration = 1,
            Providers = [BenchmarkProvider.Sqlite],
            StorageForms = [PhysicalStorageForm.SharedDocuments]
        };
        var runner = new BenchmarkRunner(null, () => environment);

        var result = await runner.RunAsync(
            new BenchmarkRunRequest(
                FindRepositoryRoot(), configuration, [BenchmarkWorkload.IndexedQuery], output, null,
                AllowContainers: false, RegressionConfirmationRun: false),
            CancellationToken.None);

        Assert.All(Assert.Single(result.Report.Cases).Samples, sample =>
        {
            Assert.Null(sample.RoundTrips);
            Assert.Equal(DatabaseSignalAvailability.Unavailable, sample.DatabaseSignal.Availability);
            Assert.Null(sample.DatabaseSignal.ObservableRoundTrips);
        });
    }

    [Theory]
    [InlineData(BenchmarkProvider.Sqlite)]
    [InlineData(BenchmarkProvider.SqlServer)]
    [InlineData(BenchmarkProvider.PostgreSql)]
    [InlineData(BenchmarkProvider.MongoDb)]
    public async Task Measured_run_persists_target_scoped_signals_for_every_provider_path(BenchmarkProvider provider)
    {
        var environment = new RecordingEnvironment(emitTargetScopedTelemetry: true);
        var configuration = BenchmarkProfiles.Smoke with
        {
            DatasetSize = 10,
            MigrationDatasetSize = 1,
            MeasurementIterations = 5,
            OperationsPerIteration = 1,
            Providers = [provider],
            StorageForms = [PhysicalStorageForm.SharedDocuments]
        };
        var providerOutput = Path.Combine(output, provider.ToString());
        var runner = new BenchmarkRunner(null, () => environment);

        var result = await runner.RunAsync(
            new BenchmarkRunRequest(
                FindRepositoryRoot(), configuration, [BenchmarkWorkload.Insert], providerOutput, null,
                AllowContainers: false, RegressionConfirmationRun: false),
            CancellationToken.None);

        Assert.All(Assert.Single(result.Report.Cases).Samples, sample =>
        {
            Assert.Equal(DatabaseSignalAvailability.Observed, sample.DatabaseSignal.Availability);
            Assert.True(sample.DatabaseSignal.ObservableRoundTrips > 0);
            Assert.Equal(sample.DatabaseSignal.ObservableRoundTrips, sample.RoundTrips);
        });
    }

    [Fact]
    public async Task Measured_run_rejects_later_observable_result_drift_instead_of_reusing_the_first_vector()
    {
        var environment = new RecordingEnvironment(driftObservableResults: true);
        var configuration = BenchmarkProfiles.Smoke with
        {
            DatasetSize = 10,
            MigrationDatasetSize = 1,
            MeasurementIterations = 5,
            OperationsPerIteration = 1,
            Providers = [BenchmarkProvider.Sqlite],
            StorageForms = [PhysicalStorageForm.SharedDocuments]
        };
        var runner = new BenchmarkRunner(null, () => environment);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            new BenchmarkRunRequest(
                FindRepositoryRoot(), configuration, [BenchmarkWorkload.IndexedQuery], output, null,
                AllowContainers: false, RegressionConfirmationRun: false),
            CancellationToken.None));

        Assert.Contains("observable results changed", exception.Message, StringComparison.Ordinal);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(output))
            Directory.Delete(output, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Groundwork repository root.");
    }

    private sealed class RecordingEnvironment(
        ManualTimeProvider? clock = null,
        TimeSpan? executionDuration = null,
        bool invalidOperationLatencies = false,
        bool driftObservableResults = false,
        bool zeroRoundTrips = false,
        bool emitTargetScopedTelemetry = false,
        long? reportedRoundTrips = null) : IBenchmarkProviderEnvironment
    {
        public List<RecordingTarget> Targets { get; } = [];

        public Task StartAsync(
            IReadOnlyList<BenchmarkProvider> providers,
            bool allowContainers,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public IPhysicalStorageBenchmarkTarget CreateTarget(
            BenchmarkProvider provider,
            PhysicalStorageForm form,
            string instance,
            string scratchDirectory,
            int migrationDatasetSize)
        {
            var target = new RecordingTarget(
                provider,
                form,
                instance,
                clock,
                executionDuration,
                invalidOperationLatencies,
                driftObservableResults,
                zeroRoundTrips,
                emitTargetScopedTelemetry,
                reportedRoundTrips);
            Targets.Add(target);
            return target;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingTarget(
        BenchmarkProvider provider,
        PhysicalStorageForm storageForm,
        string instance,
        ManualTimeProvider? clock,
        TimeSpan? executionDuration,
        bool invalidOperationLatencies,
        bool driftObservableResults,
        bool zeroRoundTrips,
        bool emitTargetScopedTelemetry,
        long? reportedRoundTrips) : IPhysicalStorageBenchmarkTarget
    {
        private const string SqliteDataSource = "recording-target.db";
        private const string SqlServerDatabase = "groundwork_measured";
        private const string PostgreSqlApplicationName = "groundwork-measured";
        private const string MongoDatabase = "groundwork-measured";
        private readonly DiagnosticListener? diagnosticListener = emitTargetScopedTelemetry && provider != BenchmarkProvider.MongoDb
            ? new DiagnosticListener(provider == BenchmarkProvider.Sqlite
                ? "Microsoft.Data.Sqlite"
                : provider == BenchmarkProvider.SqlServer ? "Microsoft.Data.SqlClient" : "Npgsql")
            : null;

        public BenchmarkProvider Provider => provider;
        public PhysicalStorageForm StorageForm => storageForm;
        public string ProviderVersion => "test";
        public IReadOnlyDictionary<string, string> ProviderConfiguration { get; } = new Dictionary<string, string>();
        public DatabaseSignalTarget SignalTarget { get; } = provider switch
        {
            BenchmarkProvider.Sqlite => DatabaseSignalTarget.ForSqlite(SqliteDataSource),
            BenchmarkProvider.SqlServer => DatabaseSignalTarget.ForSqlServer(SqlServerDatabase),
            BenchmarkProvider.PostgreSql => DatabaseSignalTarget.ForPostgreSql(PostgreSqlApplicationName),
            BenchmarkProvider.MongoDb => DatabaseSignalTarget.ForMongoDb(MongoDatabase),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };
        public string Instance => instance;
        public (int Seed, BenchmarkDataShape Shape) Seed { get; private set; }
        public int CorrectnessCalls { get; private set; }
        public int PlanCalls { get; private set; }
        public List<NativePlanAssertionMode> PlanAssertionModes { get; } = [];
        public int ExecuteCalls { get; private set; }
        public bool Disposed { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SeedAsync(int seed, BenchmarkDataShape shape, CancellationToken cancellationToken)
        {
            Seed = (seed, shape);
            return Task.CompletedTask;
        }

        public Task<CorrectnessGateResult> RunCorrectnessGateAsync(CancellationToken cancellationToken)
        {
            CorrectnessCalls++;
            return Task.FromResult(new CorrectnessGateResult(true, true, true, true, true));
        }

        public Task<IReadOnlyList<NativePlanEvidence>> CaptureNativePlansAsync(
            IReadOnlyList<BenchmarkPlanRequest> requests,
            NativePlanAssertionMode assertionMode,
            CancellationToken cancellationToken)
        {
            PlanCalls++;
            PlanAssertionModes.Add(assertionMode);
            return Task.FromResult<IReadOnlyList<NativePlanEvidence>>(requests
                .Select(request => PlanEvidence(request, assertionMode))
                .ToArray());
        }

        private NativePlanEvidence PlanEvidence(
            BenchmarkPlanRequest request,
            NativePlanAssertionMode assertionMode)
        {
            var command = request.Operation == NativePlanOperation.Count
                ? "SELECT COUNT(*) FROM \"table\" AS l WHERE l.\"storage_scope\" = @scope AND l.\"document_kind\" = @kind AND l.\"status\" = @q0"
                : $"SELECT * FROM \"table\" AS l WHERE l.\"storage_scope\" = @scope AND l.\"document_kind\" = @kind AND l.\"status\" = @q0" +
                  (request.Ordered ? " ORDER BY l.\"rank\" DESC" : string.Empty) +
                  " LIMIT @take OFFSET @skip";
            var parameters = new List<(string Name, object? Value)>
            {
                ("scope", "tenant-a"),
                ("kind", "benchmark-document"),
                ("q0", "open")
            };
            if (request.Operation == NativePlanOperation.Selection)
            {
                parameters.Add(("skip", request.Skip ?? 0));
                parameters.Add(("take", request.Take));
            }
            var receipt = NativePlanQueryReceipt.FromRelational(
                request,
                assertionMode,
                command,
                parameters,
                NativePlanTestBindings.CanonicalFields);
            return new NativePlanEvidence(
                request,
                Provider,
                StorageForm,
                "query",
                "table",
                "index",
                "plan",
                ["assertion"])
            {
                CommandBinding = new NativePlanCommandBinding("table", "l", command, receipt.Shape, receipt)
                {
                    Fields = NativePlanTestBindings.CanonicalFields
                }
            };
        }

        public Task PrepareWorkloadAsync(BenchmarkWorkload workload, int totalIterations, int operationsPerIteration, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PrepareIterationAsync(BenchmarkWorkload workload, int iteration, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<WorkloadExecution> ExecuteAsync(
            BenchmarkWorkload workload,
            int iteration,
            int operations,
            int concurrency,
            CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            if (executionDuration is not null)
                clock!.Advance(executionDuration.Value);
            EmitTargetScopedTelemetry();
            return Task.FromResult(new WorkloadExecution(
                operations,
                0,
                0,
                reportedRoundTrips ?? (zeroRoundTrips ? 0 : 2),
                new Dictionary<string, long>(),
                Enumerable.Repeat(100L, invalidOperationLatencies ? operations - 1 : operations).ToArray(),
                BenchmarkObservableResultVector.Create(
                [
                    new BenchmarkObservableResult(
                        0,
                        $"{workload}-result",
                        driftObservableResults && iteration > 0 ? "drifted" : "validated",
                        1,
                        operations,
                        null)
                ])));
        }

        public Task ValidateIterationAsync(BenchmarkWorkload workload, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<StorageSnapshot> CaptureStorageAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StorageSnapshot(0, 0, 0, 0, new Dictionary<string, long>()));

        public ValueTask DisposeAsync()
        {
            diagnosticListener?.Dispose();
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        private void EmitTargetScopedTelemetry()
        {
            if (emitTargetScopedTelemetry && provider == BenchmarkProvider.MongoDb)
            {
                DatabaseSignalCollector.PublishMongoDbCommandStart(MongoDatabase);
                return;
            }

            if (diagnosticListener is null)
                return;

            using DbConnection connection = provider switch
            {
                BenchmarkProvider.Sqlite => new SqliteConnection($"Data Source={SqliteDataSource}"),
                BenchmarkProvider.SqlServer => new SqlConnection(
                    $"Server=localhost;Database={SqlServerDatabase};User Id=groundwork;Password=secret"),
                BenchmarkProvider.PostgreSql => new NpgsqlConnection(
                    $"Host=localhost;Database=groundwork;Username=groundwork;Password=secret;Application Name={PostgreSqlApplicationName}"),
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
            };
            using var command = connection.CreateCommand();
            diagnosticListener.Write("CommandStart", new { Command = command });
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.UnixEpoch.AddTicks(timestamp);

        public void Advance(TimeSpan duration) =>
            timestamp += duration.Ticks;
    }
}
