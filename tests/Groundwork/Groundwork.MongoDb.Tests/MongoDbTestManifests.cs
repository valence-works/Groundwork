using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;
using Groundwork.Core.Intents;
using Groundwork.TestInfrastructure;

namespace Groundwork.MongoDb.Tests;

internal static class MongoDbTestManifests
{
    public static StorageManifest MetadataManifest() =>
        TestManifests.MetadataManifest(extendedIndexOperations: true);

    public static ProviderIdentity Provider { get; } = new("groundwork-mongodb", "1.0.0");

    public static StorageManifest AtomicCommitManifest()
    {
        var manifest = MetadataManifest();
        return manifest with
        {
            StorageUnits =
            [
                manifest.StorageUnits.Single() with
                {
                    Intent = StorageIntent.Operational(
                        "Configuration changes require an atomic commit.",
                        WorkloadIntent.RuntimeContinuationState,
                        WellKnownCapabilities.AtomicCommit)
                }
            ]
        };
    }

    public static StorageManifest UnicodeIdentityManifest() =>
        TestManifests.WithUnicodeIdentity(MetadataManifest());

    public static StorageManifest TenantManifest() =>
        new(
            new StorageManifestIdentity("closed.query.scoped"),
            new StorageManifestOwner("sample.application"),
            new StorageManifestVersion("1.0.0"),
            [
                new StorageUnit(
                    new StorageUnitIdentity("scopedDocument"),
                    "Scoped document",
                    StorageIntent.PortableDocument(),
                    LifecyclePolicy.Mutable,
                    IdentityPolicy.StringId(),
                    TenancyPolicy.Scoped,
                    ConcurrencyPolicy.Optimistic(),
                    SerializationPolicy.Json(),
                    [
                        new IndexDeclaration(
                            "by-name",
                            [new IndexField("name")],
                            IndexValueKind.String,
                            false,
                            true,
                            MissingValueBehavior.Excluded,
                            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal, PortableQueryOperation.Contains })
                    ],
                    [],
                    PhysicalizationPolicy.Portable)
            ],
            new HashSet<string> { "schema-history", "optimistic-concurrency" },
            []);
}
