using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Resolves host logical names for every physical object a definition owns, normalizes them into
/// provider identifiers, and detects provider-namespace collisions. Split from
/// <see cref="PhysicalStorageResolver"/>; diagnostics accumulate on the caller's list.
/// </summary>
internal static class PhysicalHostNameResolver
{
    internal static IReadOnlyList<ResolvedPhysicalObjectName> ResolveHostNames(
        StorageUnit unit,
        PhysicalTableDefinition definition,
        SharedDocumentStorageDefinition? sharedStorageDefinition,
        IPhysicalNamePolicy namePolicy,
        Dictionary<string, ResolvedPhysicalObjectName> sharedPrimaryNames,
        List<GroundworkDiagnostic> diagnostics)
    {
        var defaultNames = new List<(
            PhysicalObjectKind Kind,
            string Name,
            StorageUnitIdentity NamingOwner,
            bool AllowsUnitOverride)>();
        if (definition.Form == PhysicalStorageForm.SharedDocuments)
        {
            defaultNames.Add((
                PhysicalObjectKind.PrimaryStorage,
                sharedStorageDefinition!.FeatureDefaultLogicalName,
                new StorageUnitIdentity($"shared:{sharedStorageDefinition.Binding.Value}"),
                false));
        }
        else
        {
            defaultNames.Add((
                PhysicalObjectKind.PrimaryStorage,
                definition.FeatureDefaultLogicalName!,
                unit.Identity,
                true));
        }

        var envelope = definition.Envelope ?? sharedStorageDefinition!.Envelope;
        var envelopeOwner = definition.Form == PhysicalStorageForm.SharedDocuments
            ? new StorageUnitIdentity($"shared:{sharedStorageDefinition!.Binding.Value}")
            : unit.Identity;
        var allowsEnvelopeOverride = definition.Form != PhysicalStorageForm.SharedDocuments;
        defaultNames.AddRange(EnvelopeColumnNames(envelope).Distinct(StringComparer.Ordinal).Select(name => (
            PhysicalObjectKind.EnvelopeField,
            name,
            envelopeOwner,
            allowsEnvelopeOverride)));

        if (definition.LinkedProjectionLogicalName is not null)
        {
            defaultNames.Add((
                PhysicalObjectKind.LinkedIndexStorage,
                definition.LinkedProjectionLogicalName,
                unit.Identity,
                true));

            defaultNames.AddRange(LinkedKeyColumnNames(definition.LinkedKey!).Select(name => (
                PhysicalObjectKind.LinkedIndexField,
                name,
                unit.Identity,
                true)));
        }

        var projectedFieldKind = definition.LinkedProjectionLogicalName is null
            ? PhysicalObjectKind.ProjectedField
            : PhysicalObjectKind.LinkedProjectedField;
        defaultNames.AddRange(definition.ProjectedColumns.Select(x => (
            projectedFieldKind,
            x.LogicalName,
            unit.Identity,
            true)));
        defaultNames.AddRange(definition.ProjectedColumns
            .Where(column => column.Cardinality == ProjectionCardinality.CollectionElements)
            .Select(column => (
                PhysicalObjectKind.CollectionElementStorage,
                CollectionElementNames.StorageLogicalName(column.LogicalName),
                unit.Identity,
                true)));
        defaultNames.AddRange(definition.ProjectedColumns
            .Where(column => column.Cardinality == ProjectionCardinality.CollectionElements)
            .Select(column => (
                PhysicalObjectKind.PhysicalIndex,
                CollectionElementNames.OwnerOrdinalKeyLogicalName(column.LogicalName),
                unit.Identity,
                true)));
        defaultNames.AddRange(definition.ProjectedColumns
            .Where(column => column.Cardinality == ProjectionCardinality.CollectionElements)
            .Select(column => (
                PhysicalObjectKind.PhysicalIndex,
                CollectionElementNames.MembershipKeyLogicalName(column.LogicalName),
                unit.Identity,
                true)));
        defaultNames.AddRange(definition.ProjectedColumns
            .Where(column => column.Cardinality == ProjectionCardinality.CollectionElements)
            .SelectMany(column => CollectionElementNames.Columns.Select(field => (
                PhysicalObjectKind.CollectionElementField,
                CollectionElementNames.FieldLogicalName(column.LogicalName, field),
                unit.Identity,
                true))));
        defaultNames.AddRange(definition.Indexes.Select(x => (
            PhysicalObjectKind.PhysicalIndex,
            x.LogicalName,
            unit.Identity,
            true)));

        var overrides = unit.PhysicalStorage!.NameOverrides
            .GroupBy(x => (x.ObjectKind, x.FeatureDefaultLogicalName))
            .ToDictionary(x => x.Key, x => x.ToArray());
        var result = new List<ResolvedPhysicalObjectName>();
        foreach (var item in defaultNames)
        {
            if (overrides.TryGetValue((item.Kind, item.Name), out var matching))
            {
                if (matching.Length != 1)
                {
                    diagnostics.Add(GroundworkDiagnostic.Error(
                        "GW-PHYSICAL-008",
                        $"Physical object '{item.Name}' has conflicting per-unit name overrides.",
                        $"storageUnits.{unit.Identity.Value}.physicalStorage.nameOverrides"));
                    continue;
                }

                if (!item.AllowsUnitOverride)
                {
                    diagnostics.Add(GroundworkDiagnostic.Error(
                        "GW-PHYSICAL-022",
                        $"Shared primary storage '{item.Name}' is manifest-owned and cannot be renamed by one storage unit.",
                        $"storageUnits.{unit.Identity.Value}.physicalStorage.nameOverrides"));
                }
            }

            if (item.Kind == PhysicalObjectKind.PrimaryStorage &&
                !item.AllowsUnitOverride &&
                sharedPrimaryNames.TryGetValue(sharedStorageDefinition!.Binding.Value, out var sharedPrimaryName))
            {
                result.Add(sharedPrimaryName);
                continue;
            }

            var logicalName = namePolicy.ResolveName(new PhysicalNameContext(
                item.NamingOwner,
                item.Kind,
                item.Name));
            if (item.AllowsUnitOverride && matching is not null)
                logicalName = matching[0].LogicalName;

            if (string.IsNullOrWhiteSpace(logicalName))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-009",
                    $"Physical object '{item.Name}' resolved to an empty logical name.",
                    $"storageUnits.{unit.Identity.Value}.physicalStorage.names"));
                continue;
            }

            var resolvedName = new ResolvedPhysicalObjectName(
                item.Kind,
                item.Name,
                logicalName,
                item.NamingOwner);
            result.Add(resolvedName);
            if (item.Kind == PhysicalObjectKind.PrimaryStorage && !item.AllowsUnitOverride)
                sharedPrimaryNames[sharedStorageDefinition!.Binding.Value] = resolvedName;
        }

        var knownObjects = defaultNames
            .Select(x => (x.Kind, x.Name))
            .ToHashSet();
        foreach (var nameOverride in unit.PhysicalStorage!.NameOverrides)
        {
            if (knownObjects.Contains((nameOverride.ObjectKind, nameOverride.FeatureDefaultLogicalName)))
                continue;

            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-023",
                $"Name override references unknown physical object '{nameOverride.FeatureDefaultLogicalName}'.",
                $"storageUnits.{unit.Identity.Value}.physicalStorage.nameOverrides"));
        }

        return result;
    }

    internal static IReadOnlyList<ProviderPhysicalObjectName> NormalizeNames(
        ResolvedPhysicalTableDefinition definition,
        IProviderPhysicalNameNormalizer normalizer,
        Dictionary<
            (string NamingOwner, PhysicalObjectKind ObjectKind, string LogicalName),
            ProviderPhysicalObjectName> namesByInput,
        List<GroundworkDiagnostic> diagnostics)
    {
        var result = new List<ProviderPhysicalObjectName>();
        foreach (var name in definition.Names)
        {
            var key = (name.NamingOwner.Value, name.ObjectKind, name.LogicalName);
            if (namesByInput.TryGetValue(key, out var cachedName))
            {
                result.Add(cachedName);
                continue;
            }

            var context = new ProviderPhysicalNameContext(
                name.NamingOwner,
                name.ObjectKind,
                name.LogicalName);
            var identifier = normalizer.Normalize(context);
            if (string.IsNullOrWhiteSpace(identifier))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-010",
                    $"Provider normalization produced an empty identifier for '{name.LogicalName}'.",
                    $"storageUnits.{definition.StorageUnit.Value}.physicalStorage.names"));
                continue;
            }

            var collisionScope = normalizer.GetCollisionScope(context);
            if (string.IsNullOrWhiteSpace(collisionScope))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-024",
                    $"Provider normalization produced an empty collision scope for '{name.LogicalName}'.",
                    $"storageUnits.{definition.StorageUnit.Value}.physicalStorage.names"));
                continue;
            }

            var providerName = new ProviderPhysicalObjectName(
                name.ObjectKind,
                name.FeatureDefaultLogicalName,
                name.LogicalName,
                identifier,
                collisionScope,
                name.NamingOwner);
            result.Add(providerName);
            namesByInput[key] = providerName;
        }

        return result;
    }

    internal static void AddProviderNameCollisions(
        IReadOnlyList<ProviderPhysicalTableDefinition> definitions,
        List<GroundworkDiagnostic> diagnostics)
    {
        var collisions = definitions
            .SelectMany(definition => definition.Names.Select(name => (Definition: definition, Name: name)))
            .GroupBy(
                x => (
                    Scope: x.Name.CollisionScope,
                    x.Name.Identifier),
                PhysicalNameCollisionKeyComparer.Instance)
            .Where(group => !IsExactSharedObject(group));

        foreach (var collision in collisions)
        {
            var objects = collision
                .Select(x => $"{x.Name.NamingOwner.Value}:{x.Name.ObjectKind}:{x.Name.FeatureDefaultLogicalName}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal);
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-011",
                $"Provider identifier '{collision.Key.Identifier}' is produced by multiple physical objects in the same namespace: {string.Join(", ", objects)}.",
                "physicalStorage.providerNames"));
        }
    }

    internal static string[] EnvelopeColumnNames(DocumentEnvelopeDefinition envelope) =>
    [
        envelope.IdColumn,
        envelope.DocumentKindColumn,
        envelope.StorageScopeColumn,
        envelope.VersionColumn,
        envelope.SchemaVersionColumn,
        envelope.CanonicalJsonColumn,
        envelope.IdComparisonKeyColumn,
        envelope.IdLookupKeyColumn
    ];

    internal static string[] LinkedKeyColumnNames(LinkedDocumentKeyDefinition linkedKey) =>
    [
        linkedKey.DocumentIdColumn,
        linkedKey.DocumentKindColumn,
        linkedKey.StorageScopeColumn,
        linkedKey.DocumentIdComparisonKeyColumn,
        linkedKey.DocumentIdLookupKeyColumn
    ];

    internal static string[] EnvelopeRelationshipColumnNames(DocumentEnvelopeDefinition envelope) =>
    [
        envelope.IdColumn,
        envelope.IdComparisonKeyColumn,
        envelope.IdLookupKeyColumn,
        envelope.DocumentKindColumn,
        envelope.StorageScopeColumn
    ];

    private static bool IsExactSharedObject(
        IEnumerable<(ProviderPhysicalTableDefinition Definition, ProviderPhysicalObjectName Name)> group)
    {
        var entries = group.ToArray();
        if (entries.Length < 2)
            return true;

        var first = entries[0];
        var binding = first.Definition.Resolved.SharedStorageDefinition?.Binding;
        return binding is not null &&
               first.Name.ObjectKind is PhysicalObjectKind.PrimaryStorage or PhysicalObjectKind.EnvelopeField &&
               entries.All(entry =>
                   entry.Definition.Definition.Form == PhysicalStorageForm.SharedDocuments &&
                   entry.Definition.Resolved.SharedStorageDefinition?.Binding == binding &&
                   entry.Name.ObjectKind == first.Name.ObjectKind &&
                   entry.Name.NamingOwner == first.Name.NamingOwner &&
                   entry.Name.FeatureDefaultLogicalName == first.Name.FeatureDefaultLogicalName &&
                   entry.Name.LogicalName == first.Name.LogicalName);
    }

    private sealed class PhysicalNameCollisionKeyComparer : IEqualityComparer<(string Scope, string Identifier)>
    {
        public static PhysicalNameCollisionKeyComparer Instance { get; } = new();

        public bool Equals(
            (string Scope, string Identifier) x,
            (string Scope, string Identifier) y) =>
            StringComparer.Ordinal.Equals(x.Scope, y.Scope) &&
            StringComparer.Ordinal.Equals(x.Identifier, y.Identifier);

        public int GetHashCode((string Scope, string Identifier) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Scope),
                StringComparer.Ordinal.GetHashCode(obj.Identifier));
    }
}
