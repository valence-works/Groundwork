using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using MongoDB.Bson;

namespace Groundwork.MongoDb;

/// <summary>Canonical MongoDB membership metadata for one provider-neutral physical index.</summary>
internal static class MongoDbPhysicalIndexSemantics
{
    public static IReadOnlyList<string> ValueFields(
        ExecutableStorageRoute route,
        ExecutablePhysicalIndexRoute index)
    {
        var projected = route.ProjectedColumns
            .Where(projection => projection.Target == index.Target)
            .Select(projection => projection.Column.Identifier)
            .ToHashSet(StringComparer.Ordinal);
        return index.Columns
            .OrderBy(column => column.Order)
            .Select(column => column.Column.Identifier)
            .Where(projected.Contains)
            .ToArray();
    }

    /// <summary>
    /// The partial filter that realises <see cref="MissingValueBehavior.Excluded"/>, or
    /// <see langword="null"/> when the index keeps every document. <paramref name="missingValueBehavior"/>
    /// overrides the declared behavior, which yields the filter the index carried under a definition it no
    /// longer has.
    /// </summary>
    /// <remarks>
    /// Restricted to the same columns the relational providers filter on — the nullable ones. A
    /// non-nullable projection is always present, so requiring it to exist excludes nothing while making
    /// the index unusable for more queries than it needs to be.
    /// </remarks>
    public static BsonDocument? PartialFilter(
        ExecutableStorageRoute route,
        ExecutablePhysicalIndexRoute index,
        MissingValueBehavior? missingValueBehavior = null)
    {
        var fields = PhysicalIndexNullExclusion.Columns(route, index, missingValueBehavior);
        if (fields.Length == 0)
            return null;
        var filter = new BsonDocument();
        foreach (var field in fields)
            filter[field] = new BsonDocument("$exists", true);
        return filter;
    }

    public static bool PartialFilterMatches(BsonDocument actualIndex, BsonDocument? expected)
    {
        if (expected is null)
            return !actualIndex.Contains("partialFilterExpression");
        return actualIndex.TryGetValue("partialFilterExpression", out var actual) &&
               actual.IsBsonDocument &&
               actual.AsBsonDocument.Equals(expected);
    }
}
