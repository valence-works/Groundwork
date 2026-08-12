using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Documents.Store;

/// <summary>
/// What a null-excluding index may do for one runtime predicate: whether it may be pinned at all, and
/// the excluded columns the predicate has to restate so the provider can match the index's own filter.
/// </summary>
/// <remarks>
/// <see cref="MembershipColumns"/> is empty whenever <see cref="ServesQuery"/> is <see langword="false"/>,
/// and also when the index excludes nothing — the two cases a caller treats identically, since neither
/// contributes a conjunct.
/// </remarks>
internal sealed record PhysicalIndexNullExclusionVerdict(
    bool ServesQuery,
    IReadOnlyList<string> MembershipColumns)
{
    public static PhysicalIndexNullExclusionVerdict Serves { get; } = new(true, []);
    public static PhysicalIndexNullExclusionVerdict GivesUpIndex { get; } = new(false, []);
}

/// <summary>
/// The single decision, shared by every provider that pins an index, of whether an index declared
/// <see cref="Core.Indexing.MissingValueBehavior.Excluded"/> may serve the predicate a query just
/// produced.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PhysicalIndexNullExclusion"/> says which columns an index excludes rows for; this says
/// what a query may do about it. The invariant both exist to hold is that row exclusion is a storage
/// choice and never a row-visibility one: no provider may return, update, or delete fewer rows than the
/// predicate matches because an index happens to omit some of them.
/// </para>
/// <para>
/// So there are three outcomes, not two. Where the predicate provably rejects nulls on every excluded
/// column the restriction is a no-op and the index is pinned, with the excluded columns handed back so
/// the caller can restate them — redundant by construction, and needed only because a provider's
/// optimizer reasons over simple comparison forms and cannot see through, say,
/// <c>LOWER(column) LIKE @p</c>. Where it does not, pinning would drop rows the predicate can match, so
/// the index is given up and the optimizer serves the full row set — unless the plan is scale-bearing,
/// whose whole point is that an unindexed scan is not an acceptable fallback, in which case the query is
/// refused by name rather than silently under-served.
/// </para>
/// <para>
/// A provider that never pins an index passes no excluded columns and therefore never refuses: it has
/// no filtered index to imply, so it was never at risk of under-serving. That is why PostgreSQL answers
/// queries SQL Server, SQLite, and MongoDB refuse — the contract is the row set, not the refusal.
/// </para>
/// </remarks>
internal static class PhysicalIndexNullExclusionGuard
{
    /// <summary>
    /// Whether the index named <paramref name="indexIdentifier"/>, which excludes rows for
    /// <paramref name="excludedColumns"/>, may serve <paramref name="query"/> under
    /// <paramref name="plan"/>. Pass no excluded columns when the provider does not pin the index.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The plan is scale-bearing and its predicate can match rows the index omits.
    /// </exception>
    public static PhysicalIndexNullExclusionVerdict Decide(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        string indexIdentifier,
        IReadOnlyList<string> excludedColumns)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(excludedColumns);
        if (excludedColumns.Count == 0)
            return PhysicalIndexNullExclusionVerdict.Serves;

        var (nullRejectedColumns, matchesNoRows) = Analyze(query, plan, route);
        // A predicate that returns no rows cannot drop any, so every index serves it equally.
        if (matchesNoRows)
            return PhysicalIndexNullExclusionVerdict.Serves;
        var unproven = excludedColumns.Where(column => !nullRejectedColumns.Contains(column)).ToArray();
        if (unproven.Length == 0)
            return new PhysicalIndexNullExclusionVerdict(true, excludedColumns);
        if (plan.IsScaleBearing)
        {
            throw new InvalidOperationException(
                $"Document query '{query.QueryIdentity}' is scale-bearing on physical index " +
                $"'{indexIdentifier}', which declares MissingValueBehavior.Excluded and so omits " +
                $"rows whose {string.Join(" or ", unproven.Select(column => $"'{column}'"))} is null. Its " +
                "predicate can match those rows, so the index cannot serve the query without dropping " +
                "them. Declare the index MissingValueBehavior.IncludedAsNull so it keeps every row — an " +
                "already-applied index is rebuilt under it, which is additive. A unique index that keys a " +
                "nullable column cannot take that route, because null-distinct uniqueness is not portable " +
                "(GW-ROUTE-007); bind the query to an index that keys no nullable column, make the column " +
                "non-nullable, or declare a new index identity for the shape the query needs.");
        }
        return PhysicalIndexNullExclusionVerdict.GivesUpIndex;
    }

    /// <summary>
    /// The projected columns <paramref name="query"/> can only match non-null values of, and whether it
    /// matches no rows at all.
    /// </summary>
    /// <remarks>
    /// Clauses compose with AND, so proving null-rejection in any one of them proves it for the whole
    /// predicate. A clause is itself a disjunction, so it only rejects nulls on a column when every
    /// alternative does — one alternative that can match a null row is enough to make the whole clause
    /// able to. An alternative that matches nothing is skipped rather than intersected: it contributes no
    /// rows, so it neither widens what the clause returns nor constrains it, and folding it in would
    /// wrongly read it as "proves nothing".
    /// </remarks>
    private static (IReadOnlySet<string> NullRejectedColumns, bool MatchesNoRows) Analyze(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route)
    {
        var nullRejectedColumns = new HashSet<string>(StringComparer.Ordinal);
        var matchesNoRows = false;
        foreach (var clause in query.Clauses)
        {
            if (clause.Comparisons.Count == 0)
            {
                matchesNoRows = true;
                continue;
            }
            HashSet<string>? clauseRejected = null;
            foreach (var comparison in clause.Comparisons)
            {
                if (MatchesNoRows(comparison))
                    continue;
                var alternativeRejected = new HashSet<string>(StringComparer.Ordinal);
                if (RejectsNulls(comparison) && ProjectedColumn(plan, route, comparison) is { } column)
                    alternativeRejected.Add(column);
                if (clauseRejected is null)
                    clauseRejected = alternativeRejected;
                else
                    clauseRejected.IntersectWith(alternativeRejected);
            }
            if (clauseRejected is null)
                matchesNoRows = true;
            else
                nullRejectedColumns.UnionWith(clauseRejected);
        }
        return (nullRejectedColumns, matchesNoRows);
    }

    /// <summary>
    /// Whether the comparison, as every provider renders it, can only match rows whose field is non-null.
    /// </summary>
    /// <remarks>
    /// Note the asymmetry between the two negations: <c>NotEqual</c> qualifies, because it is defined to
    /// mean the field has a value and that value differs, while <c>NotContains</c> does not, because it
    /// is defined to match a null or absent field. <c>Equal</c> depends on the value — a null one asks
    /// for precisely the rows an excluding index omits.
    /// </remarks>
    private static bool RejectsNulls(DocumentQueryComparison comparison) => comparison.Operator switch
    {
        QueryComparisonOperator.NotEqual => true,
        QueryComparisonOperator.NotContains => false,
        QueryComparisonOperator.In =>
            comparison.Values.Count > 0 && comparison.Values.All(value => value is not null),
        QueryComparisonOperator.Equal or
            QueryComparisonOperator.GreaterThan or QueryComparisonOperator.GreaterThanOrEqual or
            QueryComparisonOperator.LessThan or QueryComparisonOperator.LessThanOrEqual or
            QueryComparisonOperator.Contains or QueryComparisonOperator.StartsWith =>
            comparison.Values[0] is not null,
        _ => false
    };

    /// <summary>An empty membership set is the only comparison shape that can match nothing.</summary>
    private static bool MatchesNoRows(DocumentQueryComparison comparison) =>
        comparison.Operator == QueryComparisonOperator.In && comparison.Values.Count == 0;

    /// <summary>
    /// The projected column the comparison restricts, or <see langword="null"/> when it restricts
    /// something else — document identity, an envelope field, a collection element, or a canonical JSON
    /// path. Only a projected column can be one an index excludes rows for.
    /// </summary>
    /// <remarks>
    /// Matched on the field the plan actually addresses rather than on
    /// <see cref="PhysicalQueryFieldSource"/>, because a provider can reach a projected column through a
    /// source that is not named after it: MongoDB has no primary-projected-column source and resolves the
    /// same column through its native-document-field one.
    /// </remarks>
    private static string? ProjectedColumn(
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        DocumentQueryComparison comparison)
    {
        if (comparison.Path == PhysicalDocumentFieldPaths.Id)
            return null;
        var field = plan.Predicates.Single(predicate => predicate.Path == comparison.Path).Field;
        var column = route.ProjectedColumns.SingleOrDefault(candidate =>
            candidate.Target == field.Target &&
            candidate.Definition.Path == comparison.Path);
        return column is not null &&
               string.Equals(column.Column.Identifier, field.Identifier, StringComparison.Ordinal)
            ? column.Column.Identifier
            : null;
    }
}
