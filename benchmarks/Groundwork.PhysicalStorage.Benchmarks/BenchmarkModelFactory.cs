using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record BenchmarkPhysicalModel(
    StorageManifest Manifest,
    PhysicalSchemaTarget Target,
    ExecutableStorageRoute Route);

public static class BenchmarkModelFactory
{
    public const string DocumentKind = "benchmarkItem";
    public const string IndexedQueryIdentity = "list-by-status";
    public const string QueryIdentity = "list-by-status-rank";
    public const string IndexedIndexIdentity = "by-status";
    public const string CompoundIndexIdentity = "by-status-rank";

    public static string QueryIdentityFor(bool ordered) =>
        ordered ? QueryIdentity : IndexedQueryIdentity;

    public static string IndexIdentityFor(bool ordered) =>
        ordered ? CompoundIndexIdentity : IndexedIndexIdentity;

    public static ExecutablePhysicalIndexRoute IndexFor(
        ExecutableStorageRoute route,
        bool ordered)
    {
        ArgumentNullException.ThrowIfNull(route);
        var identity = IndexIdentityFor(ordered);
        return route.Indexes.Single(index => index.Identity == identity);
    }

    public static IReadOnlyList<ExecutablePhysicalIndexRoute> PlanIndexCandidatesFor(
        ExecutableStorageRoute route,
        BenchmarkPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        var expected = IndexFor(route, request.Ordered);
        if (request.Operation != NativePlanOperation.Count)
            return [expected];

        var predicateIndex = IndexFor(route, ordered: false);
        var predicatePrefix = predicateIndex.Columns.Take(predicateIndex.Columns.Count - 1).ToArray();
        return route.Indexes
            .Where(candidate =>
                candidate.Target == expected.Target &&
                candidate.Columns.Count >= predicatePrefix.Length &&
                candidate.Columns.Take(predicatePrefix.Length).Select(ColumnShape)
                    .SequenceEqual(predicatePrefix.Select(ColumnShape)))
            .ToArray();

        static (string LogicalName, PhysicalSortDirection Direction) ColumnShape(
            ExecutableIndexColumnRoute column) =>
            (column.Column.LogicalName, column.Direction);
    }

    public static NativePlanFieldBinding FieldBindingFor(ExecutableStorageRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var queryRelationship = route.LinkedRelationship;
        return new NativePlanFieldBinding(
            queryRelationship?.StorageScope.Identifier ?? route.ScopeKey.Column.Identifier,
            queryRelationship?.DocumentKind.Identifier ?? route.Discriminator.Column.Identifier,
            route.ProjectedColumns.Single(column => column.Definition.Path == "status").Column.Identifier,
            route.ProjectedColumns.Single(column => column.Definition.Path == "rank").Column.Identifier,
            queryRelationship?.Identity.ComparisonKey.Identifier ?? route.Envelope.Identity.ComparisonKey.Identifier);
    }

    public static StorageManifest CreateManifest(
        PhysicalStorageForm form,
        string instance,
        bool includeCategory = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        var binding = new SharedStorageBinding("benchmark-runtime");
        var columns = new List<ProjectedColumnDefinition>
        {
            // Scope (256 bytes) + status (90) + rank (4) + comparison identity (1350)
            // reaches SQL Server's 1700-byte key limit without weakening the stable-order tail.
            new("status", "status", PortablePhysicalType.String, Length: 45, IsNullable: false),
            new("rank", "rank", PortablePhysicalType.Int32, IsNullable: false)
        };
        if (includeCategory)
            columns.Add(new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String, Length: 64));

        var indexedIndex = new PhysicalIndexDefinition(
            IndexedIndexIdentity,
            [
                new PhysicalIndexColumnDefinition("storage_scope", 0),
                new PhysicalIndexColumnDefinition("status", 1),
                new PhysicalIndexColumnDefinition("id_comparison_key", 2)
            ]);
        var orderedIndex = new PhysicalIndexDefinition(
            CompoundIndexIdentity,
            [
                new PhysicalIndexColumnDefinition("storage_scope", 0),
                new PhysicalIndexColumnDefinition("status", 1),
                new PhysicalIndexColumnDefinition("rank", 2, PhysicalSortDirection.Descending),
                new PhysicalIndexColumnDefinition("id_comparison_key", 3)
            ]);
        var table = form switch
        {
            PhysicalStorageForm.SharedDocuments => PhysicalTableDefinition.SharedDocuments(
                binding,
                columns,
                [indexedIndex, orderedIndex],
                linkedProjectionLogicalName: "benchmark_items_lookup"),
            PhysicalStorageForm.DedicatedDocumentTable => PhysicalTableDefinition.DedicatedDocumentTable(
                "benchmark_items",
                indexes: [indexedIndex, orderedIndex],
                linkedProjectedColumns: columns,
                linkedProjectionLogicalName: "benchmark_items_lookup"),
            PhysicalStorageForm.PhysicalEntityTable => PhysicalTableDefinition.PhysicalEntityTable(
                "benchmark_items",
                columns,
                indexes: [indexedIndex, orderedIndex]),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
        // Both indexes take the default IncludedAsNull, matching the physical definitions above.
        // Every benchmark document carries status and rank, and both projected columns are
        // non-nullable, so a null-excluding index would exclude nothing while still making the
        // provider build a filtered/partial index. That is a different physical structure with a
        // different plan from the one the harness means to measure.
        var indexedLogical = new LogicalIndexDeclaration(
            IndexedIndexIdentity,
            [new IndexField("status", IndexValueKind.Keyword)],
            IndexValueKind.Keyword,
            false);
        var orderedLogical = new LogicalIndexDeclaration(
            CompoundIndexIdentity,
            [new IndexField("status", IndexValueKind.Keyword), new IndexField("rank", IndexValueKind.Number)],
            IndexValueKind.Keyword,
            false);
        var indexedQuery = QueryDeclaration(IndexedQueryIdentity, indexedLogical.Identity, ordered: false);
        var orderedQuery = QueryDeclaration(QueryIdentity, orderedLogical.Identity, ordered: true);
        var unit = new StorageUnit(
            new StorageUnitIdentity(DocumentKind),
            "Physical storage benchmark item",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            [],
            [],
            PhysicalizationPolicy.Portable)
        {
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(table),
                [indexedLogical, orderedLogical],
                [indexedQuery, orderedQuery])
        };
        return new StorageManifest(
            new StorageManifestIdentity($"benchmark.{instance}"),
            new StorageManifestOwner("groundwork-benchmark-harness"),
            new StorageManifestVersion(includeCategory ? "2" : "1"),
            [unit],
            new HashSet<string>(),
            [])
        {
            SharedDocumentStorages = form == PhysicalStorageForm.SharedDocuments
                ? [new SharedDocumentStorageDefinition(binding, "benchmark_documents", new DocumentEnvelopeDefinition())]
                : []
        };
    }

    private static BoundedQueryDeclaration QueryDeclaration(
        string identity,
        string indexIdentity,
        bool ordered) =>
        new(
            identity,
            indexIdentity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            ordered ? QuerySortSupport.Descending : QuerySortSupport.None,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true,
            sortFields: ordered ? [new BoundedQuerySortField("rank", PhysicalSortDirection.Descending)] : [],
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "status",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ],
            resultOperations: new HashSet<BoundedQueryResultOperation>
            {
                BoundedQueryResultOperation.Documents,
                BoundedQueryResultOperation.Count,
                BoundedQueryResultOperation.Any,
                BoundedQueryResultOperation.First
            });

    public static BenchmarkPhysicalModel CompileRelational(
        PhysicalStorageForm form,
        string instance,
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        bool includeCategory = true)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(normalizer);
        var manifest = CreateManifest(form, instance, includeCategory);
        var target = PhysicalSchemaTargetCompiler.Compile(
            manifest,
            provider,
            normalizer,
            NamePolicy(instance));
        var route = target.Routes.Single();
        return new BenchmarkPhysicalModel(
            manifest,
            target,
            route);
    }

    public static IPhysicalNamePolicy NamePolicy(string instance) =>
        new DelegatePhysicalNamePolicy(context => $"gw_bench_{Sanitize(instance)}_{context.FeatureDefaultLogicalName}");

    private static string Sanitize(string value) =>
        new(value.Where(character => char.IsAsciiLetterOrDigit(character) || character == '_').ToArray());
}
