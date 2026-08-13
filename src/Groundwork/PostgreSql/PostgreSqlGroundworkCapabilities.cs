using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;

namespace Groundwork.PostgreSql;

public static class PostgreSqlGroundworkCapabilities
{
    private static readonly IReadOnlySet<PortableQueryOperation> QueryOperations =
        Enum.GetValues<PortableQueryOperation>().ToHashSet();

    private static readonly IReadOnlySet<ConcurrencyKind> ConcurrencyModes =
        Enum.GetValues<ConcurrencyKind>().ToHashSet();


    public static ProviderIdentity Provider { get; } = new("groundwork-postgresql", "1.0.0");

    /// <summary>PostgreSQL identifier normalization with its schema-global relation namespace and native 63-byte UTF-8 limit.</summary>
    public static IProviderPhysicalNameNormalizer PhysicalNames { get; } =
        PostgreSqlPhysicalNameNormalizer.Instance;

    public static ProviderCapabilityReport Runtime() => Runtime(Provider);

    public static ProviderCapabilityReport Runtime(ProviderIdentity provider) =>
        new(
            provider,
            new HashSet<CapabilityId> { WellKnownCapabilities.AtomicCommit },
            new HashSet<CapabilityId> { WellKnownCapabilities.AtomicCommit },
            IndexCapabilities.All,
            QueryOperations,
            ConcurrencyModes,
            []);


}
