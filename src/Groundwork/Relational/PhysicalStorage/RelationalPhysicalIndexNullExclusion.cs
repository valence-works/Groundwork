using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Relational.PhysicalStorage;

/// <summary>
/// The single definition of which of an index's key columns a provider may exclude nulls from.
/// </summary>
/// <remarks>
/// Two independent decisions depend on this answer and must never disagree. The schema side uses it to
/// emit an index filter (a unique index over a nullable column needs one on SQL Server, whose unique
/// indexes treat nulls as equal to one another). The query side uses it to decide whether an index hint
/// can be honoured: an index that excludes nulls only serves predicates that provably reject them.
/// When the two sides drift, the result is a query the provider refuses to plan, or worse, one that
/// silently omits the excluded rows.
/// </remarks>
internal static class RelationalPhysicalIndexNullExclusion
{
    /// <summary>
    /// The nullable projected key columns of <paramref name="index"/>, ordered, or empty when the index
    /// keys no nullable column. Callers decide whether the index actually excludes those nulls; this
    /// only reports which columns could be excluded.
    /// </summary>
    public static string[] Columns(ExecutableStorageRoute route, ExecutablePhysicalIndexRoute index)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(index);
        var indexed = index.Columns.Select(column => column.Column.Identifier).ToHashSet(StringComparer.Ordinal);
        return route.ProjectedColumns
            .Where(column => column.Target == index.Target &&
                             column.Definition.IsNullable &&
                             indexed.Contains(column.Column.Identifier))
            .Select(column => column.Column.Identifier)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
