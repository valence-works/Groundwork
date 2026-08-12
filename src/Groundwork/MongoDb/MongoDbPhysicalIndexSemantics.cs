using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using MongoDB.Bson;

namespace Groundwork.MongoDb;

/// <summary>
/// How MongoDB realises row exclusion for one provider-neutral physical index, and how it recognises
/// what it emitted when it reads the index back.
/// </summary>
internal static class MongoDbPhysicalIndexSemantics
{
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
