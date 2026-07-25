using Groundwork.Core.Validation;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using System.Security.Cryptography;
using System.Text;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Immutable provider-neutral execution evidence for one named bounded mutation. Predicate
/// selection is delegated to the same closed physical plan used by bounded document queries.
/// </summary>
public abstract record PhysicalMutationAction(BoundedMutationActionKind Kind);

public sealed record PhysicalDeleteMutationAction() :
    PhysicalMutationAction(BoundedMutationActionKind.Delete);

public sealed record PhysicalTransitionMutationAction(
    string Path,
    IReadOnlyList<string> AllowedSourceValues,
    string TargetValue,
    PhysicalQueryField Field) :
    PhysicalMutationAction(BoundedMutationActionKind.Transition);

/// <summary>
/// Immutable cross-route evidence compiled from a manifest relationship guard. Providers consume
/// these closed bindings directly; they never infer a join or accept a caller-supplied path.
/// </summary>
public abstract record PhysicalMutationRelationshipGuard(BoundedMutationRelationshipGuardKind Kind)
{
    public abstract string CanonicalIdentity { get; }
}

/// <summary>
/// Executable cross-unit equality evidence for one manifest-owned relationship. The relationship
/// identity is stable across compatible manifest upgrades; route fingerprints remain separate
/// admission evidence and must never become a durable fence key.
/// </summary>
public sealed record PhysicalRequireNoReferencesMutationGuard(
    PhysicalRelationshipPlan Relationship) :
    PhysicalMutationRelationshipGuard(BoundedMutationRelationshipGuardKind.RequireNoReferences)
{
    public override string CanonicalIdentity => Relationship.CanonicalIdentity;
}

public sealed record PhysicalRequireRelatedTargetNotEqualMutationGuard(
    PhysicalRelationshipPlan Relationship,
    PhysicalQueryField TargetPredicateField,
    ExecutablePhysicalIndexRoute TargetPredicateIndex,
    string DisallowedTargetValue) :
    PhysicalMutationRelationshipGuard(BoundedMutationRelationshipGuardKind.RequireRelatedTargetNotEqual)
{
    public override string CanonicalIdentity => PhysicalCanonicalEncoding.Join(
        Relationship.CanonicalIdentity,
        TargetPredicateField.Path,
        TargetPredicateField.Identifier,
        TargetPredicateField.ObjectName.Identifier,
        ((int)TargetPredicateField.ValueKind).ToString(System.Globalization.CultureInfo.InvariantCulture),
        TargetPredicateIndex.Identity,
        TargetPredicateIndex.Name.Identifier,
        DisallowedTargetValue);
}

public sealed record PhysicalMutationPlan(
    string MutationIdentity,
    PhysicalMutationAction Action,
    PhysicalQueryPlan Predicate)
{
    public IReadOnlyList<PhysicalMutationRelationshipGuard> RelationshipGuards { get; init; } = [];

    public string HandlerIdentity => Predicate.HandlerIdentity;

    public string RouteFingerprint => Predicate.RouteFingerprint;

    public string Fingerprint => PhysicalMutationPlanFingerprint.Compute(this);
}

public static class PhysicalMutationPlanFingerprint
{
    public static string Compute(PhysicalMutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var action = plan.Action switch
        {
            PhysicalDeleteMutationAction => PhysicalCanonicalEncoding.Join("delete"),
            PhysicalTransitionMutationAction transition => PhysicalCanonicalEncoding.Join(
            [
                "transition",
                transition.Path,
                transition.TargetValue,
                .. transition.AllowedSourceValues.Order(StringComparer.Ordinal)
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(plan), plan.Action, null)
        };
        var predicates = plan.Predicate.Predicates
            .OrderBy(predicate => predicate.Path, StringComparer.Ordinal)
            .Select(predicate => PhysicalCanonicalEncoding.Join(
                predicate.Path,
                Field(predicate.Field),
                predicate.IsResidual ? "1" : "0",
                predicate.IsRequired ? "1" : "0",
                predicate.CollectionConstraint is null
                    ? null
                    : PhysicalCanonicalEncoding.Join(
                        ((int)predicate.CollectionConstraint.PhysicalType).ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        predicate.CollectionConstraint.MaximumValues.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)),
                PhysicalCanonicalEncoding.Join(predicate.Operations.Order().Select(operation =>
                    ((int)operation).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray())))
            .ToArray();
        var order = plan.Predicate.Order
            .Select(item => PhysicalCanonicalEncoding.Join(
                item.Path,
                Field(item.Field),
                ((int)item.Direction).ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.IsIdentityTieBreak ? "1" : "0"))
            .ToArray();
        var guards = plan.RelationshipGuards
            .OrderBy(guard => guard.Kind)
            .ThenBy(guard => guard.CanonicalIdentity, StringComparer.Ordinal)
            .Select(guard => PhysicalCanonicalEncoding.Join(
                ((int)guard.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
                guard.CanonicalIdentity))
            .ToArray();
        var canonical = PhysicalCanonicalEncoding.Join(
            plan.MutationIdentity,
            action,
            plan.Predicate.Provider.Name,
            plan.Predicate.StorageUnit.Value,
            plan.Predicate.RouteFingerprint,
            plan.Predicate.QueryIdentity,
            plan.Predicate.LogicalIndexIdentity,
            PhysicalCanonicalEncoding.Join(plan.Predicate.LogicalIndexPaths.ToArray()),
            plan.Predicate.HandlerIdentity,
            plan.Predicate.LookupObject.Identifier,
            plan.Predicate.PrimaryObject.Identifier,
            plan.Predicate.IndexName?.Identifier,
            ((int)plan.Predicate.Form).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)plan.Predicate.AccessKind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Field(plan.Predicate.Scope.Field),
            ((int)plan.Predicate.Scope.Policy).ToString(System.Globalization.CultureInfo.InvariantCulture),
            plan.Predicate.Scope.IsMandatory ? "1" : "0",
            plan.Predicate.Scope.UsesGlobalSentinel ? "1" : "0",
            Field(plan.Predicate.Discriminator),
            ((int)plan.Predicate.DocumentIdentity.StringCasePolicy).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            plan.Predicate.DocumentIdentity.ComparisonAlgorithmId,
            plan.Predicate.DocumentIdentity.LookupAlgorithmId,
            Field(plan.Predicate.DocumentIdentity.Original),
            Field(plan.Predicate.DocumentIdentity.Comparison),
            Field(plan.Predicate.DocumentIdentity.Lookup),
            PhysicalCanonicalEncoding.Join(predicates),
            PhysicalCanonicalEncoding.Join(order),
            PhysicalCanonicalEncoding.Join(plan.Predicate.RequiredEqualityPrefixPaths.ToArray()),
            ((int)plan.Predicate.PagingSupport).ToString(System.Globalization.CultureInfo.InvariantCulture),
            PhysicalCanonicalEncoding.Join(plan.Predicate.ResultOperations
                .Order()
                .Select(operation => ((int)operation).ToString(
                    System.Globalization.CultureInfo.InvariantCulture))
                .ToArray()),
            plan.Predicate.SupportsDisjunction ? "1" : "0",
            plan.Predicate.LatestPerKeyPath,
            plan.Predicate.RequiresPrimaryLookup ? "1" : "0",
            plan.Predicate.IsScaleBearing ? "1" : "0",
            PhysicalCanonicalEncoding.Join(guards));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Field(PhysicalQueryField field) =>
        PhysicalCanonicalEncoding.Join(
            field.Path,
            field.Identifier,
            ((int)field.Source).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)field.Target).ToString(System.Globalization.CultureInfo.InvariantCulture),
            field.ObjectName.Identifier,
            ((int)field.ValueKind).ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public sealed class PhysicalMutationPlanCompilationResult
{
    public PhysicalMutationPlanCompilationResult(
        IReadOnlyList<PhysicalMutationPlan> plans,
        IReadOnlyList<GroundworkDiagnostic> diagnostics)
    {
        Plans = Array.AsReadOnly((plans ?? throw new ArgumentNullException(nameof(plans))).ToArray());
        Diagnostics = Array.AsReadOnly((diagnostics ?? throw new ArgumentNullException(nameof(diagnostics))).ToArray());
    }

    public IReadOnlyList<PhysicalMutationPlan> Plans { get; }

    public IReadOnlyList<GroundworkDiagnostic> Diagnostics { get; }

    public bool IsValid => Diagnostics.All(diagnostic => !diagnostic.IsError);
}

/// <summary>
/// Compiles declared mutation names and effects against closed physical query plans. It never
/// produces an unbounded or client-evaluated selector.
/// </summary>
public static class PhysicalMutationPlanCompiler
{
    public static PhysicalMutationPlanCompilationResult Compile(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage,
        PhysicalQueryPlannerCapabilities predicateCapabilities,
        bool supportsAtomicCollectionMaintenance = false)
        => CompileCore(
            route,
            storage,
            predicateCapabilities,
            supportsAtomicCollectionMaintenance,
            manifest: null,
            relationshipPlans: null);

    /// <summary>
    /// Compiles one mutation against the complete admitted manifest route set. Relationship guards
    /// require this overload so their referenced route and index evidence is bound before a provider
    /// can advertise or execute the mutation.
    /// </summary>
    public static PhysicalMutationPlanCompilationResult Compile(
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage,
        PhysicalQueryPlannerCapabilities predicateCapabilities,
        bool supportsAtomicCollectionMaintenance = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(routes);
        var manifestUnit = manifest.StorageUnits.SingleOrDefault(unit => unit.Identity == route.StorageUnit);
        var admittedRoutes = routes
            .Where(candidate => candidate.StorageUnit == route.StorageUnit)
            .ToArray();
        var admittedRoute = admittedRoutes.Length == 1 ? admittedRoutes[0] : null;
        if (manifestUnit?.PhysicalStorage is null ||
            admittedRoute is null ||
            !admittedRoute.Equals(route))
        {
            return new(
                [],
                [GroundworkDiagnostic.Error(
                    "GW-MUTATION-019",
                    "Full-manifest mutation admission requires the exact local route from the supplied manifest route set.",
                    $"physicalMutations.{route.StorageUnit.Value}")]);
        }
        if (!manifestUnit.PhysicalStorage.Equals(storage))
        {
            return new(
                [],
                [GroundworkDiagnostic.Error(
                    "GW-MUTATION-020",
                    "Full-manifest mutation admission requires the storage declaration owned by the local manifest unit.",
                    $"physicalMutations.{route.StorageUnit.Value}")]);
        }

        var relationshipCompilation = PhysicalRelationshipPlanCompiler.Compile(manifest, routes);
        if (!relationshipCompilation.IsValid)
            return new([], relationshipCompilation.Diagnostics);
        return CompileCore(
            route,
            storage,
            predicateCapabilities,
            supportsAtomicCollectionMaintenance,
            manifest,
            relationshipCompilation.Plans.ToDictionary(plan => plan.Identity, StringComparer.Ordinal));
    }

    private static PhysicalMutationPlanCompilationResult CompileCore(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage,
        PhysicalQueryPlannerCapabilities predicateCapabilities,
        bool supportsAtomicCollectionMaintenance,
        StorageManifest? manifest,
        IReadOnlyDictionary<string, PhysicalRelationshipPlan>? relationshipPlans)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(predicateCapabilities);

        if (storage.BoundedMutations.Count == 0)
            return new([], []);

        if (route.CollectionElementStorages.Count != 0 && !supportsAtomicCollectionMaintenance)
        {
            return new(
                [],
                storage.BoundedMutations.Select(mutation => GroundworkDiagnostic.Error(
                    "GW-MUTATION-007",
                    $"Bounded mutation '{mutation.Identity}' targets a route with collection elements, " +
                    "but the provider has not certified atomic owner-and-element maintenance.",
                    $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}"))
                    .ToArray());
        }

        var predicateQueryIdentities = storage.BoundedMutations
            .Select(mutation => mutation.PredicateQueryIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var predicateStorage = new StorageUnitPhysicalStorage(
            storage.ProvisioningMode,
            storage.Policy,
            storage.LogicalIndexes,
            storage.BoundedQueries
                .Where(query => predicateQueryIdentities.Contains(query.Identity))
                .ToArray(),
            storage.NameOverrides,
            storage.BoundedMutations);
        var queryCompilation = PhysicalQueryPlanCompiler.Compile(route, predicateStorage, predicateCapabilities);
        var diagnostics = queryCompilation.Diagnostics.ToList();
        if (!queryCompilation.IsValid)
            return new([], diagnostics);

        var duplicateIdentities = storage.BoundedMutations
            .GroupBy(mutation => mutation.Identity, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (duplicateIdentities.Length != 0)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-001",
                $"Bounded mutation identities must be unique: {string.Join(", ", duplicateIdentities)}.",
                $"physicalMutations.{route.StorageUnit.Value}"));
            return new([], diagnostics);
        }

        var plans = new List<PhysicalMutationPlan>();
        foreach (var mutation in storage.BoundedMutations)
        {
            var predicate = queryCompilation.Plans.SingleOrDefault(plan =>
                plan.QueryIdentity == mutation.PredicateQueryIdentity);
            if (predicate is null)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-MUTATION-002",
                    $"Bounded mutation '{mutation.Identity}' must reference one compiled bounded predicate query '{mutation.PredicateQueryIdentity}'.",
                    $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}"));
                continue;
            }
            if (predicate.Predicates.Count == 0)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-MUTATION-008",
                    $"Bounded mutation '{mutation.Identity}' cannot use unfiltered predicate query '{predicate.QueryIdentity}'.",
                    $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}"));
                continue;
            }
            if (!predicate.IsScaleBearing || predicate.IndexName is null)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-MUTATION-004",
                    $"Bounded mutation '{mutation.Identity}' requires a scale-bearing indexed server-side predicate.",
                    $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}"));
                continue;
            }
            if (predicate.Predicates.Any(field => field.IsResidual))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-MUTATION-006",
                    $"Bounded mutation '{mutation.Identity}' cannot use predicate query '{predicate.QueryIdentity}' because residual predicates are query-only.",
                    $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}"));
                continue;
            }
            if (predicate.AccessKind == PhysicalQueryAccessKind.CollectionElementsThenPrimary)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-MUTATION-007",
                    $"Bounded mutation '{mutation.Identity}' selects documents through collection membership. " +
                    "Collection-selected transitions and deletes require atomic owner-and-element maintenance.",
                    $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}"));
                continue;
            }

            var action = CompileAction(mutation, predicate, route, diagnostics);
            var guards = action is null
                ? null
                : CompileRelationshipGuards(mutation, route, manifest, relationshipPlans, diagnostics);
            if (action is not null && guards is not null)
            {
                plans.Add(new PhysicalMutationPlan(mutation.Identity, action, predicate)
                {
                    RelationshipGuards = guards
                });
            }
        }

        return diagnostics.Any(diagnostic => diagnostic.IsError)
            ? new([], diagnostics)
            : new(plans, diagnostics);
    }

    private static IReadOnlyList<PhysicalMutationRelationshipGuard>? CompileRelationshipGuards(
        BoundedMutationDeclaration mutation,
        ExecutableStorageRoute route,
        StorageManifest? manifest,
        IReadOnlyDictionary<string, PhysicalRelationshipPlan>? relationshipPlans,
        List<GroundworkDiagnostic> diagnostics)
    {
        if (mutation.RelationshipGuards.Count == 0)
            return [];

        var location = $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}.relationshipGuards";
        if (manifest is null || relationshipPlans is null)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-009",
                $"Bounded mutation '{mutation.Identity}' declares relationship guards and therefore requires complete manifest route admission.",
                location));
            return null;
        }
        if (mutation.Action is not BoundedDeleteMutationAction)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-010",
                $"Bounded mutation '{mutation.Identity}' uses relationship guards, which are currently admitted only for guarded deletes.",
                location));
            return null;
        }

        var guards = new List<PhysicalMutationRelationshipGuard>();
        foreach (var guard in mutation.RelationshipGuards)
        {
            switch (guard)
            {
                case RequireNoReferencesMutationRelationshipGuard noReferences:
                    CompileNoReferencesGuard(noReferences, route, relationshipPlans, guards, diagnostics, location);
                    break;

                case RequireRelatedTargetNotEqualMutationRelationshipGuard relatedTarget:
                    CompileRelatedTargetNotEqualGuard(
                        relatedTarget,
                        route,
                        manifest,
                        relationshipPlans,
                        guards,
                        diagnostics,
                        location);
                    break;

                default:
                    diagnostics.Add(GroundworkDiagnostic.Error(
                        "GW-MUTATION-009",
                        $"Bounded mutation '{mutation.Identity}' declares unsupported relationship guard '{guard.Kind}'.",
                        location));
                    break;
            }
        }

        return diagnostics.Any(diagnostic => diagnostic.IsError) ? null :
            guards.OrderBy(guard => guard.Kind).ThenBy(guard => guard.CanonicalIdentity, StringComparer.Ordinal).ToArray();
    }

    private static void CompileNoReferencesGuard(
        RequireNoReferencesMutationRelationshipGuard guard,
        ExecutableStorageRoute route,
        IReadOnlyDictionary<string, PhysicalRelationshipPlan> relationshipPlans,
        List<PhysicalMutationRelationshipGuard> guards,
        List<GroundworkDiagnostic> diagnostics,
        string location)
    {
        if (!relationshipPlans.TryGetValue(guard.RelationshipIdentity, out var relationship))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-016",
                $"Relationship guard references one admitted manifest relationship '{guard.RelationshipIdentity}', but no exact plan was found.",
                location));
            return;
        }
        if (relationship.TargetRoute.StorageUnit != route.StorageUnit)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-017",
                $"No-reference guard '{guard.RelationshipIdentity}' must delete the relationship target unit '{relationship.TargetRoute.StorageUnit.Value}', not '{route.StorageUnit.Value}'.",
                location));
            return;
        }
        guards.Add(new PhysicalRequireNoReferencesMutationGuard(relationship));
    }

    private static void CompileRelatedTargetNotEqualGuard(
        RequireRelatedTargetNotEqualMutationRelationshipGuard guard,
        ExecutableStorageRoute route,
        StorageManifest manifest,
        IReadOnlyDictionary<string, PhysicalRelationshipPlan> relationshipPlans,
        List<PhysicalMutationRelationshipGuard> guards,
        List<GroundworkDiagnostic> diagnostics,
        string location)
    {
        if (!relationshipPlans.TryGetValue(guard.RelationshipIdentity, out var relationship))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-016",
                $"Relationship guard references one admitted manifest relationship '{guard.RelationshipIdentity}', but no exact plan was found.",
                location));
            return;
        }
        if (relationship.SourceRoute.StorageUnit != route.StorageUnit)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-017",
                $"Related-target guard '{guard.RelationshipIdentity}' must mutate the relationship source unit '{relationship.SourceRoute.StorageUnit.Value}', not '{route.StorageUnit.Value}'.",
                location));
            return;
        }
        var targetUnit = manifest.StorageUnits.Single(unit => unit.Identity == relationship.TargetRoute.StorageUnit);
        var targetPredicate = ResolveProjectedScalar(
            relationship.TargetRoute,
            guard.TargetPredicatePath,
            diagnostics,
            location);
        var targetPredicateIndex = ResolvePredicateIndex(
            relationship.TargetRoute,
            targetUnit.PhysicalStorage!,
            guard.TargetPredicatePath,
            guard.TargetPredicateIndexIdentity,
            diagnostics,
            location,
            "GW-MUTATION-012");
        if (targetPredicate is null || targetPredicateIndex is null)
            return;
        if (targetPredicate.ValueKind != IndexValueKind.Keyword)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-012",
                $"Related-target predicate path '{guard.TargetPredicatePath}' must be a keyword scalar so the fixed not-equal value has portable semantics.",
                location));
            return;
        }
        guards.Add(new PhysicalRequireRelatedTargetNotEqualMutationGuard(
            relationship,
            targetPredicate,
            targetPredicateIndex,
            guard.DisallowedTargetValue));
    }

    private static ExecutablePhysicalIndexRoute? ResolvePredicateIndex(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage,
        string path,
        string indexIdentity,
        List<GroundworkDiagnostic> diagnostics,
        string location,
        string diagnosticCode)
    {
        var declarations = storage.LogicalIndexes
            .Where(candidate => candidate.Identity == indexIdentity)
            .ToArray();
        var index = declarations.Length == 1 &&
                    declarations[0].Fields.Count != 0 &&
                    declarations[0].Fields[0].Path == path &&
                    declarations[0].GetValueKind(declarations[0].Fields[0]) == IndexValueKind.Keyword
            ? declarations[0]
            : null;
        var physical = route.Indexes.SingleOrDefault(candidate => candidate.Identity == indexIdentity);
        if (index is null || declarations.Length != 1 || physical is null ||
            !physical.Columns.Any(column =>
                string.Equals(
                    column.Column.Identifier,
                    route.ProjectedColumns.SingleOrDefault(projection =>
                        projection.Target == ExecutableStorageObjectRole.PrimaryStorage &&
                        projection.Definition.Path == path)?.Column.Identifier,
                    StringComparison.Ordinal)))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                diagnosticCode,
                $"Relationship equality path '{path}' on storage unit '{route.StorageUnit.Value}' must be the leading keyword field of one declared and executable index '{indexIdentity}'.",
                location));
            return null;
        }
        return physical;
    }

    private static PhysicalQueryField? ResolveProjectedScalar(
        ExecutableStorageRoute route,
        string path,
        List<GroundworkDiagnostic> diagnostics,
        string location,
        string diagnosticCode = "GW-MUTATION-012")
    {
        var projection = route.ProjectedColumns.SingleOrDefault(candidate =>
            candidate.Target == ExecutableStorageObjectRole.PrimaryStorage &&
            candidate.Definition.Path == path);
        if (projection is null ||
            projection.Definition.Type != PortablePhysicalType.String ||
            projection.Definition.Cardinality != ProjectionCardinality.Scalar)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                diagnosticCode,
                $"Relationship path '{path}' on storage unit '{route.StorageUnit.Value}' must be a primary scalar string projection.",
                location));
            return null;
        }
        return new PhysicalQueryField(
            path,
            projection.Column.Identifier,
            PhysicalQueryFieldSource.ProjectedColumn,
            ExecutableStorageObjectRole.PrimaryStorage,
            route.PrimaryStorage.Name,
            IndexValueKind.Keyword);
    }

    private static PhysicalMutationAction? CompileAction(
        BoundedMutationDeclaration mutation,
        PhysicalQueryPlan predicate,
        ExecutableStorageRoute route,
        List<GroundworkDiagnostic> diagnostics)
    {
        if (mutation.Action is BoundedDeleteMutationAction)
            return new PhysicalDeleteMutationAction();

        var transition = (BoundedTransitionMutationAction)mutation.Action;
        var field = predicate.Predicates.SingleOrDefault(candidate => candidate.Path == transition.Path);
        if (field is null)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-003",
                $"Transition path '{transition.Path}' must be part of bounded predicate query '{predicate.QueryIdentity}'.",
                $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}.action"));
            return null;
        }
        if (!PhysicalDocumentFieldPaths.IsMutableContent(field.Field))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-005",
                $"Transition path '{transition.Path}' must resolve to document content; " +
                $"field source '{field.Field.Source}' is immutable mutation identity or envelope state.",
                $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}.action"));
            return null;
        }
        var projection = route.ProjectedColumns.SingleOrDefault(candidate =>
            string.Equals(candidate.Definition.Path, transition.Path, StringComparison.Ordinal));
        if (projection?.Definition.Cardinality == ProjectionCardinality.CollectionElements)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-007",
                $"Transition path '{transition.Path}' targets a collection projection. " +
                "Collection transitions require an atomic collection-replacement primitive.",
                $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}.action"));
            return null;
        }
        if (!field.Operations.Contains(Groundwork.Core.Indexing.PortableQueryOperation.Equal) &&
            !field.Operations.Contains(Groundwork.Core.Indexing.PortableQueryOperation.In))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-003",
                $"Transition path '{transition.Path}' must support exact source-value matching.",
                $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}.action"));
            return null;
        }
        if (transition.AllowedSourceValues.Count > 1 &&
            !field.Operations.Contains(Groundwork.Core.Indexing.PortableQueryOperation.In) &&
            !predicate.SupportsDisjunction)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-003",
                $"Transition path '{transition.Path}' requires IN or provider-certified disjunction for multiple source values.",
                $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}.action"));
            return null;
        }
        if (transition.AllowedSourceValues.Count > 1 &&
            predicate.RequiredEqualityPrefixPaths.Contains(transition.Path, StringComparer.Ordinal))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-003",
                $"Transition path '{transition.Path}' is an equality prefix and therefore requires exactly one source value.",
                $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}.action"));
            return null;
        }

        return new PhysicalTransitionMutationAction(
            transition.Path,
            transition.AllowedSourceValues,
            transition.TargetValue,
            field.Field);
    }
}
