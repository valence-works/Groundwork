using System.Text.Json.Serialization;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record StorageSnapshot(
    long TotalBytes,
    long IndexBytes,
    long PrimaryRows,
    long LinkedRows,
    IReadOnlyDictionary<string, long> ProviderValues);

public sealed record BenchmarkSample(
    int Iteration,
    int Operations,
    long ElapsedNanoseconds,
    long AllocatedBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long? RoundTrips,
    long LogicalPayloadBytes,
    long LogicalMutations,
    StorageSnapshot? StorageBefore,
    StorageSnapshot? StorageAfter,
    IReadOnlyDictionary<string, long> ProviderWork,
    IReadOnlyList<long> OperationLatencyNanoseconds)
{
    public DatabaseSignalEvidence DatabaseSignal { get; init; } = new(
        DatabaseSignalAvailability.Unavailable,
        "unavailable",
        "no-target-scoped-provider-telemetry",
        null,
        null,
        null);

    public double ThroughputOperationsPerSecond => Operations * 1_000_000_000d / ElapsedNanoseconds;
    public double AllocatedBytesPerOperation => (double)AllocatedBytes / Operations;

    /// <summary>
    /// Keeps the persisted round-trip metric bound exclusively to the target-scoped
    /// diagnostic snapshot that produced it. A target's compatibility counter is not
    /// evidence and must never be persisted as a substitute for unavailable telemetry.
    /// </summary>
    public bool HasValidDatabaseSignalEvidence()
    {
        if (DatabaseSignal is null)
            return false;

        return DatabaseSignal.Availability switch
        {
            DatabaseSignalAvailability.Observed =>
                DatabaseSignalEvidence.IsRecognizedObservedSource(DatabaseSignal.Source) &&
                DatabaseSignal.Reason is null &&
                DatabaseSignal.ObservableRoundTrips is > 0 &&
                RoundTrips == DatabaseSignal.ObservableRoundTrips &&
                (DatabaseSignal.CommandStarts is null or > 0) &&
                (DatabaseSignal.ClientActivities is null or > 0) &&
                (DatabaseSignal.Source == "target-scoped-diagnostic-command"
                    ? DatabaseSignal.CommandStarts is > 0
                    : DatabaseSignal.ClientActivities is > 0),
            DatabaseSignalAvailability.Unavailable =>
                string.Equals(DatabaseSignal.Source, "unavailable", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(DatabaseSignal.Reason) &&
                DatabaseSignal.CommandStarts is null &&
                DatabaseSignal.ClientActivities is null &&
                DatabaseSignal.ObservableRoundTrips is null &&
                RoundTrips is null,
            _ => false
        };
    }
}

public sealed record BenchmarkCaseSummary(
    string CaseIdentity,
    int SampleCount,
    int OperationLatencyObservationCount,
    double OperationLatencyP50Nanoseconds,
    double OperationLatencyP95Nanoseconds,
    double OperationLatencyP99Nanoseconds,
    double ThroughputOperationsPerSecond,
    double AllocatedBytesPerOperation,
    double? RoundTripsPerOperation,
    long? StorageGrowthBytes,
    double? NetStorageGrowthBytesPerLogicalPayloadByte,
    double? NetPhysicalRowGrowthPerLogicalMutation,
    IReadOnlyDictionary<string, double> ProviderWorkPerOperation);

public static class BenchmarkSummarizer
{
    public static BenchmarkCaseSummary Summarize(string caseIdentity, IReadOnlyList<BenchmarkSample> samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseIdentity);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException("At least one sample is required.", nameof(samples));
        if (samples.Any(sample =>
                sample.Operations <= 0 ||
                sample.ElapsedNanoseconds <= 0 ||
                !sample.HasValidDatabaseSignalEvidence() ||
                sample.OperationLatencyNanoseconds is null ||
                sample.OperationLatencyNanoseconds.Count != sample.Operations ||
                sample.OperationLatencyNanoseconds.Any(latency => latency <= 0)))
        {
            throw new ArgumentException(
                "Every sample must contain valid target-scoped database-signal evidence and one positive raw latency observation per operation.",
                nameof(samples));
        }

        var operationLatency = samples
            .SelectMany(sample => sample.OperationLatencyNanoseconds)
            .Select(latency => (double)latency)
            .ToArray();
        var totalOperations = samples.Sum(sample => (long)sample.Operations);
        var totalElapsed = samples.Sum(sample => sample.ElapsedNanoseconds);
        var totalAllocated = samples.Sum(sample => sample.AllocatedBytes);
        var observableRoundTrips = samples.Where(sample => sample.RoundTrips.HasValue).ToArray();
        var firstStorage = samples.Select(sample => sample.StorageBefore).FirstOrDefault(snapshot => snapshot is not null);
        var lastStorage = samples.Select(sample => sample.StorageAfter).LastOrDefault(snapshot => snapshot is not null);
        var logicalBytes = samples.Sum(sample => sample.LogicalPayloadBytes);
        var logicalMutations = samples.Sum(sample => sample.LogicalMutations);
        long? storageGrowth = firstStorage is null || lastStorage is null
            ? null
            : Math.Max(0, lastStorage.TotalBytes - firstStorage.TotalBytes);
        long? rowGrowth = firstStorage is null || lastStorage is null
            ? null
            : Math.Max(0, lastStorage.PrimaryRows + lastStorage.LinkedRows - firstStorage.PrimaryRows - firstStorage.LinkedRows);
        var providerWork = samples
            .SelectMany(sample => sample.ProviderWork)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value) / (double)totalOperations, StringComparer.Ordinal);

        return new BenchmarkCaseSummary(
            caseIdentity,
            samples.Count,
            operationLatency.Length,
            BenchmarkStatistics.Percentile(operationLatency, 0.50),
            BenchmarkStatistics.Percentile(operationLatency, 0.95),
            BenchmarkStatistics.Percentile(operationLatency, 0.99),
            totalOperations * 1_000_000_000d / totalElapsed,
            totalAllocated / (double)totalOperations,
            observableRoundTrips.Length == 0
                ? null
                : observableRoundTrips.Sum(sample => sample.RoundTrips!.Value) /
                  (double)observableRoundTrips.Sum(sample => sample.Operations),
            storageGrowth,
            storageGrowth.HasValue && logicalBytes > 0 ? storageGrowth.Value / (double)logicalBytes : null,
            rowGrowth.HasValue && logicalMutations > 0 ? rowGrowth.Value / (double)logicalMutations : null,
            providerWork);
    }
}
