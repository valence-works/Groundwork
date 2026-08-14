using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.Intents;

namespace Groundwork.TestInfrastructure;

/// <summary>
/// Shared builder behind the per-suite manifest helpers (Sqlite, Relational, MongoDb, Sample).
/// The suites differ only in tenancy, whether a spare "by-sort" index exists, StartsWith support on
/// the category surface, and the description list. The unit is fully declared through the current
/// physical-storage surface (logical indexes plus scale-bearing bounded queries under the default
/// policy), so the resolver synthesizes the projected columns and physical indexes and the manifest
/// is materializable as-is on every provider.
/// </summary>
public static class TestManifests
{
    public static StorageManifest MetadataManifest(
        TenancyPolicy? tenancy = null,
        bool extendedIndexOperations = false,
        bool startsWithCategory = false,
        string? description = null)
    {
        var listByCategoryOperations = new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal };
        if (startsWithCategory)
            listByCategoryOperations.Add(PortableQueryOperation.StartsWith);
        var indexes = new List<LogicalIndexDeclaration>
        {
            new(
                "by-key",
                [new IndexField("key")],
                IndexValueKind.Keyword,
                true,
                MissingValueBehavior.Excluded,
                length: 128),
            new(
                "by-category",
                [new IndexField("category")],
                IndexValueKind.String,
                false,
                MissingValueBehavior.Excluded,
                length: 200)
        };
        if (extendedIndexOperations)
        {
            indexes.Add(new LogicalIndexDeclaration(
                "by-sort",
                [new IndexField("sort")],
                IndexValueKind.String,
                false,
                MissingValueBehavior.Excluded,
                length: 200));
        }
        var queries = new List<BoundedQueryDeclaration>
        {
            new(
                "find-by-key",
                "by-key",
                new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                QuerySortSupport.None,
                QueryPagingSupport.None,
                BoundedQueryExecutionClass.ScaleBearing,
                supportsTotalCount: true),
            new(
                "list-by-category",
                "by-category",
                listByCategoryOperations,
                QuerySortSupport.Both,
                QueryPagingSupport.Offset,
                BoundedQueryExecutionClass.ScaleBearing,
                supportsTotalCount: true)
        };
        return new StorageManifest(
            new StorageManifestIdentity("configuration.documents"),
            new StorageManifestOwner("sample.application"),
            new StorageManifestVersion("1.0.0"),
            [
                new StorageUnit(
                    new StorageUnitIdentity("configurationDocument"),
                    "Configuration document",
                    StorageIntent.PortableDocument(),
                    LifecyclePolicy.Mutable,
                    IdentityPolicy.StringId(),
                    tenancy ?? TenancyPolicy.Global,
                    ConcurrencyPolicy.Optimistic(),
                    SerializationPolicy.Json(),
                    new StorageUnitPhysicalStorage(
                        StorageUnitProvisioningMode.Declared,
                        PhysicalStoragePolicy.Default(),
                        indexes,
                        queries))
            ],
            new HashSet<string> { "schema-history", "optimistic-concurrency" },
            description is null ? [] : [description]);
    }

    public static StorageManifest WithUnicodeIdentity(StorageManifest manifest) =>
        manifest with
        {
            StorageUnits =
            [
                manifest.StorageUnits.Single() with
                {
                    IdentityPolicy = IdentityPolicy.StringId(
                        stringCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase)
                }
            ]
        };
}
