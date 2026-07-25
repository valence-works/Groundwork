using Xunit;
using System.Text.Json;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkSummarizerTests
{
    [Fact]
    public void Summary_rejects_a_round_trip_that_is_not_bound_to_observed_target_scoped_telemetry()
    {
        var sample = new BenchmarkSample(
            0, 1, 1_000, 200, 1, 0, 0, null, null, new Dictionary<string, long>(), [100]);

        var exception = Assert.Throws<ArgumentException>(() =>
            BenchmarkSummarizer.Summarize("case", [sample]));

        Assert.Contains("target-scoped database-signal evidence", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_reports_raw_operation_latency_throughput_allocation_round_trips_and_write_amplification()
    {
        var before = new StorageSnapshot(1_000, 200, 10, 10, new Dictionary<string, long>());
        var after = new StorageSnapshot(1_400, 300, 12, 12, new Dictionary<string, long>());
        BenchmarkSample[] samples =
        [
            new BenchmarkSample(0, 2, 1_000, 200, 5, 100, 2, before, after, new Dictionary<string, long> { ["commands"] = 5 }, [100, 900])
                .WithObservedCommandSignal(),
            new BenchmarkSample(1, 2, 1_000, 400, 7, 100, 2, before, after, new Dictionary<string, long> { ["commands"] = 7 }, [100, 900])
                .WithObservedCommandSignal()
        ];

        var summary = BenchmarkSummarizer.Summarize("case", samples);

        Assert.Equal(4, summary.OperationLatencyObservationCount);
        Assert.Equal(100, summary.OperationLatencyP50Nanoseconds);
        Assert.Equal(900, summary.OperationLatencyP95Nanoseconds);
        Assert.Equal(4 * 1_000_000_000d / 2_000, summary.ThroughputOperationsPerSecond);
        Assert.Equal(150, summary.AllocatedBytesPerOperation);
        Assert.Equal(3, summary.RoundTripsPerOperation);
        Assert.Equal(400, summary.StorageGrowthBytes);
        Assert.Equal(2, summary.NetStorageGrowthBytesPerLogicalPayloadByte);
        Assert.Equal(1, summary.NetPhysicalRowGrowthPerLogicalMutation);
        Assert.Equal(3, summary.ProviderWorkPerOperation["commands"]);

        var json = JsonSerializer.Serialize(samples[0], BenchmarkJson.Options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            [100L, 900L],
            document.RootElement.GetProperty("operationLatencyNanoseconds")
                .EnumerateArray()
                .Select(element => element.GetInt64()));
        Assert.False(document.RootElement.TryGetProperty("normalizedBatchLatencyNanosecondsPerOperation", out _));
    }
}
