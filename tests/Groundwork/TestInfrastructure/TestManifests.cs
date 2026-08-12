using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;
using Groundwork.Core.Intents;

namespace Groundwork.TestInfrastructure;

/// <summary>
/// Shared builder behind the per-suite manifest helpers (Sqlite, Relational, MongoDb, Sample).
/// The suites differ only in tenancy, the operation sets admitted by the shared indexes, whether a
/// spare "by-sort" index exists, StartsWith support on the category surface, and the description list.
/// </summary>
public static class TestManifests
{
    public static StorageManifest MetadataManifest(
        TenancyPolicy? tenancy = null,
        bool extendedIndexOperations = false,
        bool startsWithCategory = false,
        string? description = null)
    {
        var categoryOperations = new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal };
        if (extendedIndexOperations)
            categoryOperations.Add(PortableQueryOperation.In);
        if (startsWithCategory)
            categoryOperations.Add(PortableQueryOperation.StartsWith);
        var listByCategoryOperations = new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal };
        if (startsWithCategory)
            listByCategoryOperations.Add(PortableQueryOperation.StartsWith);
        var keyOperations = new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal };
        if (extendedIndexOperations)
        {
            keyOperations.Add(PortableQueryOperation.In);
            keyOperations.Add(PortableQueryOperation.Contains);
            keyOperations.Add(PortableQueryOperation.NotContains);
        }
        var indexes = new List<IndexDeclaration>
        {
            new(
                "by-key",
                [new IndexField("key")],
                IndexValueKind.Keyword,
                true,
                true,
                MissingValueBehavior.Excluded,
                keyOperations),
            new(
                "by-category",
                [new IndexField("category")],
                IndexValueKind.String,
                false,
                true,
                MissingValueBehavior.Excluded,
                categoryOperations)
        };
        if (extendedIndexOperations)
        {
            indexes.Add(new IndexDeclaration(
                "by-sort",
                [new IndexField("sort")],
                IndexValueKind.String,
                false,
                true,
                MissingValueBehavior.Excluded,
                new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }));
        }
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
                    indexes,
                    [
                        new PortableQueryDeclaration(
                            "find-by-key",
                            "by-key",
                            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                            QuerySortSupport.None,
                            QueryPagingSupport.None),
                        new PortableQueryDeclaration(
                            "list-by-category",
                            "by-category",
                            listByCategoryOperations,
                            QuerySortSupport.Both,
                            QueryPagingSupport.Offset)
                    ],
                    PhysicalizationPolicy.Portable)
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
