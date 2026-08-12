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
    /// match. <paramref name="missingValueBehavior"/> overrides the declared behavior, which lets a caller
    /// ask what an index excluded under a definition it no longer carries.
    /// </summary>
    public static string[] Columns(
        ExecutableStorageRoute route,
        ExecutablePhysicalIndexRoute index,
        MissingValueBehavior? missingValueBehavior = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(index);
        if ((missingValueBehavior ?? index.MissingValueBehavior) != MissingValueBehavior.Excluded)
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
    /// <para>
    /// A unique index that keeps rows with no value has to decide whether two such rows collide, and the
    /// providers genuinely disagree: SQL Server and MongoDB treat them as equal, so at most one may
    /// exist, while PostgreSQL and SQLite treat them as distinct and allow any number. PostgreSQL can be
    /// told otherwise with <c>NULLS NOT DISTINCT</c>; SQLite has no way to express it at all. Rather
    /// than let one manifest mean two different things, the shape is rejected — declare
    /// <see cref="MissingValueBehavior.Excluded"/> so uniqueness applies only to rows that have the
    /// value, make the column non-nullable, or drop the uniqueness.
    /// </para>
    /// <para>
    /// Unless the uniqueness is already implied. When some other unique index's key columns are a subset
    /// of this index's, no two rows can share this whole key whatever the nullable columns hold, so the
    /// disagreement is unobservable and the declaration is portable after all. That is not a corner
    /// case: a compound index that appends a searchable column to an already-unique identity is the
    /// ordinary way to get a total order without paying for a wide identity tie-break column.
    /// </para>
    /// </remarks>
    public static bool HasNonPortableUniqueness(ExecutableStorageRoute route, ExecutablePhysicalIndexRoute index)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(index);
        if (!index.IsUnique || index.MissingValueBehavior == MissingValueBehavior.Excluded)
            return false;
        var indexed = index.Columns.Select(column => column.Column.Identifier).ToHashSet(StringComparer.Ordinal);
        var keysNullableColumn = route.ProjectedColumns.Any(column =>
            column.Target == index.Target &&
            column.Definition.IsNullable &&
            indexed.Contains(column.Column.Identifier));
        return keysNullableColumn && !HasImpliedUniqueness(route, index, indexed);
    }

    private static bool HasImpliedUniqueness(
        ExecutableStorageRoute route,
        ExecutablePhysicalIndexRoute index,
        IReadOnlySet<string> indexed) =>
        route.Indexes.Any(other =>
            other.IsUnique &&
            other.Identity != index.Identity &&
            other.Target == index.Target &&
            other.Columns.Count < index.Columns.Count &&
            other.Columns.All(column => indexed.Contains(column.Column.Identifier)) &&
            // An implying index must itself constrain every row, or it only proves uniqueness among the
            // rows it kept.
            Columns(route, other).Length == 0);
}
