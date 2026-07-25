using Groundwork.Core.PhysicalStorage;

namespace Groundwork.PhysicalStorage.Benchmarks;

public enum NativePlanOperation
{
    Selection,
    Count
}

public sealed record BenchmarkPlanRequest(
    BenchmarkWorkload Workload,
    NativePlanOperation Operation,
    bool Ordered,
    int? Skip,
    int? Take);

public static class BenchmarkPlanRequests
{
    private static readonly IReadOnlyList<BenchmarkPlanRequest> Canonical =
    [
        new(BenchmarkWorkload.IndexedQuery, NativePlanOperation.Selection, Ordered: false, Skip: null, Take: 20),
        new(BenchmarkWorkload.IndexedQuery, NativePlanOperation.Count, Ordered: false, Skip: null, Take: 20),
        new(BenchmarkWorkload.MixedCompoundOrdering, NativePlanOperation.Selection, Ordered: true, Skip: null, Take: 20),
        new(BenchmarkWorkload.MixedCompoundOrdering, NativePlanOperation.Count, Ordered: true, Skip: null, Take: 20),
        new(BenchmarkWorkload.PaginationAndCount, NativePlanOperation.Selection, Ordered: true, Skip: 7, Take: 20),
        new(BenchmarkWorkload.PaginationAndCount, NativePlanOperation.Count, Ordered: true, Skip: 7, Take: 20)
    ];

    public static IReadOnlyList<BenchmarkPlanRequest> ForWorkloads(IEnumerable<BenchmarkWorkload> workloads)
    {
        ArgumentNullException.ThrowIfNull(workloads);
        var selected = workloads.ToHashSet();
        return Canonical.Where(request => selected.Contains(request.Workload)).ToArray();
    }
}

public sealed record NativePlanEvidence(
    BenchmarkPlanRequest Request,
    string Provider,
    string StorageForm,
    string QueryIdentity,
    string PhysicalObject,
    string IndexName,
    string NativePlan,
    IReadOnlyList<string> Assertions);

public static class NativePlanEvidenceAssertions
{
    public static IReadOnlyList<string> For(
        NativePlanAssertionMode assertionMode,
        IReadOnlyList<string> acceptanceAssertions)
    {
        ArgumentNullException.ThrowIfNull(acceptanceAssertions);
        return assertionMode switch
        {
            NativePlanAssertionMode.RequireDeclaredIndex => acceptanceAssertions,
            NativePlanAssertionMode.ScanCharacterization =>
            [
                "provider-native plan captured as a non-gating scan characterization",
                "index selection is not required at this selectivity"
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(assertionMode), assertionMode, null)
        };
    }
}

public sealed record CorrectnessGateResult(
    bool ScopeIsolation,
    bool OptimisticConcurrency,
    bool UnitOfWorkRollback,
    bool BoundedQuery,
    bool MixedOrdering);

/// <summary>
/// Evidence emitted by one concurrent-create measurement. Released-together
/// waves prove synchronized contention demand and exact outcomes. The in-flight
/// value spans the public production-store call window and is retained only as
/// provider characterization; it does not prove physical database overlap.
/// </summary>
public sealed record ConcurrentLoadEvidence(
    int RequestedParallelism,
    long WaveCount,
    long ReleasedTogetherWaveCount,
    long Attempts,
    long Completions,
    long SuccessfulOperations,
    long ConflictOperations,
    int PeakInFlightProductionStoreCalls)
{
    public bool IsInternallyConsistent()
    {
        if (RequestedParallelism <= 0 ||
            WaveCount <= 0 ||
            ReleasedTogetherWaveCount < 0 ||
            ReleasedTogetherWaveCount > WaveCount ||
            PeakInFlightProductionStoreCalls <= 0 ||
            PeakInFlightProductionStoreCalls > RequestedParallelism)
        {
            return false;
        }

        try
        {
            var expectedAttempts = checked(WaveCount * RequestedParallelism);
            var expectedConflicts = checked(WaveCount * (RequestedParallelism - 1L));
            return Attempts == expectedAttempts &&
                   Completions == expectedAttempts &&
                   SuccessfulOperations == WaveCount &&
                   ConflictOperations == expectedConflicts &&
                   checked(SuccessfulOperations + ConflictOperations) == Completions;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public bool MeetsConfiguredContention(int configuredParallelism) =>
        configuredParallelism > 0 &&
        IsInternallyConsistent() &&
        RequestedParallelism == configuredParallelism &&
        ReleasedTogetherWaveCount == WaveCount;
}

/// <summary>
/// Records only production-store call windows. Callers must enter immediately
/// before invoking the store and dispose after that invocation completes; work
/// waiting at a start barrier is deliberately not counted as in-flight.
/// </summary>
internal sealed class ConcurrentLoadEvidenceCollector
{
    private readonly int requestedParallelism;
    private long attempts;
    private long completions;
    private long successfulOperations;
    private long conflictOperations;
    private int inFlightProductionStoreCalls;
    private int peakInFlightProductionStoreCalls;
    private long waveCount;
    private long releasedTogetherWaveCount;
    private long attemptsAtWaveStart;
    private bool waveActive;

    public ConcurrentLoadEvidenceCollector(int requestedParallelism)
    {
        if (requestedParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(requestedParallelism));
        this.requestedParallelism = requestedParallelism;
    }

    public void BeginWave()
    {
        if (waveActive || Volatile.Read(ref inFlightProductionStoreCalls) != 0)
            throw new InvalidOperationException("A concurrent-load wave cannot start before the preceding call window closes.");
        attemptsAtWaveStart = Interlocked.Read(ref attempts);
        waveActive = true;
    }

    public IDisposable EnterProductionStoreCall()
    {
        Interlocked.Increment(ref attempts);
        var observed = Interlocked.Increment(ref inFlightProductionStoreCalls);
        RecordPeak(ref peakInFlightProductionStoreCalls, observed);
        return new ProductionStoreCall(this);
    }

    public void CompleteWave(long successful, long conflicts, bool releasedTogether)
    {
        if (!waveActive || successful != 1 || conflicts != requestedParallelism - 1 ||
            Volatile.Read(ref inFlightProductionStoreCalls) != 0 ||
            Interlocked.Read(ref attempts) - attemptsAtWaveStart != requestedParallelism)
        {
            throw new InvalidOperationException("Concurrent-load wave did not complete one fully accounted contention attempt.");
        }
        Interlocked.Add(ref successfulOperations, successful);
        Interlocked.Add(ref conflictOperations, conflicts);
        Interlocked.Add(ref completions, checked(successful + conflicts));
        Interlocked.Increment(ref waveCount);
        if (releasedTogether)
            Interlocked.Increment(ref releasedTogetherWaveCount);
        waveActive = false;
    }

    public ConcurrentLoadEvidence Build()
    {
        if (waveActive || Volatile.Read(ref inFlightProductionStoreCalls) != 0)
            throw new InvalidOperationException("Concurrent-load evidence cannot be sealed while a production-store call is in flight.");
        return new(
            requestedParallelism,
            Interlocked.Read(ref waveCount),
            Interlocked.Read(ref releasedTogetherWaveCount),
            Interlocked.Read(ref attempts),
            Interlocked.Read(ref completions),
            Interlocked.Read(ref successfulOperations),
            Interlocked.Read(ref conflictOperations),
            Volatile.Read(ref peakInFlightProductionStoreCalls));
    }

    private void ExitProductionStoreCall() =>
        Interlocked.Decrement(ref inFlightProductionStoreCalls);

    private static void RecordPeak(ref int peak, int observed)
    {
        while (true)
        {
            var current = Volatile.Read(ref peak);
            if (observed <= current || Interlocked.CompareExchange(ref peak, observed, current) == current)
                return;
        }
    }

    private sealed class ProductionStoreCall(ConcurrentLoadEvidenceCollector owner) : IDisposable
    {
        private ConcurrentLoadEvidenceCollector? owner = owner;

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ExitProductionStoreCall();
    }
}

public sealed record WorkloadExecution(
    int Operations,
    long LogicalPayloadBytes,
    long LogicalMutations,
    long? RoundTrips,
    IReadOnlyDictionary<string, long> ProviderWork,
    IReadOnlyList<long> OperationLatencyNanoseconds,
    BenchmarkObservableResultVector? ObservableResultVector = null)
{
    public ConcurrentLoadEvidence? ConcurrentLoad { get; init; }
}

public interface IPhysicalStorageBenchmarkTarget : IAsyncDisposable
{
    BenchmarkProvider Provider { get; }
    PhysicalStorageForm StorageForm { get; }
    string ProviderVersion { get; }
    IReadOnlyDictionary<string, string> ProviderConfiguration { get; }
    DatabaseSignalTarget SignalTarget { get; }

    Task InitializeAsync(CancellationToken cancellationToken);
    Task SeedAsync(int seed, BenchmarkDataShape shape, CancellationToken cancellationToken);
    Task<CorrectnessGateResult> RunCorrectnessGateAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<NativePlanEvidence>> CaptureNativePlansAsync(
        IReadOnlyList<BenchmarkPlanRequest> requests,
        NativePlanAssertionMode assertionMode,
        CancellationToken cancellationToken);
    Task PrepareWorkloadAsync(BenchmarkWorkload workload, int totalIterations, int operationsPerIteration, CancellationToken cancellationToken);
    Task PrepareIterationAsync(BenchmarkWorkload workload, int iteration, CancellationToken cancellationToken);
    Task<WorkloadExecution> ExecuteAsync(BenchmarkWorkload workload, int iteration, int operations, int concurrency, CancellationToken cancellationToken);
    Task ValidateIterationAsync(BenchmarkWorkload workload, CancellationToken cancellationToken);
    Task<StorageSnapshot> CaptureStorageAsync(CancellationToken cancellationToken);
}
