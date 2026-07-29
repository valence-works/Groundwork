using Groundwork.Core.PhysicalStorage;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BaselineEligibilityEvaluatorTests
{
    [Fact]
    public void Scheduled_scaffolding_matrix_is_non_promotable_until_issue_50_evidence_is_complete()
    {
        var cases = BenchmarkMatrix.Create(BenchmarkProfiles.Scheduled)
            .Select(CreateResult)
            .ToArray();

        var result = BaselineEligibilityEvaluator.Evaluate(
            BenchmarkProfiles.Scheduled,
            Enum.GetValues<BenchmarkWorkload>(),
            Machine(gitDirty: false),
            cases);

        Assert.False(result.Eligible);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("1K/100K/1M", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("payload", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("selectivity", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("exact-HEAD", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("four providers", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("recovery", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("immutable-baseline", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Contains("synchronized-contention", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Filtered_dirty_run_cannot_be_promoted()
    {
        var configuration = BenchmarkProfiles.Scheduled with
        {
            Providers = [BenchmarkProvider.Sqlite],
            StorageForms = [PhysicalStorageForm.PhysicalEntityTable]
        };
        var cases = BenchmarkMatrix.Create(configuration)
            .Where(benchmarkCase => benchmarkCase.Workload == BenchmarkWorkload.IndexedQuery)
            .Select(CreateResult)
            .ToArray();

        var result = BaselineEligibilityEvaluator.Evaluate(
            configuration,
            [BenchmarkWorkload.IndexedQuery],
            Machine(gitDirty: true),
            cases);

        Assert.False(result.Eligible);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("clean Git commit", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("every provider", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("every workload", StringComparison.Ordinal));
    }

    private static BenchmarkCaseResult CreateResult(BenchmarkCase benchmarkCase)
    {
        var samples = Enumerable.Range(0, BenchmarkProfiles.Scheduled.MeasurementIterations)
            .Select(iteration =>
            {
                var operations = benchmarkCase.Workload == BenchmarkWorkload.ConcurrentCreate
                    ? BenchmarkProfiles.Scheduled.Concurrency * BenchmarkProfiles.Scheduled.OperationsPerIteration
                    : 10;
                return new BenchmarkSample(
                iteration, operations, 1_000_000_000, 100, 1, 0, 0, null, null, new Dictionary<string, long>(),
                Enumerable.Repeat(100L, operations).ToArray())
            {
                ConcurrentLoad = benchmarkCase.Workload == BenchmarkWorkload.ConcurrentCreate
                    ? new ConcurrentLoadEvidence(
                        RequestedParallelism: BenchmarkProfiles.Scheduled.Concurrency,
                        WaveCount: BenchmarkProfiles.Scheduled.OperationsPerIteration,
                        ReleasedTogetherWaveCount: BenchmarkProfiles.Scheduled.OperationsPerIteration,
                        Attempts: BenchmarkProfiles.Scheduled.Concurrency * BenchmarkProfiles.Scheduled.OperationsPerIteration,
                        Completions: BenchmarkProfiles.Scheduled.Concurrency * BenchmarkProfiles.Scheduled.OperationsPerIteration,
                        SuccessfulOperations: BenchmarkProfiles.Scheduled.OperationsPerIteration,
                        ConflictOperations: (BenchmarkProfiles.Scheduled.Concurrency - 1) * BenchmarkProfiles.Scheduled.OperationsPerIteration,
                        PeakInFlightProductionStoreCalls: BenchmarkProfiles.Scheduled.Concurrency)
                    : null
            }.WithObservedCommandSignal();
            })
            .ToArray();
        return new BenchmarkCaseResult(
            benchmarkCase,
            new CorrectnessGateResult(true, true, true, true, true),
            BenchmarkPlanRequests.ForWorkloads([benchmarkCase.Workload])
                .Select(request => $"plans/{benchmarkCase.Identity}-{request.Operation}.json")
                .ToArray(),
            BenchmarkSummarizer.Summarize(benchmarkCase.Identity, samples),
            samples);
    }

    private static BenchmarkMachineMetadata Machine(bool gitDirty) => new(
        "test-os",
        "benchmark-host",
        "test-architecture",
        "test-framework",
        BuildConfiguration: "Release",
        ProcessorCount: 8,
        ServerGc: true,
        StopwatchFrequency: 1_000_000_000,
        HarnessVersion: "1.0.0",
        GitCommit: "0123456789abcdef",
        GitDirty: gitDirty,
        CapturedAtUtc: DateTimeOffset.UnixEpoch);
}
