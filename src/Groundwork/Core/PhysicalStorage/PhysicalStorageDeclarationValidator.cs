using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;
using Groundwork.Core.Validation;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Validates a storage unit's logical index, bounded query, and bounded mutation declarations
/// before any physical definition is resolved. Split from <see cref="PhysicalStorageResolver"/>;
/// diagnostics accumulate on the caller's list.
/// </summary>
internal static class PhysicalStorageDeclarationValidator
{
    internal static bool ValidateDeclarations(
        StorageUnit unit,
        List<GroundworkDiagnostic> diagnostics)
    {
        var storage = unit.PhysicalStorage!;
        var target = $"storageUnits.{unit.Identity.Value}.physicalStorage";
        var valid = true;
        var indexGroups = RequireUniqueIdentities(
            storage.LogicalIndexes,
            x => x.Identity,
            "GW-PHYSICAL-021",
            "Logical index identities must be non-empty and unique within a storage unit.",
            $"{target}.logicalIndexes",
            diagnostics,
            ref valid);

        foreach (var index in storage.LogicalIndexes)
        {
            if (index.Fields.Count == 0 ||
                index.Fields.Any(x => string.IsNullOrWhiteSpace(x.Path)) ||
                index.Fields.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != index.Fields.Count)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-021",
                    $"Logical index '{index.Identity}' requires one or more unique, non-empty stable serialized paths.",
                    $"{target}.logicalIndexes.{index.Identity}"));
                valid = false;
            }
        }

        var queryGroups = RequireUniqueIdentities(
            storage.BoundedQueries,
            x => x.Identity,
            "GW-PHYSICAL-021",
            "Bounded query identities must be non-empty and unique within a storage unit.",
            $"{target}.boundedQueries",
            diagnostics,
            ref valid);

        foreach (var query in storage.BoundedQueries)
        {
            if (!indexGroups.TryGetValue(query.IndexIdentity ?? string.Empty, out var matching) || matching.Length != 1)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-020",
                    $"Bounded query '{query.Identity}' must reference exactly one logical index '{query.IndexIdentity}'.",
                    $"{target}.boundedQueries.{query.Identity}.indexIdentity"));
                valid = false;
            }
            else if (!HasCompatiblePredicateAndSortPrefix(query, matching[0]))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-027",
                    $"Bounded query '{query.Identity}' predicate and sort fields must form a compatible logical-index prefix and require declared sort support.",
                    $"{target}.boundedQueries.{query.Identity}.sortFields"));
                valid = false;
            }

            if (query.Operations.Count == 0)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-021",
                    $"Bounded query '{query.Identity}' must declare at least one allowed operation.",
                    $"{target}.boundedQueries.{query.Identity}.operations"));
                valid = false;
            }

            if (query.PredicateFields.Select(field => field.Path).Distinct(StringComparer.Ordinal).Count() !=
                query.PredicateFields.Count)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-028",
                    $"Bounded query '{query.Identity}' predicate paths must be unique.",
                    $"{target}.boundedQueries.{query.Identity}.predicateFields"));
                valid = false;
            }

            if (query.ResidualPredicateFields.Select(field => field.Path).Distinct(StringComparer.Ordinal).Count() !=
                query.ResidualPredicateFields.Count)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-036",
                    $"Bounded query '{query.Identity}' residual predicate paths must be unique.",
                    $"{target}.boundedQueries.{query.Identity}.residualPredicateFields"));
                valid = false;
            }

            var resolvedPredicatePaths = matching is { Length: 1 }
                ? BoundedQueryPredicateResolver.Resolve(query, matching[0])
                    .Select(field => field.Path)
                : query.PredicateFields.Select(field => field.Path);
            var overlappingPredicates = resolvedPredicatePaths
                .Intersect(
                    query.ResidualPredicateFields.Select(field => field.Path),
                    StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (overlappingPredicates.Length != 0)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-036",
                    $"Bounded query '{query.Identity}' cannot declare an index-prefix predicate path as residual: {string.Join(", ", overlappingPredicates)}.",
                    $"{target}.boundedQueries.{query.Identity}.residualPredicateFields"));
                valid = false;
            }

            foreach (var residual in query.ResidualPredicateFields)
            {
                if (residual.Path == PhysicalDocumentFieldPaths.StorageScope)
                {
                    diagnostics.Add(GroundworkDiagnostic.Error(
                        "GW-PHYSICAL-036",
                        $"Bounded query '{query.Identity}' cannot declare session-owned storage scope as a residual predicate.",
                        $"{target}.boundedQueries.{query.Identity}.residualPredicateFields"));
                    valid = false;
                    continue;
                }

                if (residual.Operations.Count != 0 &&
                    residual.Operations.IsSubsetOf(query.Operations))
                {
                    continue;
                }

                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-036",
                    $"Residual predicate path '{residual.Path}' must declare operations included by bounded query '{query.Identity}'.",
                    $"{target}.boundedQueries.{query.Identity}.residualPredicateFields"));
                valid = false;
            }

            if (query.ResultOperations.Count == 0 ||
                query.SupportsTotalCount != query.ResultOperations.Contains(BoundedQueryResultOperation.Count))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-029",
                    $"Bounded query '{query.Identity}' requires at least one result operation and consistent total-count declaration.",
                    $"{target}.boundedQueries.{query.Identity}.resultOperations"));
                valid = false;
            }
        }

        foreach (var queryGroup in storage.BoundedQueries
                     .Where(x => x.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing)
                     .GroupBy(x => x.IndexIdentity, StringComparer.Ordinal))
        {
            if (!indexGroups.TryGetValue(queryGroup.Key, out var matching) || matching.Length != 1)
                continue;

            var tieBreakShapes = queryGroup
                .Select(query => ResolveProviderAppliedIdentityTieBreakShape(matching[0], query))
                .Where(shape => shape != "none")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (tieBreakShapes.Length > 1)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-027",
                    $"Scale-bearing queries sharing logical index '{queryGroup.Key}' require incompatible provider-applied identity tie-break shapes: {string.Join(", ", tieBreakShapes)}.",
                    $"{target}.boundedQueries"));
                valid = false;
            }

            var directionShapes = queryGroup
                .Select(query => PhysicalDefinitionValidator.CanonicalizeSortDirections(
                    PhysicalDefinitionValidator.ResolveSortDirections(query, matching[0])))
                .Select(PhysicalDefinitionValidator.DirectionShape)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (directionShapes.Length <= 1)
                continue;

            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-028",
                $"Scale-bearing queries for logical index '{queryGroup.Key}' require incompatible compound sort directions.",
                $"{target}.boundedQueries"));
            valid = false;
        }

        RequireUniqueIdentities(
            storage.BoundedMutations,
            mutation => mutation.Identity,
            "GW-PHYSICAL-031",
            "Bounded mutation identities must be non-empty and unique within a storage unit.",
            $"{target}.boundedMutations",
            diagnostics,
            ref valid);

        foreach (var mutation in storage.BoundedMutations)
        {
            if (!queryGroups.TryGetValue(mutation.PredicateQueryIdentity, out var queries) || queries.Length != 1)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-032",
                    $"Bounded mutation '{mutation.Identity}' must reference exactly one bounded predicate query '{mutation.PredicateQueryIdentity}'.",
                    $"{target}.boundedMutations.{mutation.Identity}.predicateQueryIdentity"));
                valid = false;
                continue;
            }

            var query = queries[0];
            if (query.ExecutionClass != BoundedQueryExecutionClass.ScaleBearing)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-033",
                    $"Bounded mutation '{mutation.Identity}' requires a scale-bearing predicate query.",
                    $"{target}.boundedMutations.{mutation.Identity}.predicateQueryIdentity"));
                valid = false;
            }

            if (mutation.Action is not BoundedTransitionMutationAction transition)
                continue;
            var effectivePredicates =
                indexGroups.TryGetValue(query.IndexIdentity, out var mutationIndexes) &&
                mutationIndexes.Length == 1
                    ? BoundedQueryPredicateResolver.Resolve(query, mutationIndexes[0])
                    : query.PredicateFields;
            var transitionPredicate = effectivePredicates.SingleOrDefault(field => field.Path == transition.Path);
            if (transitionPredicate is null ||
                !transitionPredicate.Operations.Contains(PortableQueryOperation.Equal) &&
                !transitionPredicate.Operations.Contains(PortableQueryOperation.In))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-034",
                    $"Bounded transition '{mutation.Identity}' requires exact matching on declared predicate path '{transition.Path}'.",
                    $"{target}.boundedMutations.{mutation.Identity}.action"));
                valid = false;
            }
        }

        return valid;
    }

    /// <summary>
    /// Groups declarations by identity and emits the caller's diagnostic when any identity is
    /// empty or duplicated. Each declaration family keeps its own code and message.
    /// </summary>
    private static Dictionary<string, T[]> RequireUniqueIdentities<T>(
        IEnumerable<T> declarations,
        Func<T, string?> identity,
        string code,
        string message,
        string target,
        List<GroundworkDiagnostic> diagnostics,
        ref bool valid)
    {
        var groups = declarations
            .GroupBy(declaration => identity(declaration) ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (groups.Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Value.Length != 1))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(code, message, target));
            valid = false;
        }

        return groups;
    }

    private static bool HasCompatiblePredicateAndSortPrefix(
        BoundedQueryDeclaration query,
        LogicalIndexDeclaration index)
    {
        var indexPaths = index.Fields.Select(field => field.Path).ToList();
        if (PhysicalQueryOrderRequirements.RequiresProviderAppliedIdentityTieBreak(index, query))
            indexPaths.Add(PhysicalDocumentFieldPaths.Id);
        var predicates = BoundedQueryPredicateResolver.Resolve(query, index).ToArray();
        var predicatePaths = predicates.Select(field => field.Path).ToArray();
        if (!indexPaths.Take(predicatePaths.Length).SequenceEqual(predicatePaths))
            return false;

        if (predicates.Any(field =>
                field.Operations.Count == 0 ||
                !field.Operations.IsSubsetOf(query.Operations)))
        {
            return false;
        }

        if (query.LatestPerKeyPath is not null &&
            !indexPaths.Contains(query.LatestPerKeyPath, StringComparer.Ordinal))
        {
            return false;
        }

        if (query.PredicateBindingMode == BoundedQueryPredicateBindingMode.DeclaredFields &&
            predicates.Length == 0 &&
            query.SortFields.Count == 0)
        {
            return false;
        }
        if (PhysicalQueryOrderRequirements.IsUniquePointLookup(index, predicates))
            return true;

        var sortPaths = query.SortFields.Count != 0
            ? query.SortFields.Select(field => field.Path).ToList()
            : query.SortSupport == QuerySortSupport.None
                ? []
                : index.Fields.Select(field => field.Path).ToList();
        if (PhysicalQueryOrderRequirements.RequiresProviderAppliedIdentityTieBreak(index, query) &&
            !sortPaths.Contains(PhysicalDocumentFieldPaths.Id, StringComparer.Ordinal))
        {
            sortPaths.Add(PhysicalDocumentFieldPaths.Id);
        }
        if (sortPaths.Count == 0)
            return true;
        return CompoundIndexOrdering.TryResolveSortStart(
                   indexPaths,
                   predicatePaths,
                   predicates,
                   sortPaths,
                   out _,
                   out var requiredEqualityPredicateCount) &&
               CompoundIndexOrdering.AreSingleValueEqualities(predicates, requiredEqualityPredicateCount);
    }

    private static string ResolveProviderAppliedIdentityTieBreakShape(
        LogicalIndexDeclaration logicalIndex,
        BoundedQueryDeclaration query) =>
        PhysicalQueryOrderRequirements.RequiresProviderAppliedIdentityTieBreak(logicalIndex, query)
            ? query.PagingSupport == QueryPagingSupport.Cursor
                ? PhysicalDocumentIdentityFieldPaths.Lookup
                : PhysicalDocumentIdentityFieldPaths.Comparison
            : "none";
}
