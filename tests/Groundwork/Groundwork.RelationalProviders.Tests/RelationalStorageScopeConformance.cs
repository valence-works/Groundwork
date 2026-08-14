using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.TestInfrastructure;

namespace Groundwork.RelationalProviders.Tests;

/// <summary>
/// Runs the shared storage-scope black-box contract against a relational server provider. Each
/// provider suite supplies only its schema executor, physical store, and bounded query runtime;
/// the scoped metadata manifest and per-manifest schema application are shared. Physical names are
/// prefixed so the suite coexists with the other models in the class's shared container database.
/// </summary>
internal static class RelationalStorageScopeConformance
{
    public static async Task VerifyAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<PhysicalSchemaTarget, Task> applyAsync,
        Func<StorageManifest, PhysicalSchemaTarget, DocumentStoreAccess, IDocumentStore> createStore,
        Func<IDocumentStore, StorageManifest, ExecutableStorageRoute, ProviderIdentity, IBoundedDocumentStore> createQueries)
    {
        var manifest = ScopedManifest();
        var namePolicy = new DelegatePhysicalNamePolicy(context => $"gwscope_{context.FeatureDefaultLogicalName}");
        var applied = new Dictionary<StorageManifest, PhysicalSchemaTarget>();

        async Task<IDocumentStore> CreateStoreAsync(StorageManifest targetManifest, DocumentStoreAccess access)
        {
            if (!applied.TryGetValue(targetManifest, out var target))
            {
                target = PhysicalSchemaTargetCompiler.Compile(targetManifest, provider, normalizer, namePolicy);
                await applyAsync(target);
                applied[targetManifest] = target;
            }

            return createStore(targetManifest, target, access);
        }

        await StorageScopeDocumentStoreConformance.VerifyAsync(
            manifest,
            CreateStoreAsync,
            (store, targetManifest) => createQueries(
                store,
                targetManifest,
                applied[targetManifest].Routes.Single(route =>
                    route.StorageUnit.Value == targetManifest.StorageUnits[0].Identity.Value),
                provider));
    }

    private static StorageManifest ScopedManifest()
    {
        var template = TestManifests.MetadataManifest(tenancy: TenancyPolicy.Scoped);
        var unit = template.StorageUnits.Single();
        var physical = unit.PhysicalStorage!;
        // The scope contract only exercises the unique "find-by-key" surface. Dropping the wide
        // "by-category" declaration keeps the scope-led synthesized index (scope, category,
        // id-comparison-key) from exceeding SQL Server's 1700-byte index key cap.
        return template with
        {
            Identity = new StorageManifestIdentity("scoped.metadata"),
            StorageUnits =
            [
                unit with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        physical.ProvisioningMode,
                        physical.Policy,
                        physical.LogicalIndexes.Where(index => index.Identity == "by-key").ToArray(),
                        physical.BoundedQueries.Where(query => query.Identity == "find-by-key").ToArray())
                }
            ]
        };
    }
}
