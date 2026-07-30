using Groundwork.Core.Indexing;
using Groundwork.Core.Queries;

namespace Groundwork.Core.PhysicalStorage;

internal static class PhysicalQueryOrderRequirements
{
    public static bool IsUniquePointLookup(
        LogicalIndexDeclaration logicalIndex,
        IReadOnlyList<BoundedQueryPredicateField> predicates) =>
        logicalIndex.IsUnique &&
        logicalIndex.Fields.Select(field => field.Path)
            .SequenceEqual(predicates.Select(predicate => predicate.Path)) &&
        CompoundIndexOrdering.AreSingleValueEqualities(predicates, predicates.Count);

    public static bool RequiresProviderAppliedIdentityTieBreak(
        LogicalIndexDeclaration logicalIndex,
        BoundedQueryDeclaration query) =>
        query.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing &&
        query.PagingSupport is QueryPagingSupport.Cursor or QueryPagingSupport.Offset &&
        !query.Operations.All(operation => operation is
            PortableQueryOperation.CollectionContains or
            PortableQueryOperation.CollectionContainsAll) &&
        !logicalIndex.IsUnique &&
        logicalIndex.Fields.All(field => field.Path != PhysicalDocumentFieldPaths.Id);
}
