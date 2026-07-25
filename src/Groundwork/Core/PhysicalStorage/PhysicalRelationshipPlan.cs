using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Stable provider-neutral identities for the materialized relationship and target-key fence.
/// They are derived from manifest and relationship identities only, never a mutable manifest
/// version or route fingerprint, so compatible upgrades address the same durable fence.
/// </summary>
public sealed record PhysicalRelationshipMaterializationIdentity(
    string ReferenceStorageIdentity,
    string ReferenceBySourceIndexIdentity,
    string ReferenceByTargetIndexIdentity,
    string FenceStorageIdentity,
    string FenceByTargetIndexIdentity)
{
    internal static PhysicalRelationshipMaterializationIdentity Create(
        StorageManifestIdentity manifest,
        ManifestRelationshipDeclaration relationship)
    {
        var root = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            PhysicalCanonicalEncoding.Join(
                manifest.Value,
                relationship.Identity,
                relationship.SourceStorageUnit.Value,
                relationship.TargetStorageUnit.Value)))).ToLowerInvariant();
        return new(
            $"relationship-reference:{root}",
            $"relationship-reference-by-source:{root}",
            $"relationship-reference-by-target:{root}",
            $"relationship-fence:{root}",
            $"relationship-fence-by-target:{root}");
    }
}

/// <summary>
/// Provider-neutral execution contract for one admitted manifest relationship. Providers read the
/// optional source reference from canonical JSON, project any present value with the target
/// identity policy, and persist the resulting comparison key in the generated materialization.
/// The source declaration index is admission evidence for the stable path; it is deliberately not
/// represented as comparable to the target identity key.
/// </summary>
public sealed record PhysicalRelationshipPlan(
    ManifestRelationshipDeclaration Declaration,
    ExecutableStorageRoute SourceRoute,
    PhysicalQueryField SourceCanonicalJsonReference,
    ExecutablePhysicalIndexRoute SourceReferenceDeclarationIndex,
    ExecutableStorageRoute TargetRoute,
    PhysicalQueryField TargetIdentityComparison,
    ExecutablePhysicalIndexRoute TargetEqualityIndex,
    PhysicalRelationshipMaterializationIdentity Materialization)
{
    public string Identity => Declaration.Identity;

    public string CanonicalIdentity => PhysicalCanonicalEncoding.Join(
        Declaration.Identity,
        SourceRoute.StorageUnit.Value,
        SourceRoute.Fingerprint,
        SourceCanonicalJsonReference.Path,
        SourceCanonicalJsonReference.Identifier,
        SourceCanonicalJsonReference.ObjectName.Identifier,
        SourceReferenceDeclarationIndex.Identity,
        SourceReferenceDeclarationIndex.Name.Identifier,
        TargetRoute.StorageUnit.Value,
        TargetRoute.Fingerprint,
        TargetIdentityComparison.Path,
        TargetIdentityComparison.Identifier,
        TargetIdentityComparison.ObjectName.Identifier,
        TargetEqualityIndex.Identity,
        TargetEqualityIndex.Name.Identifier,
        ((int)Declaration.ReferenceCasePolicy).ToString(System.Globalization.CultureInfo.InvariantCulture),
        TargetRoute.Envelope.Identity.ComparisonAlgorithmId,
        TargetRoute.Envelope.Identity.LookupAlgorithmId,
        Materialization.ReferenceStorageIdentity,
        Materialization.ReferenceBySourceIndexIdentity,
        Materialization.ReferenceByTargetIndexIdentity,
        Materialization.FenceStorageIdentity,
        Materialization.FenceByTargetIndexIdentity);

    /// <summary>
    /// Projects an optional serialized reference into the exact comparison key used by the target
    /// identity route. A missing value remains absent; malformed or empty identities fail closed.
    /// </summary>
    public string? ProjectReference(string? sourceReference) =>
        sourceReference is null
            ? null
            : TargetRoute.Envelope.Identity.Project(sourceReference).ComparisonKey;
}

public sealed class PhysicalRelationshipPlanCompilationResult
{
    public PhysicalRelationshipPlanCompilationResult(
        IReadOnlyList<PhysicalRelationshipPlan> plans,
        IReadOnlyList<GroundworkDiagnostic> diagnostics)
    {
        Plans = Array.AsReadOnly((plans ?? throw new ArgumentNullException(nameof(plans))).ToArray());
        Diagnostics = Array.AsReadOnly((diagnostics ?? throw new ArgumentNullException(nameof(diagnostics))).ToArray());
    }

    public IReadOnlyList<PhysicalRelationshipPlan> Plans { get; }

    public IReadOnlyList<GroundworkDiagnostic> Diagnostics { get; }

    public bool IsValid => Diagnostics.All(diagnostic => !diagnostic.IsError);
}

public sealed class PhysicalRelationshipProviderNotSupportedException : NotSupportedException
{
    public PhysicalRelationshipProviderNotSupportedException(
        ProviderIdentity provider,
        IReadOnlyList<string> relationshipIdentities)
        : base(
            $"GW-RELATIONSHIP-012: Provider '{provider.Name}' does not certify relationship materialization and target-key fences for: " +
            string.Join(", ", relationshipIdentities))
    {
        Provider = provider;
        RelationshipIdentities = Array.AsReadOnly(relationshipIdentities.ToArray());
    }

    public ProviderIdentity Provider { get; }

    public IReadOnlyList<string> RelationshipIdentities { get; }
}

/// <summary>
/// Temporary fail-closed boundary for provider runtimes that have not implemented the admitted
/// relationship plan. Providers must not perform schema or document I/O until they advertise and
/// certify the generated reference materialization and target-key fence.
/// </summary>
public static class PhysicalRelationshipProviderAdmission
{
    public static void RequireMaterializationSupport(
        StorageManifest manifest,
        ProviderIdentity provider,
        bool supportsRelationshipMaterialization)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);
        if (supportsRelationshipMaterialization || manifest.Relationships.Count == 0)
            return;
        throw new PhysicalRelationshipProviderNotSupportedException(
            provider,
            manifest.Relationships
                .Select(relationship => relationship.Identity)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }
}

/// <summary>
/// Admits every manifest relationship independently of bounded mutations so ordinary writes,
/// deletes, unit-of-work commits, and guarded mutations all consume one immutable contract.
/// </summary>
public static class PhysicalRelationshipPlanCompiler
{
    public static PhysicalRelationshipPlanCompilationResult Compile(
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(routes);
        var diagnostics = new List<GroundworkDiagnostic>();
        var routeGroups = routes
            .GroupBy(route => route.StorageUnit)
            .ToDictionary(group => group.Key, group => group.ToArray());
        if (routes.Count != manifest.StorageUnits.Count ||
            routeGroups.Any(group => group.Value.Length != 1) ||
            manifest.StorageUnits.Any(unit => !routeGroups.ContainsKey(unit.Identity)))
        {
            diagnostics.Add(Error(
                "GW-RELATIONSHIP-001",
                "Relationship admission requires exactly one executable route for every manifest storage unit.",
                "physicalRelationships"));
            return new([], diagnostics);
        }

        var duplicateIdentities = manifest.Relationships
            .GroupBy(relationship => relationship.Identity, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (duplicateIdentities.Length != 0)
        {
            diagnostics.Add(Error(
                "GW-RELATIONSHIP-002",
                $"Relationship identities must be unique: {string.Join(", ", duplicateIdentities)}.",
                "physicalRelationships"));
            return new([], diagnostics);
        }

        var units = manifest.StorageUnits.ToDictionary(unit => unit.Identity);
        var plans = new List<PhysicalRelationshipPlan>();
        foreach (var relationship in manifest.Relationships.OrderBy(item => item.Identity, StringComparer.Ordinal))
        {
            var location = $"physicalRelationships.{relationship.Identity}";
            if (!units.TryGetValue(relationship.SourceStorageUnit, out var sourceUnit) ||
                sourceUnit.PhysicalStorage is null ||
                !routeGroups.TryGetValue(relationship.SourceStorageUnit, out var sourceRouteGroup))
            {
                diagnostics.Add(Error(
                    "GW-RELATIONSHIP-003",
                    $"Relationship '{relationship.Identity}' requires one admitted source storage unit and route '{relationship.SourceStorageUnit.Value}'.",
                    location));
                continue;
            }
            if (!units.TryGetValue(relationship.TargetStorageUnit, out var targetUnit) ||
                targetUnit.PhysicalStorage is null ||
                !routeGroups.TryGetValue(relationship.TargetStorageUnit, out var targetRouteGroup))
            {
                diagnostics.Add(Error(
                    "GW-RELATIONSHIP-004",
                    $"Relationship '{relationship.Identity}' requires one admitted target storage unit and route '{relationship.TargetStorageUnit.Value}'.",
                    location));
                continue;
            }
            if (relationship.SourceStorageUnit == relationship.TargetStorageUnit)
            {
                diagnostics.Add(Error(
                    "GW-RELATIONSHIP-005",
                    $"Relationship '{relationship.Identity}' uses unsupported same-unit topology; relationship guards require distinct source and target units.",
                    location));
                continue;
            }

            var sourceRoute = sourceRouteGroup[0];
            var targetRoute = targetRouteGroup[0];
            if (sourceUnit.Tenancy != targetUnit.Tenancy ||
                sourceRoute.ScopePolicy != targetRoute.ScopePolicy)
            {
                diagnostics.Add(Error(
                    "GW-RELATIONSHIP-006",
                    $"Relationship '{relationship.Identity}' crosses incompatible source and target scope policies.",
                    location));
                continue;
            }
            if (targetUnit.IdentityPolicy.Kind != StorageIdentityKind.String ||
                relationship.TargetIdentityPath != PhysicalDocumentFieldPaths.Id)
            {
                diagnostics.Add(Error(
                    "GW-RELATIONSHIP-007",
                    $"Relationship '{relationship.Identity}' requires a string target identity at '{PhysicalDocumentFieldPaths.Id}'.",
                    location));
                continue;
            }
            if (relationship.ReferenceCasePolicy != targetUnit.IdentityPolicy.StringCasePolicy ||
                relationship.ReferenceCasePolicy != targetRoute.Envelope.Identity.StringCasePolicy)
            {
                diagnostics.Add(Error(
                    "GW-RELATIONSHIP-008",
                    $"Relationship '{relationship.Identity}' reference case policy does not match the target identity policy.",
                    location));
                continue;
            }
            if (PhysicalDocumentFieldPaths.IsEnvelope(relationship.SourceReferencePath))
            {
                diagnostics.Add(Error(
                    "GW-RELATIONSHIP-009",
                    $"Relationship '{relationship.Identity}' source reference must be a stable canonical JSON content path.",
                    location));
                continue;
            }

            var sourceProjection = sourceRoute.ProjectedColumns.SingleOrDefault(candidate =>
                candidate.Definition.Path == relationship.SourceReferencePath);
            var sourceIndex = ResolveIndex(
                sourceRoute,
                sourceUnit.PhysicalStorage,
                relationship.SourceReferencePath,
                relationship.SourceReferenceIndexIdentity,
                sourceProjection?.Column.Identifier);
            if (sourceProjection is null ||
                sourceProjection.Definition.Type != PortablePhysicalType.String ||
                sourceProjection.Definition.Cardinality != ProjectionCardinality.Scalar ||
                sourceIndex is null)
            {
                diagnostics.Add(Error(
                    "GW-RELATIONSHIP-010",
                    $"Relationship '{relationship.Identity}' source reference path must be a scalar string with the exact declared reference index '{relationship.SourceReferenceIndexIdentity}'.",
                    location));
                continue;
            }

            var targetIndex = ResolveIndex(
                targetRoute,
                targetUnit.PhysicalStorage,
                relationship.TargetIdentityPath,
                relationship.TargetEqualityIndexIdentity,
                targetRoute.Envelope.Identity.ComparisonKey.Identifier);
            if (targetIndex is null)
            {
                diagnostics.Add(Error(
                    "GW-RELATIONSHIP-011",
                    $"Relationship '{relationship.Identity}' target identity requires the exact comparison-key index '{relationship.TargetEqualityIndexIdentity}'.",
                    location));
                continue;
            }

            var sourceReference = new PhysicalQueryField(
                relationship.SourceReferencePath,
                sourceRoute.Envelope.CanonicalJson.Identifier,
                PhysicalQueryFieldSource.CanonicalJsonPath,
                ExecutableStorageObjectRole.PrimaryStorage,
                sourceRoute.PrimaryStorage.Name,
                IndexValueKind.Keyword);
            var targetIdentity = new PhysicalQueryField(
                PhysicalDocumentFieldPaths.Id,
                targetRoute.Envelope.Identity.ComparisonKey.Identifier,
                PhysicalQueryFieldSource.Envelope,
                ExecutableStorageObjectRole.PrimaryStorage,
                targetRoute.PrimaryStorage.Name,
                IndexValueKind.Keyword);
            plans.Add(new PhysicalRelationshipPlan(
                relationship,
                sourceRoute,
                sourceReference,
                sourceIndex,
                targetRoute,
                targetIdentity,
                targetIndex,
                PhysicalRelationshipMaterializationIdentity.Create(manifest.Identity, relationship)));
        }

        return diagnostics.Any(diagnostic => diagnostic.IsError)
            ? new([], diagnostics)
            : new(plans, diagnostics);
    }

    private static ExecutablePhysicalIndexRoute? ResolveIndex(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage,
        string path,
        string indexIdentity,
        string? expectedColumnIdentifier)
    {
        var declarations = storage.LogicalIndexes
            .Where(candidate => candidate.Identity == indexIdentity)
            .ToArray();
        if (declarations.Length != 1 ||
            declarations[0].Fields.Count == 0 ||
            declarations[0].Fields[0].Path != path ||
            declarations[0].GetValueKind(declarations[0].Fields[0]) != IndexValueKind.Keyword)
        {
            return null;
        }
        return route.Indexes.SingleOrDefault(candidate =>
            candidate.Identity == indexIdentity &&
            candidate.Target == ExecutableStorageObjectRole.PrimaryStorage &&
            expectedColumnIdentifier is not null &&
            candidate.Columns.Any(column =>
                column.Column.Identifier == expectedColumnIdentifier));
    }

    private static GroundworkDiagnostic Error(string code, string message, string target) =>
        GroundworkDiagnostic.Error(code, message, target);
}

internal static class PhysicalCanonicalEncoding
{
    public static string Join(params string?[] values) =>
        string.Concat(values.Select(value => value is null ? "-1:" : $"{value.Length}:{value}"));
}
