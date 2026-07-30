using Groundwork.Core.PhysicalStorage;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MongoDbBenchmarkSignalEvidenceCollection
{
    public const string Name = nameof(MongoDbBenchmarkSignalEvidenceCollection);
}

[Collection(MongoDbBenchmarkSignalEvidenceCollection.Name)]
public sealed class MongoDbBenchmarkSignalEvidenceTests()
    : BenchmarkIntegrationTestBase("groundwork-mongodb-signal-evidence")
{
    [Fact]
    public async Task Real_runner_persists_target_scoped_driver_command_counts()
    {
        var configuration = BenchmarkProfiles.Smoke with
        {
            DatasetSize = 25,
            MigrationDatasetSize = 5,
            MeasurementIterations = 5,
            OperationsPerIteration = 1,
            Providers = [BenchmarkProvider.MongoDb],
            StorageForms = [PhysicalStorageForm.SharedDocuments]
        };
        var workloads = new[]
        {
            BenchmarkWorkload.ClientResetPointReadBatch,
            BenchmarkWorkload.IndexedQuery,
            BenchmarkWorkload.BackfillMigration,
            BenchmarkWorkload.ClientRestartValidation
        };
        var result = await new BenchmarkRunner().RunAsync(
            new BenchmarkRunRequest(
                RepositoryRoot,
                configuration,
                workloads,
                Output,
                BaselineRun: null,
                AllowContainers: true,
                RegressionConfirmationRun: false),
            CancellationToken.None);

        Assert.Equal(workloads, result.Report.Cases.Select(resultCase => resultCase.Case.Workload));
        Assert.All(result.Report.Cases, benchmarkCase =>
        {
            Assert.Equal(configuration.MeasurementIterations, benchmarkCase.Samples.Count);
            Assert.All(benchmarkCase.Samples, sample =>
            {
                Assert.Equal("target-scoped-diagnostic-command", sample.DatabaseSignal.Source);
                Assert.True(sample.DatabaseSignal.CommandStarts > 0);
                Assert.Null(sample.DatabaseSignal.ClientActivities);
                Assert.Equal(sample.DatabaseSignal.CommandStarts, sample.DatabaseSignal.ObservableRoundTrips);
                Assert.Equal(sample.DatabaseSignal.ObservableRoundTrips, sample.RoundTrips);
                Assert.Equal(
                    sample.DatabaseSignal.CommandStarts,
                    sample.ProviderWork["target_scoped_diagnostic_command_starts"]);
                Assert.Equal(
                    sample.DatabaseSignal.ObservableRoundTrips,
                    sample.ProviderWork["target_scoped_round_trip_signals"]);
                Assert.DoesNotContain("target_scoped_client_activities", sample.ProviderWork.Keys);
            });
        });

        var raw = await File.ReadAllTextAsync(Path.Combine(Output, "raw", "measurements.jsonl"));
        Assert.DoesNotContain("mongodb://", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("groundwork_bench_", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ordered_query_uses_the_declared_compound_index_without_a_blocking_sort_for_every_form()
    {
        var configuration = BenchmarkProfiles.Smoke with
        {
            DatasetSize = 25,
            MigrationDatasetSize = 5,
            MeasurementIterations = 5,
            OperationsPerIteration = 1,
            Providers = [BenchmarkProvider.MongoDb],
            StorageForms = Enum.GetValues<PhysicalStorageForm>()
        };
        var result = await new BenchmarkRunner().RunAsync(
            new BenchmarkRunRequest(
                RepositoryRoot,
                configuration,
                [BenchmarkWorkload.MixedCompoundOrdering],
                Output,
                BaselineRun: null,
                AllowContainers: true,
                RegressionConfirmationRun: false),
            CancellationToken.None);

        Assert.Equal(Enum.GetValues<PhysicalStorageForm>(), result.Report.Cases.Select(resultCase => resultCase.Case.StorageForm));
        Assert.All(result.Report.Cases, benchmarkCase => Assert.Equal(2, benchmarkCase.PlanArtifacts.Count));
    }
}
