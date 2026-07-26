using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Scoping;
using Groundwork.Core.Text;
using Groundwork.Core.Validation;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Stable provider-neutral identities for one semantic generation of the materialized relationship
/// and target-key fence. Physical renames and mutable manifest versions do not rotate the slot, but
/// path, index, scope, case-policy, or target identity-algorithm changes do.
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
        ManifestRelationshipDeclaration relationship,
        ExecutableStorageRoute sourceRoute,
        ExecutableStorageRoute targetRoute)
    {
        var root = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            PhysicalCanonicalEncoding.Join(
                manifest.Value,
                relationship.Identity,
                relationship.SourceStorageUnit.Value,
                relationship.SourceReferencePath,
                relationship.SourceReferenceIndexIdentity,
                relationship.TargetStorageUnit.Value,
                relationship.TargetIdentityPath,
                relationship.TargetEqualityIndexIdentity,
                ((int)relationship.ReferenceCasePolicy).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ((int)sourceRoute.ScopePolicy).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ((int)targetRoute.ScopePolicy).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ((int)sourceRoute.Envelope.Identity.StringCasePolicy).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                sourceRoute.Envelope.Identity.ComparisonAlgorithmId,
                sourceRoute.Envelope.Identity.LookupAlgorithmId,
                targetRoute.Envelope.Identity.ComparisonAlgorithmId,
                targetRoute.Envelope.Identity.LookupAlgorithmId)))).ToLowerInvariant();
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

    /// <summary>
    /// The generated provider-neutral relationship reference and target-key fence schema. This is
    /// intentionally a pure, non-authoritative contract. Relationship manifests remain behind an
    /// unconditional fail-closed prerequisite boundary: no provider capability can currently be
    /// advertised and no certification gate exists yet.
    /// </summary>
    public PhysicalRelationshipMaterializationSchema MaterializationSchema =>
        PhysicalRelationshipMaterializationSchema.Create(this);

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
    public string? ProjectReference(string? sourceReference)
        => ProjectReferenceIdentity(sourceReference)?.ComparisonKey;

    /// <summary>
    /// Projects an optional serialized reference with the target identity route. Both lookup and
    /// comparison keys are exposed for generated materialization and fence keys; callers must not
    /// treat the lookup hash as a complete identity.
    /// </summary>
    public PortableStringIdentityProjection? ProjectReferenceIdentity(string? sourceReference)
    {
        if (sourceReference is null)
            return null;
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceReference);
        return TargetRoute.Envelope.Identity.Project(sourceReference);
    }
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

/// <summary>
/// An executable route set resolved and compiled from one exact manifest in one operation. The
/// constructor is intentionally private so cross-unit admission cannot combine a local route with
/// remote routes compiled from another manifest.
/// </summary>
public sealed class ManifestExecutableRouteSet
{
    private ManifestExecutableRouteSet(
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes)
    {
        Manifest = Snapshot(manifest);
        Routes = Array.AsReadOnly(routes
            .OrderBy(route => route.StorageUnit.Value, StringComparer.Ordinal)
            .ToArray());
    }

    internal StorageManifest Manifest { get; }

    public StorageManifestIdentity ManifestIdentity => Manifest.Identity;

    public StorageManifestVersion ManifestVersion => Manifest.Version;

    public IReadOnlyList<ExecutableStorageRoute> Routes { get; }

    internal static ManifestExecutableRouteSet Create(
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes) =>
        new(manifest, routes);

    internal static StorageManifest Snapshot(StorageManifest manifest) =>
        new(
            manifest.Identity,
            manifest.Owner,
            manifest.Version,
            manifest.StorageUnits.Select(Snapshot).ToArray(),
            manifest.RequiredCapabilities.ToHashSet(StringComparer.Ordinal),
            manifest.CompatibilityNotes.ToArray())
        {
            SharedDocumentStorages = manifest.SharedDocumentStorages.ToArray(),
            Relationships = manifest.Relationships.ToArray()
        };

    private static StorageUnit Snapshot(StorageUnit unit) =>
        unit with
        {
            Indexes = unit.Indexes.ToArray(),
            Queries = unit.Queries.ToArray(),
            PhysicalStorage = unit.PhysicalStorage is null
                ? null
                : new StorageUnitPhysicalStorage(
                    unit.PhysicalStorage.ProvisioningMode,
                    unit.PhysicalStorage.Policy,
                    unit.PhysicalStorage.LogicalIndexes,
                    unit.PhysicalStorage.BoundedQueries,
                    unit.PhysicalStorage.NameOverrides,
                    unit.PhysicalStorage.BoundedMutations)
        };
}

public sealed class ManifestExecutableRouteSetCompilationResult
{
    public ManifestExecutableRouteSetCompilationResult(
        ManifestExecutableRouteSet? routeSet,
        IReadOnlyList<GroundworkDiagnostic> diagnostics)
    {
        RouteSet = routeSet;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public ManifestExecutableRouteSet? RouteSet { get; }

    public IReadOnlyList<GroundworkDiagnostic> Diagnostics { get; }

    public bool IsValid => RouteSet is not null && Diagnostics.All(diagnostic => !diagnostic.IsError);
}

/// <summary>
/// Resolves and compiles all routes from one manifest before exposing the sealed route set used by
/// cross-unit admission.
/// </summary>
public static class ManifestExecutableRouteSetCompiler
{
    public static ManifestExecutableRouteSetCompilationResult Compile(
        StorageManifest manifest,
        IPhysicalNamePolicy namePolicy,
        IProviderPhysicalNameNormalizer providerNames)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(namePolicy);
        ArgumentNullException.ThrowIfNull(providerNames);
        var snapshot = ManifestExecutableRouteSet.Snapshot(manifest);
        var diagnostics = new StorageManifestValidator().Validate(snapshot).Diagnostics.ToList();
        if (diagnostics.Any(diagnostic => diagnostic.IsError))
            return new(null, diagnostics);

        var resolution = PhysicalStorageResolver.Resolve(snapshot, namePolicy, providerNames);
        diagnostics.AddRange(resolution.Diagnostics);
        if (!resolution.IsValid)
            return new(null, diagnostics);

        var routes = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        diagnostics.AddRange(routes.Diagnostics);
        return routes.IsValid
            ? new(ManifestExecutableRouteSet.Create(snapshot, routes.Routes), diagnostics)
            : new(null, diagnostics);
    }
}

public sealed class PhysicalRelationshipProviderNotSupportedException : NotSupportedException
{
    public PhysicalRelationshipProviderNotSupportedException(
        ProviderIdentity provider,
        IReadOnlyList<string> relationshipIdentities)
        : base(
            $"GW-RELATIONSHIP-012: Relationship manifests are currently unavailable for provider '{provider.Name}' at the unconditional fail-closed prerequisite boundary: " +
            string.Join(", ", relationshipIdentities))
    {
        Provider = provider;
        RelationshipIdentities = Array.AsReadOnly(relationshipIdentities.ToArray());
    }

    public ProviderIdentity Provider { get; }

    public IReadOnlyList<string> RelationshipIdentities { get; }
}

/// <summary>
/// Unconditional fail-closed prerequisite boundary for relationship manifests. Providers must not
/// perform schema or document I/O for these manifests; no provider capability can currently be
/// advertised and no certification gate exists yet. There is deliberately no public override.
/// </summary>
public static class PhysicalRelationshipProviderAdmission
{
    public static void RequireMaterializationSupport(
        StorageManifest manifest,
        ProviderIdentity provider)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);
        var relationshipIdentities = manifest.Relationships
            .Select(relationship => relationship.Identity)
            .Concat(manifest.StorageUnits
                .SelectMany(unit => unit.PhysicalStorage?.BoundedMutations ?? [])
                .SelectMany(mutation => mutation.RelationshipGuards)
                .Select(guard => guard.RelationshipIdentity))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (relationshipIdentities.Length == 0)
            return;
        throw new PhysicalRelationshipProviderNotSupportedException(
            provider,
            relationshipIdentities);
    }
}

/// <summary>
/// Admits every manifest relationship independently of bounded mutations so ordinary writes,
/// deletes, unit-of-work commits, and guarded mutations all consume one immutable contract.
/// </summary>
public static class PhysicalRelationshipPlanCompiler
{
    public static PhysicalRelationshipPlanCompilationResult Compile(
        ManifestExecutableRouteSet routeSet)
    {
        ArgumentNullException.ThrowIfNull(routeSet);
        var manifest = routeSet.Manifest;
        var routes = routeSet.Routes;
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
                PhysicalRelationshipMaterializationIdentity.Create(
                    manifest.Identity,
                    relationship,
                    sourceRoute,
                    targetRoute)));
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
            HasRelationshipEqualityPrefix(route, candidate, expectedColumnIdentifier));
    }

    internal static bool HasRelationshipEqualityPrefix(
        ExecutableStorageRoute route,
        ExecutablePhysicalIndexRoute index,
        string fieldIdentifier)
    {
        var expected = new List<string>();
        if (route.Discriminator.ParticipatesInPrimaryKey)
            expected.Add(route.Discriminator.Column.Identifier);
        if (route.ScopePolicy == StorageScopePolicy.Scoped &&
            route.ScopeKey.ParticipatesInPrimaryKey)
            expected.Add(route.ScopeKey.Column.Identifier);
        expected.Add(fieldIdentifier);
        return index.Target == ExecutableStorageObjectRole.PrimaryStorage &&
               index.Columns.Count >= expected.Count &&
               index.Columns
                   .Take(expected.Count)
                   .Select(column => column.Column.Identifier)
                   .SequenceEqual(expected, StringComparer.Ordinal);
    }

    private static GroundworkDiagnostic Error(string code, string message, string target) =>
        GroundworkDiagnostic.Error(code, message, target);
}

internal static class PhysicalCanonicalEncoding
{
    public static string Join(params string?[] values) =>
        string.Concat(values.Select(value => value is null ? "-1:" : $"{value.Length}:{value}"));
}
