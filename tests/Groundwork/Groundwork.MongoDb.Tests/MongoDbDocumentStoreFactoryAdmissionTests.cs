using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.MongoDb.Documents;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoDbDocumentStoreFactoryAdmissionTests
{
    [Fact]
    public async Task Conventional_factory_rejects_relationships_before_constructing_a_client()
    {
        var manifest = MongoDbTestManifests.MetadataManifest();
        var unit = Assert.Single(manifest.StorageUnits);
        var related = manifest with
        {
            Relationships =
            [
                new ManifestRelationshipDeclaration(
                    "relationship-under-test",
                    unit.Identity,
                    "category",
                    "by-category",
                    unit.Identity,
                    PhysicalDocumentFieldPaths.Id,
                    "by-category")
            ]
        };

        var exception = await Assert.ThrowsAsync<PhysicalRelationshipProviderNotSupportedException>(() =>
            MongoDbDocumentStoreFactory.CreateAsync(
                "not-a-mongodb-connection-string",
                "unused",
                related,
                MongoDbTestManifests.Provider,
                DocumentStoreAccess.Global));

        Assert.Contains("GW-RELATIONSHIP-012", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["relationship-under-test"], exception.RelationshipIdentities);
    }
}
