using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;

namespace Groundwork.SqlServer;

public static class SqlServerGroundworkCapabilities
{
    private static readonly IReadOnlySet<PortableQueryOperation> QueryOperations =
        Enum.GetValues<PortableQueryOperation>().ToHashSet();

    private static readonly IReadOnlySet<ConcurrencyKind> ConcurrencyModes =
        Enum.GetValues<ConcurrencyKind>().ToHashSet();

    private static readonly IReadOnlySet<IndexValueKind> IndexValueKinds =
        new HashSet<IndexValueKind>
        {
            IndexValueKind.String,
            IndexValueKind.Number,
            IndexValueKind.Boolean,
            IndexValueKind.DateTime,
            IndexValueKind.Keyword
        };

    private static readonly IReadOnlySet<MissingValueBehavior> MissingValueBehaviors =
        Enum.GetValues<MissingValueBehavior>().ToHashSet();


    public static ProviderIdentity Provider { get; } = new("groundwork-sqlserver", "1.0.0");

    /// <summary>
    /// SQL Server identifier normalization with its native 128-character limit. No collision-scope
    /// delegate is supplied, so SQL Server uses the per-table Core default
    /// (<see cref="ProviderPhysicalNameNormalizerDefaults"/>): index names collide per storage unit
    /// and schema history is its own scope, whereas PostgreSQL and SQLite fold relations, indexes,
    /// and schema history into one flat namespace ("schema-relations" / "schema-objects"). The
    /// per-table model matches T-SQL's table-scoped index namespace, but the divergence from the
    /// flat-namespace providers is recorded here rather than decided.
    /// </summary>
    public static IProviderPhysicalNameNormalizer PhysicalNames { get; } =
        new DelegateProviderPhysicalNameNormalizer(context => SqlServerPhysicalName.Normalize(context.LogicalName));

    public static ProviderCapabilityReport Runtime() => Runtime(Provider);

    public static ProviderCapabilityReport Runtime(ProviderIdentity provider) =>
        new(
            provider,
            new HashSet<CapabilityId> { WellKnownCapabilities.AtomicCommit },
            new HashSet<CapabilityId> { WellKnownCapabilities.AtomicCommit },
            new IndexCapabilities(
                IndexValueKinds,
                SupportsUniqueIndexes: true,
                SupportsSortableIndexes: true,
                MissingValueBehaviors),
            QueryOperations,
            ConcurrencyModes,
            []);


}
