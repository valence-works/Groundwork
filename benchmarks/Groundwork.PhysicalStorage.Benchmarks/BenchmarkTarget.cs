using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using MongoDB.Bson;
using System.Text.Json.Serialization;

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
    BenchmarkProvider Provider,
    PhysicalStorageForm StorageForm,
    string QueryIdentity,
    string PhysicalObject,
    string IndexName,
    string NativePlan,
    IReadOnlyList<string> Assertions)
{
    /// <summary>
    /// Version of the strict sidecar contract written next to every native plan.
    /// This is deliberately independent from the broader benchmark report schema.
    /// </summary>
    public const string SidecarSchemaVersion = "groundwork.physical-storage.native-plan-assertions/v1";

    public string SchemaVersion { get; init; } = SidecarSchemaVersion;
    public NativePlanCommandBinding? CommandBinding { get; init; }
}

public sealed record NativePlanCommandBinding(
    string PhysicalObject,
    string? Alias,
    [property: JsonIgnore]
    string? ParameterizedCommand,
    NativePlanRequestShape? RequestShape = null,
    NativePlanQueryReceipt? QueryReceipt = null)
{
    public NativePlanFieldBinding? Fields { get; init; }
    public string? ParameterizedCommandDigest { get; init; }

    /// <summary>
    /// MongoDB exposes the executed command inside its explain response rather than as SQL.
    /// The retained value is a structurally complete, value-redacted rendering of that command.
    /// </summary>
    public NativePlanMongoCommandReceipt? MongoCommandReceipt { get; init; }
}

public sealed record NativePlanFieldBinding(
    string StorageScope,
    string DocumentKind,
    string Status,
    string Rank,
    string IdentityComparison);

public enum NativePlanTerminalOperation
{
    Documents,
    Count,
    Any,
    First
}

public sealed record NativePlanFilterShape(
    string Field,
    string Operator,
    string Parameter);

public sealed record NativePlanOrderShape(
    string Field,
    PhysicalSortDirection Direction);

public sealed record NativePlanParameterReceipt(
    string Name,
    string Role,
    int? StructuralValue,
    string ValueClassification);

public sealed record NativePlanQueryReceipt(
    NativePlanRequestShape Shape,
    IReadOnlyList<NativePlanParameterReceipt> Parameters)
{
    public static NativePlanQueryReceipt FromRelational(
        BenchmarkPlanRequest request,
        NativePlanAssertionMode assertionMode,
        string parameterizedCommand,
        IReadOnlyList<(string Name, object? Value)> parameters,
        NativePlanFieldBinding fields)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterizedCommand);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(fields);
        var redactedParameters = parameters.Select(Parameter).ToArray();
        var shape = NativePlanRequestShape.FromRelationalCommand(
            request,
            assertionMode,
            parameterizedCommand,
            redactedParameters,
            fields);
        return new NativePlanQueryReceipt(
            shape,
            redactedParameters);
    }

    public static NativePlanQueryReceipt FromMongoDb(
        BenchmarkPlanRequest request,
        NativePlanAssertionMode assertionMode,
        NativePlanMongoCommandReceipt command,
        NativePlanFieldBinding fields)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(fields);
        var parameters = new List<NativePlanParameterReceipt>
        {
            new("scope", "storage-scope", null, "benchmark-tenant"),
            new("kind", "document-kind", null, "benchmark-document-kind"),
            new("q0", "predicate", null, "benchmark-selected-status")
        };
        if (request.Operation == NativePlanOperation.Selection)
        {
            parameters.Add(new("skip", "pagination-skip", request.Skip ?? 0, "structural"));
            if (request.Take is { } take)
                parameters.Add(new("take", "pagination-take", take, "structural"));
        }

        return new NativePlanQueryReceipt(
            NativePlanRequestShape.FromMongoCommand(request, assertionMode, command, fields),
            parameters);
    }

    private static NativePlanParameterReceipt Parameter((string Name, object? Value) parameter) =>
        parameter.Name switch
        {
            "scope" when parameter.Value is string =>
                new(parameter.Name, "storage-scope", null, "benchmark-tenant"),
            "kind" when parameter.Value is string =>
                new(parameter.Name, "document-kind", null, "benchmark-document-kind"),
            "q0" when string.Equals(parameter.Value as string, "open", StringComparison.Ordinal) =>
                new(parameter.Name, "predicate", null, "benchmark-selected-status"),
            "skip" when parameter.Value is not null =>
                new(parameter.Name, "pagination-skip", Convert.ToInt32(parameter.Value), "structural"),
            "take" when parameter.Value is not null =>
                new(parameter.Name, "pagination-take", Convert.ToInt32(parameter.Value), "structural"),
            _ => throw new InvalidOperationException(
                $"Native-plan command contains an unsupported or unredactable parameter '{parameter.Name}'.")
        };
}

/// <summary>
/// A value-redacted copy of the actual MongoDB command passed to <c>explain</c>.
/// Object names, operators, ordering, and pagination remain observable; predicate values do not.
/// </summary>
public sealed record NativePlanMongoCommandReceipt(
    PhysicalDocumentQueryCommandKind Kind,
    string RedactedCommand)
{
    public static NativePlanMongoCommandReceipt FromExplain(
        BsonDocument explain,
        PhysicalDocumentQueryCommandKind kind,
        NativePlanFieldBinding fields)
    {
        ArgumentNullException.ThrowIfNull(explain);
        ArgumentNullException.ThrowIfNull(fields);
        if (!explain.TryGetValue("command", out var commandValue) || !commandValue.IsBsonDocument)
        {
            throw new InvalidOperationException(
                "MongoDB explain evidence did not retain the executed command required for native-plan binding.");
        }

        var command = commandValue.AsBsonDocument;
        if (!NativePlanCommandParsing.MongoCommandContainsSelectedStatus(command, fields))
        {
            throw new InvalidOperationException(
                "MongoDB explain command did not retain the benchmark selected-status predicate.");
        }

        return new NativePlanMongoCommandReceipt(
            kind,
            NativePlanCommandParsing.RedactMongoCommand(command).ToJson());
    }
}

public sealed record NativePlanRequestShape(
    BenchmarkWorkload Workload,
    NativePlanOperation Operation,
    NativePlanTerminalOperation Terminal,
    IReadOnlyList<NativePlanFilterShape> Filters,
    IReadOnlyList<NativePlanOrderShape> Order,
    int? Skip,
    int? Take,
    int QuerySelectivityBasisPoints)
{
    internal static readonly IReadOnlyList<NativePlanFilterShape> CanonicalFilters =
    [
        new("storage-scope", "equal", "scope"),
        new("document-kind", "equal", "kind"),
        new("status", "equal", "q0")
    ];

    public static NativePlanRequestShape For(
        BenchmarkPlanRequest request,
        NativePlanAssertionMode assertionMode)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new NativePlanRequestShape(
            request.Workload,
            request.Operation,
            request.Operation == NativePlanOperation.Selection
                ? NativePlanTerminalOperation.Documents
                : NativePlanTerminalOperation.Count,
            CanonicalFilters,
            request.Operation == NativePlanOperation.Selection && request.Ordered
                ? [new NativePlanOrderShape("rank", PhysicalSortDirection.Descending)]
                : [],
            request.Operation == NativePlanOperation.Selection ? request.Skip : null,
            request.Operation == NativePlanOperation.Selection ? request.Take : null,
            assertionMode == NativePlanAssertionMode.ScanCharacterization
                ? BenchmarkSelectivityPolicy.ScanCharacterizationBasisPoints
                : BenchmarkSelectivityPolicy.IndexedQueryAcceptanceBasisPoints);
    }

    public static NativePlanRequestShape FromRelationalCommand(
        BenchmarkPlanRequest request,
        NativePlanAssertionMode assertionMode,
        string command,
        IReadOnlyList<NativePlanParameterReceipt> parameters,
        NativePlanFieldBinding fields)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(fields);
        var expected = For(request, assertionMode);
        var terminal = NativePlanCommandParsing.IsCount(command)
            ? NativePlanTerminalOperation.Count
            : NativePlanTerminalOperation.Documents;
        var filters = NativePlanCommandParsing.RelationalFilters(command, parameters, fields);
        var order = NativePlanCommandParsing.RelationalOrder(command, fields);
        var pagination = NativePlanCommandParsing.RelationalPagination(command, parameters, terminal);
        return new NativePlanRequestShape(
            request.Workload,
            terminal == NativePlanTerminalOperation.Count
                ? NativePlanOperation.Count
                : NativePlanOperation.Selection,
            terminal,
            filters,
            order,
            pagination.Skip,
            pagination.Take,
            expected.QuerySelectivityBasisPoints);
    }

    public static NativePlanRequestShape FromMongoCommand(
        BenchmarkPlanRequest request,
        NativePlanAssertionMode assertionMode,
        NativePlanMongoCommandReceipt command,
        NativePlanFieldBinding fields)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(fields);
        return NativePlanCommandParsing.MongoShape(request, assertionMode, command, fields);
    }
}

public static class NativePlanEvidenceAssertions
{
    private static readonly IReadOnlyList<string> ScanCharacterization =
    [
        "provider-native plan captured as a non-gating scan characterization",
        "index selection is not required at this selectivity"
    ];

    public static IReadOnlyList<string> For(
        NativePlanAssertionMode assertionMode,
        IReadOnlyList<string> acceptanceAssertions)
    {
        ArgumentNullException.ThrowIfNull(acceptanceAssertions);
        return assertionMode switch
        {
            NativePlanAssertionMode.RequireDeclaredIndex => acceptanceAssertions,
            NativePlanAssertionMode.ScanCharacterization => ScanCharacterization,
            _ => throw new ArgumentOutOfRangeException(nameof(assertionMode), assertionMode, null)
        };
    }

    public static IReadOnlyList<string> ForSqlite(
        NativePlanAssertionMode assertionMode,
        string indexName,
        string nativePlan) =>
        For(
            assertionMode,
            [
                "indexed SEARCH is present",
                $"index {indexName} is selected",
                "linked and primary full-table SCAN stages are absent",
                nativePlan.Contains("USE TEMP B-TREE", StringComparison.OrdinalIgnoreCase)
                    ? "ordering remains server-side with a temporary B-tree for the stable identity suffix"
                    : "ordering is satisfied directly by the selected index"
            ]);

    public static IReadOnlyList<string> ForMongoDb(
        NativePlanAssertionMode assertionMode,
        string indexName) =>
        For(
            assertionMode,
            [
                "winningPlan contains IXSCAN",
                $"winningPlan selects index {indexName}",
                "winningPlan contains no COLLSCAN"
            ]);

    public static IReadOnlyList<string> ForPostgreSql(
        NativePlanAssertionMode assertionMode) =>
        For(
            assertionMode,
            [
                "declared index is selected on the predicate-bearing relation",
                "the predicate-bearing relation is not sequentially scanned",
                "an optimizer-selected scan of a separate primary payload relation is permitted for linked forms",
                "query shape is rendered by the certified production handler"
            ]);

    public static IReadOnlyList<string> ForSqlServer(
        NativePlanAssertionMode assertionMode) =>
        For(
            assertionMode,
            [
                "declared index is selected",
                "table and index scans are absent",
                "query shape is rendered by the certified production handler"
            ]);

    public static bool Matches(
        NativePlanAssertionMode assertionMode,
        BenchmarkPlanRequest request,
        BenchmarkProvider provider,
        string indexName,
        string physicalObject,
        NativePlanCommandBinding? commandBinding,
        string nativePlan,
        IReadOnlyList<string>? assertions)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(indexName) ||
            string.IsNullOrWhiteSpace(physicalObject) ||
            string.IsNullOrWhiteSpace(nativePlan) ||
            assertions is null)
            return false;

        var expected = provider switch
        {
            BenchmarkProvider.Sqlite => ForSqlite(assertionMode, indexName, nativePlan),
            BenchmarkProvider.MongoDb => ForMongoDb(assertionMode, indexName),
            BenchmarkProvider.PostgreSql => ForPostgreSql(assertionMode),
            BenchmarkProvider.SqlServer => ForSqlServer(assertionMode),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };
        if (!expected.SequenceEqual(assertions, StringComparer.Ordinal))
            return false;
        if (!NativePlanEvidenceValidator.Matches(
                assertionMode,
                request,
                provider,
                physicalObject,
                indexName,
                commandBinding,
                nativePlan))
        {
            return false;
        }
        if (assertionMode == NativePlanAssertionMode.ScanCharacterization)
            return true;

        return provider switch
        {
            BenchmarkProvider.Sqlite => SqliteBenchmarkTarget.UsesDeclaredIndexWithoutScanningRelations(nativePlan, indexName),
            BenchmarkProvider.MongoDb => MongoDbBenchmarkTarget.UsesDeclaredIndex(nativePlan, indexName),
            BenchmarkProvider.PostgreSql => PostgreSqlBenchmarkTarget.UsesDeclaredIndexWithoutScanningIndexedRelation(
                nativePlan,
                indexName,
                physicalObject),
            BenchmarkProvider.SqlServer => SqlServerBenchmarkTarget.UsesDeclaredIndex(
                nativePlan,
                indexName,
                physicalObject),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
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

    public bool MeetsConfiguredContention(int configuredParallelism, int? expectedWaveCount = null) =>
        configuredParallelism > 0 &&
        (!expectedWaveCount.HasValue || expectedWaveCount.Value > 0 && WaveCount == expectedWaveCount.Value) &&
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
