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

}
