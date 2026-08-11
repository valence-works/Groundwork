using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Store;

namespace Groundwork.RelationalProviders.Tests;

/// <summary>
/// The shared route shape behind valence-works/Groundwork#165: a bounded query over a category index
/// that may or may not be unique, over a column that may or may not be nullable. Only that pairing
/// produces a null-excluding index, and only SQL Server acts on it.
/// </summary>
internal static class NullExcludedIndexScenario
{
    public static DocumentQueryComparison Contains() => DocumentQueryComparison.Contains("category", "lph");
    public static DocumentQueryComparison Equality() => DocumentQueryComparison.Equal("category", "alpha");
    public static DocumentQueryComparison NullEquality() => DocumentQueryComparison.Equal("category", null);

    public static DocumentQuery Query(DocumentQueryComparison comparison) =>
        new("configurationDocument", "list-by-category", [DocumentQueryClause.Of(comparison)]);

    public static (StorageManifest Manifest, PhysicalSchemaTarget Target) Create(
        PhysicalStorageForm form,
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        bool unique,
        bool nullable,
        DocumentQueryComparison comparison) =>
        RelationalPhysicalStorageTestModels.Create(
            form,
            provider,
            includePriority: false,
            instance: Guid.NewGuid().ToString("N")[..8],
            normalizer: normalizer,
            categoryUnique: unique,
            categoryNullable: nullable,
            categoryOperations: comparison.Operator == QueryComparisonOperator.Contains
                ? new HashSet<PortableQueryOperation>
                {
                    PortableQueryOperation.Equal,
                    PortableQueryOperation.Contains
                }
                : new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal });

    /// <summary>The physical identifier of the projected category column on the index's own object.</summary>
    public static string CategoryColumn(ExecutableStorageRoute route) =>
        route.ProjectedColumns
            .Single(column => column.Definition.LogicalName == "category")
            .Column.Identifier;

    public static ExecutablePhysicalIndexRoute CategoryIndex(ExecutableStorageRoute route) =>
        route.Indexes.Single(index => index.Identity == "by-category");
}
