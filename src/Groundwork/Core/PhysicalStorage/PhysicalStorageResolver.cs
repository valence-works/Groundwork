using System.Text;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;
using Groundwork.Core.Scoping;
using Groundwork.Core.Validation;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Resolves provider-neutral manifest intent through host naming and provider normalization. This
/// module does not execute DDL or route runtime document operations. Orchestrates
/// <see cref="PhysicalStorageDeclarationValidator"/>, <see cref="PhysicalDefinitionValidator"/>,
/// and <see cref="PhysicalHostNameResolver"/>, and owns definition synthesis.
/// </summary>
public static class PhysicalStorageResolver
{
    public static PhysicalStorageResolutionResult Resolve(
        StorageManifest manifest,
        IPhysicalNamePolicy namePolicy,
        IProviderPhysicalNameNormalizer providerNameNormalizer)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(namePolicy);
        ArgumentNullException.ThrowIfNull(providerNameNormalizer);

        var diagnostics = new List<GroundworkDiagnostic>();
        var definitions = new List<ProviderPhysicalTableDefinition>();
        var sharedPrimaryNames = new Dictionary<string, ResolvedPhysicalObjectName>(StringComparer.Ordinal);
        var providerNamesByInput = new Dictionary<
            (string NamingOwner, PhysicalObjectKind ObjectKind, string LogicalName),
            ProviderPhysicalObjectName>();

        foreach (var unit in manifest.StorageUnits)
        {
            if (IdentityPolicyAdmission.Validate(
                    unit.IdentityPolicy,
                    $"storageUnits.{unit.Identity.Value}.identityPolicy") is { } identityPolicyDiagnostic)
            {
                diagnostics.Add(identityPolicyDiagnostic);
                continue;
            }

            if (unit.PhysicalStorage is null)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-001",
                    $"Storage unit '{unit.Identity.Value}' uses the legacy physicalization model; convert it explicitly through LegacyPhysicalStorageBridge.",
                    $"storageUnits.{unit.Identity.Value}.physicalStorage"));
                continue;
            }

            if (!TryResolveScopePolicy(unit, diagnostics, out var scopePolicy))
                continue;

            if (!PhysicalStorageDeclarationValidator.ValidateDeclarations(unit, diagnostics))
                continue;

            var errorCount = diagnostics.Count(diagnostic => diagnostic.IsError);
            var demand = ResolveScaleBearingDemand(unit.PhysicalStorage, unit.Identity, diagnostics);
            if (diagnostics.Count(diagnostic => diagnostic.IsError) != errorCount)
                continue;
            var definition = ResolveDefinition(unit, manifest, demand, diagnostics);
            if (definition is null)
                continue;

            if (!PhysicalDefinitionValidator.ValidateDefinition(unit, manifest, definition, demand, diagnostics))
                continue;

            SharedDocumentStorageDefinition? sharedStorageDefinition = null;
            if (definition.Form == PhysicalStorageForm.SharedDocuments &&
                !TryGetSharedDefinition(
                    manifest,
                    definition.SharedStorage!,
                    unit.Identity,
                    diagnostics,
                    out sharedStorageDefinition))
            {
                continue;
            }

            var names = PhysicalHostNameResolver.ResolveHostNames(
                unit,
                definition,
                sharedStorageDefinition,
                namePolicy,
                sharedPrimaryNames,
                diagnostics);
            if (names.Count(x => x.ObjectKind == PhysicalObjectKind.PrimaryStorage) != 1)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-029",
                    $"Storage unit '{unit.Identity.Value}' must resolve exactly one primary storage logical name.",
                    $"storageUnits.{unit.Identity.Value}.physicalStorage.names"));
                continue;
            }

            var resolved = new ResolvedPhysicalTableDefinition(
                unit.Identity,
                unit.PhysicalStorage.ProvisioningMode,
                unit.IdentityPolicy,
                definition,
                sharedStorageDefinition,
                demand.ToArray(),
                names.ToArray())
            {
                ScopePolicy = scopePolicy
            };
            var providerNames = PhysicalHostNameResolver.NormalizeNames(
                resolved,
                providerNameNormalizer,
                providerNamesByInput,
                diagnostics);
            if (providerNames.Count(x => x.ObjectKind == PhysicalObjectKind.PrimaryStorage) != 1)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-029",
                    $"Storage unit '{unit.Identity.Value}' must normalize exactly one provider primary storage identifier.",
                    $"storageUnits.{unit.Identity.Value}.physicalStorage.names"));
                continue;
            }

            definitions.Add(new ProviderPhysicalTableDefinition(
                resolved,
                providerNames.ToArray(),
                PhysicalStorageDefinitionSerializer.CreateFingerprint(resolved, providerNames)));
        }

        PhysicalHostNameResolver.AddProviderNameCollisions(definitions, diagnostics);
        return new PhysicalStorageResolutionResult(definitions.ToArray(), diagnostics.ToArray());
    }

    private static bool TryResolveScopePolicy(
        StorageUnit unit,
        List<GroundworkDiagnostic> diagnostics,
        out StorageScopePolicy scopePolicy)
    {
        switch (unit.Tenancy.Kind)
        {
            case TenancyKind.Global:
                scopePolicy = StorageScopePolicy.Global;
                return true;
            case TenancyKind.Scoped:
                scopePolicy = StorageScopePolicy.Scoped;
                return true;
            default:
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-030",
                    $"Storage unit '{unit.Identity.Value}' uses unsupported tenancy kind '{unit.Tenancy.Kind}' and cannot resolve physical storage.",
                    $"storageUnits.{unit.Identity.Value}.tenancy"));
                scopePolicy = default;
                return false;
        }
    }

    private static IReadOnlyList<ScaleBearingPathDemand> ResolveScaleBearingDemand(
        StorageUnitPhysicalStorage storage,
        StorageUnitIdentity unitIdentity,
        List<GroundworkDiagnostic> diagnostics)
    {
        var indexes = storage.LogicalIndexes
            .GroupBy(x => x.Identity, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        var demand = new List<ScaleBearingPathDemand>();

        foreach (var query in storage.BoundedQueries.Where(x => x.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing))
        {
            if (!indexes.TryGetValue(query.IndexIdentity, out var matching) || matching.Count != 1)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-006",
                    $"Scale-bearing query '{query.Identity}' must reference exactly one declared logical index '{query.IndexIdentity}'.",
                    $"storageUnits.{unitIdentity.Value}.physicalStorage.boundedQueries.{query.Identity}"));
                continue;
            }

            var sortDirections = PhysicalDefinitionValidator.ResolveSortDirections(query, matching[0]);
            foreach (var (field, order) in matching[0].Fields.Select((field, order) => (field, order)))
            {
                if (string.IsNullOrWhiteSpace(field.Path))
                {
                    diagnostics.Add(GroundworkDiagnostic.Error(
                        "GW-PHYSICAL-007",
                        $"Scale-bearing query '{query.Identity}' references an index with an empty serialized path.",
                        $"storageUnits.{unitIdentity.Value}.physicalStorage.logicalIndexes.{query.IndexIdentity}"));
                    continue;
                }

                var valueKind = matching[0].GetValueKind(field);
                var isNumber = valueKind == IndexValueKind.Number;
                demand.Add(new ScaleBearingPathDemand(
                    query.Identity,
                    query.IndexIdentity,
                    field.Path,
                    sortDirections[order],
                    valueKind,
                    PhysicalStorageDeclarationValidator.IsStringKind(valueKind) ? matching[0].GetLength(field) : null,
                    isNumber ? matching[0].GetPrecision(field) : null,
                    isNumber ? matching[0].GetScale(field) : null,
                    matching[0].MissingValueBehavior,
                    Array.AsReadOnly(query.Operations.Order().ToArray()),
                    query.SortSupport,
                    query.PagingSupport,
                    query.SupportsDisjunction,
                    query.SupportsTotalCount,
                    query.PredicateBindingMode,
                    Array.AsReadOnly(query.PredicateFields.ToArray()),
                    Array.AsReadOnly(query.ResidualPredicateFields.ToArray()),
                    Array.AsReadOnly(query.ResultOperations.Order().ToArray()),
                    query.LatestPerKeyPath));
            }
        }

        var conflictingResidualKinds = storage.BoundedQueries
            .Where(query => query.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing)
            .SelectMany(query => query.ResidualPredicateFields)
            .GroupBy(field => field.Path, StringComparer.Ordinal)
            .Where(group => group.Select(field => field.ValueKind).Distinct().Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (conflictingResidualKinds.Length != 0)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-036",
                $"Scale-bearing residual predicate paths must have one value kind per storage unit: {string.Join(", ", conflictingResidualKinds)}.",
                $"storageUnits.{unitIdentity.Value}.physicalStorage.boundedQueries"));
        }

        // An omitted length is an unbounded contract, not a missing opinion: mixing it with a declared
        // length for the same path would silently narrow the shared projected column and reject writes
        // the unbounded declaration permits. Consistency is enforced only for paths scale-bearing
        // demand actually projects — declarations nothing demands synthesize nothing and stay inert —
        // but for a demanded path every typed declaration site participates: string-kind fields of
        // all logical indexes, queried or not, and scale-bearing residual predicates.
        var scaleBearingResiduals = storage.BoundedQueries
            .Where(query => query.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing)
            .SelectMany(query => query.ResidualPredicateFields)
            .ToArray();
        var demandedStringPaths = demand
            .Where(x => PhysicalStorageDeclarationValidator.IsStringKind(x.ValueKind))
            .Select(x => x.Path)
            .Concat(scaleBearingResiduals
                .Where(field => PhysicalStorageDeclarationValidator.IsStringKind(field.ValueKind))
                .Select(field => field.Path))
            .ToHashSet(StringComparer.Ordinal);
        var conflictingKeyLengths = storage.LogicalIndexes
            .SelectMany(index => index.Fields
                .Where(field => PhysicalStorageDeclarationValidator.IsStringKind(index.GetValueKind(field)))
                .Select(field => (field.Path, Length: index.GetLength(field))))
            .Concat(scaleBearingResiduals
                .Where(field => PhysicalStorageDeclarationValidator.IsStringKind(field.ValueKind))
                .Select(field => (field.Path, field.Length)))
            .Where(x => demandedStringPaths.Contains(x.Path))
            .GroupBy(x => x.Path, StringComparer.Ordinal)
            .Where(group => group.Select(x => x.Length).Distinct().Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (conflictingKeyLengths.Length != 0)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-039",
                $"Scale-bearing string paths must declare one key length, or none, per storage unit: {string.Join(", ", conflictingKeyLengths)}.",
                $"storageUnits.{unitIdentity.Value}.physicalStorage"));
        }

        // The same demand-scoped declaration-consistency rule applies to the numeric twin: every
        // Number declaration of a demanded path must agree on one precision and scale.
        var demandedNumericPaths = demand
            .Where(x => x.ValueKind == IndexValueKind.Number)
            .Select(x => x.Path)
            .ToHashSet(StringComparer.Ordinal);
        var conflictingNumericShapes = storage.LogicalIndexes
            .SelectMany(index => index.Fields
                .Where(field => index.GetValueKind(field) == IndexValueKind.Number)
                .Select(field => (field.Path, Precision: index.GetPrecision(field), Scale: index.GetScale(field))))
            .Where(x => demandedNumericPaths.Contains(x.Path))
            .GroupBy(x => x.Path, StringComparer.Ordinal)
            .Where(group => group.Select(x => (x.Precision, x.Scale)).Distinct().Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (conflictingNumericShapes.Length != 0)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-038",
                $"Scale-bearing numeric paths must declare one decimal precision and scale per storage unit: {string.Join(", ", conflictingNumericShapes)}.",
                $"storageUnits.{unitIdentity.Value}.physicalStorage.logicalIndexes"));
        }

        return demand
            .Distinct()
            .OrderBy(x => x.QueryIdentity, StringComparer.Ordinal)
            .ThenBy(x => x.IndexIdentity, StringComparer.Ordinal)
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static PhysicalTableDefinition? ResolveDefinition(
        StorageUnit unit,
        StorageManifest manifest,
        IReadOnlyList<ScaleBearingPathDemand> demand,
        List<GroundworkDiagnostic> diagnostics)
    {
        var storage = unit.PhysicalStorage!;
        if (storage.Policy is PhysicalStoragePolicy.ExplicitPolicy explicitPolicy)
            return ValidateExplicit(unit, manifest, explicitPolicy.Definition, diagnostics)
                ? explicitPolicy.Definition
                : null;

        var defaultPolicy = (PhysicalStoragePolicy.DefaultPolicy)storage.Policy;
        if (storage.ProvisioningMode == StorageUnitProvisioningMode.Dynamic)
        {
            if (defaultPolicy.SharedStorage is null)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-002",
                    "Dynamic storage using the default policy requires a shared-storage binding.",
                    $"storageUnits.{unit.Identity.Value}.physicalStorage.policy"));
                return null;
            }

            if (!TryGetSharedDefinition(
                    manifest,
                    defaultPolicy.SharedStorage,
                    unit.Identity,
                    diagnostics,
                    out var sharedDefinition))
                return null;

            var projected = SynthesizeProjectedColumns(demand);
            var physicalIndexes = SynthesizePhysicalIndexes(
                unit,
                storage,
                projected,
                sharedDefinition!.Envelope);
            var hasLinkedStructures = projected.Count != 0 || physicalIndexes.Count != 0;
            return PhysicalTableDefinition.SharedDocuments(
                defaultPolicy.SharedStorage,
                projected,
                physicalIndexes,
                linkedProjectionLogicalName: hasLinkedStructures
                    ? $"{unit.Identity.Value}_projection"
                    : null);
        }

        if (defaultPolicy.SharedStorage is not null)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-003",
                "Declared storage using the default policy cannot supply a shared-storage binding.",
                $"storageUnits.{unit.Identity.Value}.physicalStorage.policy"));
            return null;
        }

        var envelope = new DocumentEnvelopeDefinition();
        var projectedColumns = SynthesizeProjectedColumns(demand);
        var indexes = SynthesizePhysicalIndexes(unit, storage, projectedColumns, envelope);
        return projectedColumns.Count == 0
            ? PhysicalTableDefinition.DedicatedDocumentTable(
                unit.Identity.Value,
                envelope,
                indexes)
            : PhysicalTableDefinition.PhysicalEntityTable(
                unit.Identity.Value,
                projectedColumns,
                envelope,
                indexes);
    }

    private static IReadOnlyList<ProjectedColumnDefinition> SynthesizeProjectedColumns(
        IReadOnlyList<ScaleBearingPathDemand> demand) =>
        demand
            .SelectMany(x => new[] { new ProjectedPathDemand(x.Path, x.ValueKind, x.Length, x.Precision, x.Scale) }
                .Concat(x.ResidualPredicateFields.Select(residual =>
                    new ProjectedPathDemand(residual.Path, residual.ValueKind, residual.Length, null, null))))
            .Where(x => !PhysicalDocumentFieldPaths.IsEnvelope(x.Path))
            .GroupBy(x => x.Path, StringComparer.Ordinal)
            .Select(group => new ProjectedColumnDefinition(
                FeatureDefaultColumnName(group.Key),
                group.Key,
                ToPortableType(group.First().ValueKind),
                Length: group.Select(x => x.Length).FirstOrDefault(length => length is not null),
                Precision: group.Select(x => x.Precision).FirstOrDefault(precision => precision is not null),
                Scale: group.Select(x => x.Scale).FirstOrDefault(scale => scale is not null)))
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<PhysicalIndexDefinition> SynthesizePhysicalIndexes(
        StorageUnit unit,
        StorageUnitPhysicalStorage storage,
        IReadOnlyList<ProjectedColumnDefinition> projectedColumns,
        DocumentEnvelopeDefinition envelope)
    {
        var projectedNames = projectedColumns.ToDictionary(
            x => x.Path,
            x => x.LogicalName,
            StringComparer.Ordinal);
        var scaleBearingQueries = storage.BoundedQueries
            .Where(x => x.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing)
            .GroupBy(x => x.IndexIdentity, StringComparer.Ordinal);
        var physicalIndexes = new List<PhysicalIndexDefinition>();
        foreach (var queryGroup in scaleBearingQueries)
        {
            var logicalIndex = storage.LogicalIndexes.SingleOrDefault(x => x.Identity == queryGroup.Key);
            if (logicalIndex is null)
                continue;

            var sortDirections = PhysicalDefinitionValidator.ResolveCanonicalSortDirections(queryGroup, logicalIndex);
            var columns = new List<PhysicalIndexColumnDefinition>();
            if (PhysicalDefinitionValidator.RequiresStorageScope(unit, logicalIndex))
            {
                columns.Add(new PhysicalIndexColumnDefinition(
                    envelope.StorageScopeColumn,
                    columns.Count));
            }

            var firstFieldOrder = columns.Count;
            columns.AddRange(logicalIndex.Fields.Select((field, order) => new PhysicalIndexColumnDefinition(
                PhysicalDocumentFieldPaths.IsEnvelope(field.Path)
                    ? PhysicalDefinitionValidator.EnvelopeColumnName(envelope, field.Path)
                    : projectedNames[field.Path],
                firstFieldOrder + order,
                sortDirections[order])));
            var tieBreakQueries = queryGroup
                .Where(query => PhysicalQueryOrderRequirements.RequiresProviderAppliedIdentityTieBreak(
                    logicalIndex,
                    query))
                .ToArray();
            if (tieBreakQueries.Length != 0)
            {
                columns.Add(new PhysicalIndexColumnDefinition(
                    tieBreakQueries.Any(query => query.PagingSupport == QueryPagingSupport.Cursor)
                        ? envelope.IdLookupKeyColumn
                        : envelope.IdComparisonKeyColumn,
                    columns.Count,
                    PhysicalSortDirection.Ascending));
            }
            physicalIndexes.Add(new PhysicalIndexDefinition(
                logicalIndex.Identity,
                columns,
                logicalIndex.IsUnique,
                missingValueBehavior: logicalIndex.MissingValueBehavior));
        }

        return physicalIndexes;
    }

    private static bool ValidateExplicit(
        StorageUnit unit,
        StorageManifest manifest,
        PhysicalTableDefinition definition,
        List<GroundworkDiagnostic> diagnostics)
    {
        var valid = true;
        if (unit.PhysicalStorage!.ProvisioningMode == StorageUnitProvisioningMode.Dynamic &&
            definition.Form != PhysicalStorageForm.SharedDocuments)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-004",
                "Dynamic storage requires an explicit shared-documents definition.",
                $"storageUnits.{unit.Identity.Value}.physicalStorage.policy"));
            valid = false;
        }

        if (definition.Form == PhysicalStorageForm.SharedDocuments)
        {
            if (definition.SharedStorage is null ||
                !TryGetSharedDefinition(manifest, definition.SharedStorage, unit.Identity, diagnostics, out _))
                valid = false;
        }
        else if (string.IsNullOrWhiteSpace(definition.FeatureDefaultLogicalName) || definition.Envelope is null)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-005",
                "Dedicated and entity definitions require a primary logical name and canonical document envelope.",
                $"storageUnits.{unit.Identity.Value}.physicalStorage.policy"));
            valid = false;
        }

        return valid;
    }

    internal static bool TryGetSharedDefinition(
        StorageManifest manifest,
        SharedStorageBinding binding,
        StorageUnitIdentity unitIdentity,
        List<GroundworkDiagnostic> diagnostics,
        out SharedDocumentStorageDefinition? definition)
    {
        if (string.IsNullOrWhiteSpace(binding.Value))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-012",
                "Shared-storage binding identity is required.",
                $"storageUnits.{unitIdentity.Value}.physicalStorage.sharedStorage"));
            definition = null;
            return false;
        }

        var matches = manifest.SharedDocumentStorages
            .Where(x => StringComparer.Ordinal.Equals(x.Binding.Value, binding.Value))
            .ToArray();
        if (matches.Length != 1)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-012",
                matches.Length == 0
                    ? $"Shared-storage binding '{binding.Value}' is not declared by the manifest."
                    : $"Shared-storage binding '{binding.Value}' has conflicting manifest-owned definitions.",
                $"storageUnits.{unitIdentity.Value}.physicalStorage.sharedStorage"));
            definition = null;
            return false;
        }

        definition = matches[0];
        return true;
    }

    private static string FeatureDefaultColumnName(string path)
    {
        var builder = new StringBuilder(path.Length);
        foreach (var character in path)
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        return builder.ToString();
    }

    private static PortablePhysicalType ToPortableType(IndexValueKind kind) => kind switch
    {
        IndexValueKind.String or IndexValueKind.Keyword => PortablePhysicalType.String,
        IndexValueKind.Number => PortablePhysicalType.Decimal,
        IndexValueKind.Boolean => PortablePhysicalType.Boolean,
        IndexValueKind.DateTime => PortablePhysicalType.DateTime,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private sealed record ProjectedPathDemand(string Path, IndexValueKind ValueKind, int? Length, int? Precision, int? Scale);
}
