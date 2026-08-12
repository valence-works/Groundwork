using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.TestInfrastructure;

namespace Groundwork.Sqlite.Tests;

internal static class SqliteTestManifests
{
    public static StorageManifest MetadataManifest() => TestManifests.MetadataManifest();

    public static ProviderIdentity Provider { get; } = new("groundwork-sqlite", "1.0.0");
}
