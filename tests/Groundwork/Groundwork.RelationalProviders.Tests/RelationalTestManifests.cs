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
}
