namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Canonical logical names for collection-element storage: the derived object names for the
/// element table and its two keys, and the fixed column set every element table carries. Both
/// <see cref="PhysicalStorageResolver"/> (which registers the names) and
/// <see cref="ExecutableStorageRouteCompiler"/> (which maps them to executable routes) consume
/// these, so the earlier pipeline stage no longer depends on the later one.
/// </summary>
/// <remarks>
/// Internal by design: providers consume these spellings through compiled
/// <see cref="ExecutableStorageRoute"/> identifiers rather than re-deriving them, so no assembly
/// outside Groundwork.Core's InternalsVisibleTo grants needs this type. Provider literals that
/// merely coincide with these strings (the legacy groundwork_documents DDL, envelope column
/// defaults, and the "ordinal" collation identity) are different concepts and stay provider-owned.
/// </remarks>
internal static class CollectionElementNames
{
    internal const string DocumentKindColumn = "document_kind";
    internal const string StorageScopeColumn = "storage_scope";
    internal const string IdComparisonKeyColumn = "id_comparison_key";
    internal const string IdLookupKeyColumn = "id_lookup_key";
    internal const string OrdinalColumn = "ordinal";
    internal const string ValueColumn = "value";

    /// <summary>The fixed columns of a collection-element table, in canonical order.</summary>
    internal static readonly string[] Columns =
    [
        DocumentKindColumn,
        StorageScopeColumn,
        IdComparisonKeyColumn,
        IdLookupKeyColumn,
        OrdinalColumn,
        ValueColumn
    ];

    internal static string StorageLogicalName(string projectionLogicalName) =>
        $"{projectionLogicalName}__elements";

    internal static string OwnerOrdinalKeyLogicalName(string projectionLogicalName) =>
        $"{projectionLogicalName}__owner_ordinal_unique";

    internal static string MembershipKeyLogicalName(string projectionLogicalName) =>
        $"{projectionLogicalName}__value_owner";

    internal static string FieldLogicalName(string projectionLogicalName, string column) =>
        $"{projectionLogicalName}__{column}";
}
