using Groundwork.Core.Manifests;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using Groundwork.TestInfrastructure;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoDbStorageScopeTests(MongoDbReplicaSetTestContainer fixture)
    : IClassFixture<MongoDbReplicaSetTestContainer>
{
    private readonly string connectionString = fixture.ConnectionString;

    [Fact]
    public async Task SatisfiesSharedStorageScopeBlackBoxContract()
    {
        var database = new MongoClient(connectionString).GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var manifest = ScopedManifest();
        var materialized = new Dictionary<StorageManifest, MongoDbPhysicalStorageModel>();

        async Task<IDocumentStore> CreateStoreAsync(StorageManifest targetManifest, DocumentStoreAccess access)
        {
            if (!materialized.TryGetValue(targetManifest, out var model))
            {
                model = MongoDbPhysicalStorageModel.Compile(targetManifest, MongoDbTestManifests.Provider);
                await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
                materialized[targetManifest] = model;
            }

            return new MongoDbPhysicalDocumentStore(database, model, access);
        }

        await StorageScopeDocumentStoreConformance.VerifyAsync(manifest, CreateStoreAsync);
    }

    private static StorageManifest ScopedManifest()
    {
        var manifest = MongoDbTestManifests.MetadataManifest();
        return manifest with
        {
            Identity = new StorageManifestIdentity("scoped.metadata"),
            StorageUnits =
            [
                manifest.StorageUnits.Single() with
                {
                    Tenancy = TenancyPolicy.Scoped
                }
            ]
        };
    }
}
