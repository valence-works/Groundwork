using Groundwork.Core.Indexing;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// The single definition of which of an index's key columns exclude rows that have no value.
/// </summary>
/// <remarks>
/// <para>
/// Row exclusion is declared, once, by <see cref="MissingValueBehavior"/>, and every provider answers
/// this question the same way. That matters because three separate decisions depend on the answer and
/// must never disagree: the schema side emits the index filter, the query side decides whether pinning
/// the index is sound, and validation compares what was emitted against what was declared.
/// </para>
/// <para>
/// It deliberately does not consider uniqueness. SQL Server unique indexes treat nulls as equal to one
/// another, so a unique index over a nullable column used to acquire a null-excluding filter as a side
/// effect of that emulation — which silently turned a constraint choice into a row-visibility choice,
/// and made the same manifest mean different things on different providers.
/// </para>
/// </remarks>
public static class PhysicalIndexNullExclusion
{
    /// <summary>
    /// The ordered key columns whose missing values <paramref name="index"/> excludes rows for, or empty
    /// when it indexes every row. Only nullable projected columns can be excluded: a non-nullable column
    /// is always present, so filtering on it would restrict nothing while making the filter harder to
    /// match.
    /// </summary>
    public static string[] Columns(ExecutableStorageRoute route, ExecutablePhysicalIndexRoute index)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(index);
        if (index.MissingValueBehavior != MissingValueBehavior.Excluded)
            return [];
        var indexed = index.Columns.Select(column => column.Column.Identifier).ToHashSet(StringComparer.Ordinal);
        return route.ProjectedColumns
            .Where(column => column.Target == index.Target &&
                             column.Definition.IsNullable &&
                             indexed.Contains(column.Column.Identifier))
            .Select(column => column.Column.Identifier)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Whether <paramref name="index"/> declares a uniqueness constraint whose meaning is not portable.
    /// </summary>
    /// <remarks>
    /// A unique index that keeps rows with no value has to decide whether two such rows collide, and the
    /// providers genuinely disagree: SQL Server and MongoDB treat them as equal, so at most one may
    /// exist, while PostgreSQL and SQLite treat them as distinct and allow any number. PostgreSQL can be
    /// told otherwise with <c>NULLS NOT DISTINCT</c>; SQLite has no way to express it at all. Rather
    /// than let one manifest mean two different things, the shape is rejected — declare
    /// <see cref="MissingValueBehavior.Excluded"/> so uniqueness applies only to rows that have the
    /// value, make the column non-nullable, or drop the uniqueness.
    /// </remarks>
    public static bool HasNonPortableUniqueness(ExecutableStorageRoute route, ExecutablePhysicalIndexRoute index)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(index);
        if (!index.IsUnique || index.MissingValueBehavior == MissingValueBehavior.Excluded)
            return false;
        var indexed = index.Columns.Select(column => column.Column.Identifier).ToHashSet(StringComparer.Ordinal);
        return route.ProjectedColumns.Any(column =>
            column.Target == index.Target &&
            column.Definition.IsNullable &&
            indexed.Contains(column.Column.Identifier));
    }
}
