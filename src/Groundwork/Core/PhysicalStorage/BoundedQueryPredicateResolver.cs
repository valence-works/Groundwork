namespace Groundwork.Core.PhysicalStorage;

internal static class BoundedQueryPredicateResolver
{
    public static IReadOnlyList<BoundedQueryPredicateField> Resolve(
        BoundedQueryDeclaration query,
        LogicalIndexDeclaration logicalIndex) =>
        query.PredicateBindingMode == BoundedQueryPredicateBindingMode.ImplicitFirstLogicalIndexField
            ? logicalIndex.Fields.Take(1)
                .Select(field => new BoundedQueryPredicateField(field.Path, query.Operations))
                .ToArray()
            : query.PredicateFields;
}
