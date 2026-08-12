using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.TestInfrastructure;

namespace Groundwork.RelationalProviders.Tests;

internal static class RelationalTestManifests
{
    public static StorageManifest MetadataManifest() =>
        TestManifests.MetadataManifest(extendedIndexOperations: true);

    public static ProviderIdentity SqlServerProvider { get; } = new("groundwork-sqlserver", "1.0.0");
    public static ProviderIdentity PostgreSqlProvider { get; } = new("groundwork-postgresql", "1.0.0");

    public static StorageManifest UnicodeIdentityManifest() =>
        TestManifests.WithUnicodeIdentity(MetadataManifest());

    public static StorageManifest WithIdentityKind(StorageIdentityKind kind)
    {
        var manifest = MetadataManifest();
        return manifest with
        {
            StorageUnits =
            [
                manifest.StorageUnits.Single() with
                {
                    IdentityPolicy = new IdentityPolicy(kind, "id")
                }
            ]
        };
    }

    public static StorageManifest ScopedManifest()
    {
        var manifest = MetadataManifest();
        return manifest with
        {
            Identity = new StorageManifestIdentity("scoped.configuration.documents"),
            StorageUnits =
            [
                manifest.StorageUnits.Single() with
                {
                    Tenancy = TenancyPolicy.Scoped,
                    Physicalization = PhysicalizationPolicy.Optimized
                }
            ]
        };
    }

    public static StorageManifest WithoutIndex(string indexIdentity)
    {
        var unit = MetadataManifest().StorageUnits.Single();
        return MetadataManifest() with
        {
            StorageUnits =
            [
                unit with
                {
                    Indexes = unit.Indexes.Where(index => index.Identity != indexIdentity).ToList(),
                    Queries = unit.Queries.Where(query => query.IndexIdentity != indexIdentity).ToList()
                }
            ]
        };
    }
}
