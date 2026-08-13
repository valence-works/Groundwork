using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;
using Groundwork.Core.Validation;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Validates a resolved physical table definition against its storage unit's declarations, and
/// owns the sort/index algebra that certifies physical indexes against scale-bearing queries.
/// Split from <see cref="PhysicalStorageResolver"/>; diagnostics accumulate on the caller's list.
/// </summary>
internal static class PhysicalDefinitionValidator
{
    /// <summary>
    /// Requires the unit's declarations to have passed
    /// <see cref="PhysicalStorageDeclarationValidator.ValidateDeclarations"/> first: the
    /// scale-bearing certification below relies on every referenced logical index being declared
    /// exactly once.
    /// </summary>
    internal static bool ValidateDefinition(
        StorageUnit unit,
        StorageManifest manifest,
        PhysicalTableDefinition definition,
        IReadOnlyList<ScaleBearingPathDemand> demand,
        List<GroundworkDiagnostic> diagnostics)
    {
        var valid = true;
        var target = $"storageUnits.{unit.Identity.Value}.physicalStorage.definition";
        if (definition.SchemaVersion <= 0)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-013",
                "Physical table schema version must be greater than zero.",
                $"{target}.schemaVersion"));
            valid = false;
        }

        SharedDocumentStorageDefinition? sharedDefinition = null;
        if (definition.Form == PhysicalStorageForm.SharedDocuments)
        {
            if (definition.SharedStorage is null ||
                !PhysicalStorageResolver.TryGetSharedDefinition(manifest, definition.SharedStorage, unit.Identity, diagnostics, out sharedDefinition))
            {
                valid = false;
            }
            else if (sharedDefinition!.SchemaVersion <= 0 ||
                     string.IsNullOrWhiteSpace(sharedDefinition.FeatureDefaultLogicalName) ||
                     !HasCanonicalEnvelope(sharedDefinition.Envelope))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-019",
                    "Shared document storage requires a positive schema version and complete canonical document envelope.",
                    $"{target}.sharedStorage"));
                valid = false;
            }
        }
        else if (definition.Envelope is null || !HasCanonicalEnvelope(definition.Envelope))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-019",
                "Dedicated and entity storage require a complete canonical document envelope.",
                $"{target}.envelope"));
            valid = false;
        }

        if (definition.Form != PhysicalStorageForm.SharedDocuments &&
            string.IsNullOrWhiteSpace(definition.FeatureDefaultLogicalName))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-013",
                "Dedicated and entity storage require a non-empty feature-default logical name.",
                $"{target}.featureDefaultLogicalName"));
            valid = false;
        }

        var hasLinkedName = !string.IsNullOrWhiteSpace(definition.LinkedProjectionLogicalName);
        var hasLinkedStructures = definition.ProjectedColumns.Count != 0 ||
                                  definition.Indexes.Any(index =>
                                      PhysicalIndexStorageTargetResolver.Resolve(definition, index) == PhysicalIndexStorageTarget.LinkedIndexStorage);
        var hasLinkedKey = definition.LinkedKey is not null;
        if ((definition.Form == PhysicalStorageForm.SharedDocuments && hasLinkedStructures != hasLinkedName) ||
            (definition.Form == PhysicalStorageForm.DedicatedDocumentTable &&
             ((definition.ProjectedColumns.Count != 0 && !hasLinkedName) || (hasLinkedName && !hasLinkedStructures))) ||
            (definition.Form == PhysicalStorageForm.PhysicalEntityTable && hasLinkedName) ||
            hasLinkedName != hasLinkedKey ||
            definition.Indexes.Any(index => !PhysicalIndexStorageTargetResolver.IsValid(definition, index)))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-013",
                "Linked projected/index structures require exactly one auxiliary table logical name and entity projections remain in-primary.",
                $"{target}.linkedProjectionLogicalName"));
            valid = false;
        }

        if (definition.LinkedKey is not null)
        {
            var linkedKeyColumns = PhysicalHostNameResolver.LinkedKeyColumnNames(definition.LinkedKey);
            if (linkedKeyColumns.Any(string.IsNullOrWhiteSpace) ||
                linkedKeyColumns.Distinct(StringComparer.Ordinal).Count() != linkedKeyColumns.Length)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-013",
                    "Linked document relationship fields must be non-empty and distinct.",
                    $"{target}.linkedKey"));
                valid = false;
            }
        }

        if (definition.Form == PhysicalStorageForm.PhysicalEntityTable && definition.ProjectedColumns.Count == 0)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-013",
                "Physical entity tables require at least one projected column.",
                $"{target}.projectedColumns"));
            valid = false;
        }

        var envelope = definition.Envelope ?? sharedDefinition?.Envelope;
        var envelopeColumns = envelope is null
            ? Array.Empty<string>()
            : PhysicalHostNameResolver.EnvelopeColumnNames(envelope);
        var unavailableProjectedNames = definition.LinkedKey is null || envelope is null
            ? envelopeColumns
            : PhysicalHostNameResolver.EnvelopeRelationshipColumnNames(envelope)
                .Concat(PhysicalHostNameResolver.LinkedKeyColumnNames(definition.LinkedKey))
                .ToArray();
        var duplicateColumnNames = definition.ProjectedColumns
            .GroupBy(x => x.LogicalName, StringComparer.Ordinal)
            .Any(x => x.Count() > 1) ||
            definition.ProjectedColumns.Any(column =>
                unavailableProjectedNames.Contains(column.LogicalName, StringComparer.Ordinal));
        var duplicatePaths = definition.ProjectedColumns
            .GroupBy(x => x.Path, StringComparer.Ordinal)
            .Any(x => x.Count() > 1);
        if (duplicateColumnNames || duplicatePaths)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-016",
                "Projected column logical names and serialized paths must be unique within a definition.",
                $"{target}.projectedColumns"));
            valid = false;
        }

        foreach (var column in definition.ProjectedColumns)
        {
            if (string.IsNullOrWhiteSpace(column.LogicalName) ||
                string.IsNullOrWhiteSpace(column.Path) ||
                column.Length is <= 0 ||
                column.Precision is <= 0 ||
                column.Scale is < 0 ||
                (column.Scale is not null && column.Precision is null) ||
                (column.Scale is not null && column.Precision is not null && column.Scale > column.Precision) ||
                (column.Type == PortablePhysicalType.Decimal &&
                 (column.Precision is null or > 28 || column.Scale is null)) ||
                (column.Type != PortablePhysicalType.Decimal && column.Scale is not null) ||
                !Enum.IsDefined(column.Cardinality) ||
                (column.Cardinality == ProjectionCardinality.Scalar && column.MaxCollectionElements is not null) ||
                (column.Cardinality == ProjectionCardinality.CollectionElements &&
                 (column.MaxCollectionElements is null or <= 0 ||
                  column.DefaultValue is not null ||
                  !column.IsNullable ||
                  !CanonicalCollectionElementProjection.IsSupportedPath(column.Path) ||
                  !CanonicalCollectionElementProjection.SupportsElementType(column.Type) ||
                  (column.Type != PortablePhysicalType.String && column.Length is not null) ||
                  (column.Type != PortablePhysicalType.String && column.Collation is not null))))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-018",
                    $"Projected column '{column.LogicalName}' has invalid portable metadata.",
                    $"{target}.projectedColumns"));
                valid = false;
            }
        }

        if (!duplicatePaths)
        {
            var projectedByPath = definition.ProjectedColumns.ToDictionary(
                column => column.Path,
                StringComparer.Ordinal);
            foreach (var logicalIndex in unit.PhysicalStorage!.LogicalIndexes)
            {
                var physicalIndex = definition.Indexes.FirstOrDefault(index =>
                    index.LogicalName == logicalIndex.Identity);
                if (physicalIndex is null)
                    continue;
                foreach (var field in logicalIndex.Fields)
                {
                    if (!projectedByPath.TryGetValue(field.Path, out var projection) ||
                        physicalIndex.Columns.All(column => column.ColumnLogicalName != projection.LogicalName) ||
                        PortableQueryOperationCompatibility.Supports(logicalIndex.GetValueKind(field), projection.Type))
                    {
                        continue;
                    }

                    diagnostics.Add(GroundworkDiagnostic.Error(
                        "GW-PHYSICAL-031",
                        $"Logical index '{logicalIndex.Identity}' value kind '{logicalIndex.GetValueKind(field)}' cannot use " +
                        $"projected path '{field.Path}' with physical type '{projection.Type}' without changing query semantics.",
                        $"{target}.projectedColumns.{projection.LogicalName}"));
                    valid = false;
                }
            }

            var residualKinds = unit.PhysicalStorage!.BoundedQueries
                .Where(query => query.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing)
                .SelectMany(query => query.ResidualPredicateFields)
                .GroupBy(field => field.Path, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(field => field.ValueKind).Distinct().ToArray(),
                    StringComparer.Ordinal);
            foreach (var (path, kinds) in residualKinds)
            {
                if (kinds.Length != 1 ||
                    !projectedByPath.TryGetValue(path, out var projection) ||
                    PortableQueryOperationCompatibility.Supports(kinds[0], projection.Type))
                {
                    continue;
                }

                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-036",
                    $"Residual predicate path '{path}' value kind '{kinds[0]}' cannot use projected physical type " +
                    $"'{projection.Type}' without changing query semantics.",
                    $"{target}.projectedColumns.{projection.LogicalName}"));
                valid = false;
            }
        }

        var envelopeColumnSet = envelopeColumns.ToHashSet(StringComparer.Ordinal);
        var primaryAvailableColumns = definition.Form == PhysicalStorageForm.PhysicalEntityTable
            ? envelopeColumns.Concat(definition.ProjectedColumns.Select(column => column.LogicalName)).ToHashSet(StringComparer.Ordinal)
            : envelopeColumnSet;
        var linkedAvailableColumns = envelope is null
            ? definition.ProjectedColumns.Select(column => column.LogicalName).ToHashSet(StringComparer.Ordinal)
            : PhysicalHostNameResolver.EnvelopeRelationshipColumnNames(envelope)
            .Concat(definition.ProjectedColumns.Select(column => column.LogicalName))
            .ToHashSet(StringComparer.Ordinal);
        if (definition.Indexes.GroupBy(x => x.LogicalName, StringComparer.Ordinal).Any(x => x.Count() > 1))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-016",
                "Physical index logical names must be unique within a definition.",
                $"{target}.indexes"));
            valid = false;
        }

        foreach (var index in definition.Indexes)
        {
            if (string.IsNullOrWhiteSpace(index.LogicalName) ||
                index.SchemaVersion <= 0 ||
                index.Columns.Count == 0)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-013",
                    $"Physical index '{index.LogicalName}' requires a name, positive schema version, and at least one column.",
                    $"{target}.indexes"));
                valid = false;
                continue;
            }

            var expectedOrder = Enumerable.Range(0, index.Columns.Count);
            if (!index.Columns.Select(x => x.Order).Order().SequenceEqual(expectedOrder))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-015",
                    $"Physical index '{index.LogicalName}' column order must be unique and contiguous from zero.",
                    $"{target}.indexes.{index.LogicalName}.columns"));
                valid = false;
            }

            if (unit.Tenancy.Kind == TenancyKind.Scoped &&
                envelope is not null &&
                index.Columns.All(x => x.ColumnLogicalName != envelope.StorageScopeColumn))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-026",
                    $"Scoped index '{index.LogicalName}' must include envelope scope column '{envelope.StorageScopeColumn}'.",
                    $"{target}.indexes.{index.LogicalName}.columns"));
                valid = false;
            }

            var availableColumns = PhysicalIndexStorageTargetResolver.Resolve(definition, index) == PhysicalIndexStorageTarget.LinkedIndexStorage
                ? linkedAvailableColumns
                : primaryAvailableColumns;
            foreach (var indexColumn in index.Columns)
            {
                if (!availableColumns.Contains(indexColumn.ColumnLogicalName))
                {
                    diagnostics.Add(GroundworkDiagnostic.Error(
                        "GW-PHYSICAL-014",
                        $"Physical index '{index.LogicalName}' references unknown column '{indexColumn.ColumnLogicalName}'.",
                        $"{target}.indexes.{index.LogicalName}.columns"));
                    valid = false;
                }
            }
        }

        foreach (var query in unit.PhysicalStorage!.BoundedQueries
                     .Where(x => x.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing))
        {
            var indexIdentity = query.IndexIdentity;
            var logicalIndex =
                unit.PhysicalStorage.LogicalIndexes.SingleOrDefault(x => x.Identity == indexIdentity) ??
                throw new InvalidOperationException(
                    $"Scale-bearing query '{query.Identity}' references logical index '{indexIdentity}' that is not " +
                    "declared exactly once; ValidateDefinition relies on declaration validation having certified " +
                    "every scale-bearing query's index reference before it runs.");
            if (PhysicalQueryIdentityDemand.Resolve(logicalIndex, query) ==
                PhysicalQueryIdentityEvidenceDemand.Mixed)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-035",
                    $"Scale-bearing query '{query.Identity}' has mixed exact and ordered document-identity demand; " +
                    "no single explicit physical index order can certify both evidence shapes.",
                    $"{target}.indexes"));
                valid = false;
                continue;
            }
            if (logicalIndex.Fields.Count == 1 &&
                query.Operations.Count != 0 &&
                query.Operations.All(operation => operation is
                    PortableQueryOperation.CollectionContains or
                    PortableQueryOperation.CollectionContainsAll) &&
                definition.ProjectedColumns.Any(column =>
                    column.Path == logicalIndex.Fields[0].Path &&
                    column.Cardinality == ProjectionCardinality.CollectionElements))
            {
                continue;
            }
            var sharedIdentityTieBreakPaging = ResolveProviderAppliedIdentityTieBreakPaging(
                logicalIndex,
                unit.PhysicalStorage.BoundedQueries.Where(candidate =>
                    candidate.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing &&
                    candidate.IndexIdentity == indexIdentity));
            var expectedColumns = ResolveExpectedIndexColumns(
                unit,
                logicalIndex,
                query,
                definition,
                sharedDefinition,
                sharedIdentityTieBreakPaging);
            var physicalIndex = definition.Indexes.SingleOrDefault(x => x.LogicalName == indexIdentity);
            if (expectedColumns is not null &&
                physicalIndex is not null &&
                physicalIndex.IsUnique == logicalIndex.IsUnique &&
                physicalIndex.MissingValueBehavior == logicalIndex.MissingValueBehavior &&
                PhysicalIndexFulfills(
                    physicalIndex.Columns,
                    expectedColumns,
                    RequiresStorageScope(unit, logicalIndex),
                    sharedIdentityTieBreakPaging is not null))
            {
                continue;
            }

            // Naming the disagreement matters most where the physical index exists and differs in one
            // declared property: the generic wording sends the reader looking at columns and ordering,
            // which is the one thing that is right.
            var mismatch = physicalIndex is null
                ? string.Empty
                : physicalIndex.IsUnique != logicalIndex.IsUnique
                    ? $" The physical index declares IsUnique {physicalIndex.IsUnique} where the logical index declares {logicalIndex.IsUnique}; both have to agree."
                    : physicalIndex.MissingValueBehavior != logicalIndex.MissingValueBehavior
                        ? $" The physical index declares MissingValueBehavior.{physicalIndex.MissingValueBehavior} where the logical index declares {logicalIndex.MissingValueBehavior}; both have to agree, so state it on each or let each take the default."
                        : string.Empty;
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-025",
                $"Scale-bearing logical index '{indexIdentity}' requires a matching ordered physical index.{mismatch}",
                $"{target}.indexes"));
            valid = false;
        }

        var projectedPaths = definition.ProjectedColumns.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        var unmetDemand = demand
            .SelectMany(x => new[] { x.Path }.Concat(
                x.ResidualPredicateFields.Select(residual => residual.Path)))
            .Where(path => !PhysicalDocumentFieldPaths.IsEnvelope(path) && !projectedPaths.Contains(path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (unmetDemand.Length != 0)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-017",
                $"Scale-bearing paths must be projected by the selected physical definition: {string.Join(", ", unmetDemand)}.",
                $"{target}.projectedColumns"));
            valid = false;
        }

        return valid;
    }

    private static bool HasCanonicalEnvelope(DocumentEnvelopeDefinition envelope)
    {
        var columns = PhysicalHostNameResolver.EnvelopeColumnNames(envelope);
        return columns.All(column => !string.IsNullOrWhiteSpace(column)) &&
               columns.Distinct(StringComparer.Ordinal).Count() == columns.Length;
    }

    internal static bool RequiresStorageScope(StorageUnit unit, LogicalIndexDeclaration index) =>
        unit.Tenancy.Kind == TenancyKind.Scoped &&
        index.Fields.All(field => field.Path != PhysicalDocumentFieldPaths.StorageScope);

    internal static IReadOnlyList<PhysicalSortDirection> ResolveSortDirections(
        BoundedQueryDeclaration query,
        LogicalIndexDeclaration index)
    {
        if (query.SortFields.Count != 0)
        {
            var explicitDirections = query.SortFields.ToDictionary(x => x.Path, x => x.Direction, StringComparer.Ordinal);
            return index.Fields
                .Select(field => explicitDirections.GetValueOrDefault(field.Path, PhysicalSortDirection.Ascending))
                .ToArray();
        }

        var direction = query.SortSupport == QuerySortSupport.Descending
            ? PhysicalSortDirection.Descending
            : PhysicalSortDirection.Ascending;
        return Enumerable.Repeat(direction, index.Fields.Count).ToArray();
    }

    internal static IReadOnlyList<PhysicalSortDirection> ResolveCanonicalSortDirections(
        IEnumerable<BoundedQueryDeclaration> queries,
        LogicalIndexDeclaration index) =>
        queries
            .OrderBy(query => query.Identity, StringComparer.Ordinal)
            .Select(query => ResolveSortDirections(query, index))
            .First();

    internal static IReadOnlyList<PhysicalSortDirection> CanonicalizeSortDirections(
        IReadOnlyList<PhysicalSortDirection> directions)
    {
        var forward = directions.ToArray();
        var reverse = directions.Select(PhysicalSortDirections.Opposite).ToArray();
        return StringComparer.Ordinal.Compare(DirectionShape(forward), DirectionShape(reverse)) <= 0
            ? forward
            : reverse;
    }

    internal static string DirectionShape(IEnumerable<PhysicalSortDirection> directions) =>
        string.Join(",", directions.Select(x => (int)x));

    private static bool PhysicalIndexFulfills(
        IReadOnlyList<PhysicalIndexColumnDefinition> actual,
        IReadOnlyList<PhysicalIndexColumnDefinition> expected,
        bool hasScopePrefix,
        bool requiresProviderAppliedIdentityTieBreak)
    {
        if (actual.Count != expected.Count ||
            !actual.Select(x => (x.ColumnLogicalName, x.Order))
                .SequenceEqual(expected.Select(x => (x.ColumnLogicalName, x.Order))))
        {
            return false;
        }

        var offset = hasScopePrefix ? 1 : 0;
        var actualDirections = actual.Skip(offset).Select(x => x.Direction).ToArray();
        var expectedDirections = expected.Skip(offset).Select(x => x.Direction).ToArray();
        return actualDirections.SequenceEqual(expectedDirections) ||
               (!requiresProviderAppliedIdentityTieBreak &&
                actualDirections.SequenceEqual(expectedDirections.Select(PhysicalSortDirections.Opposite)));
    }

    private static IReadOnlyList<PhysicalIndexColumnDefinition>? ResolveExpectedIndexColumns(
        StorageUnit unit,
        LogicalIndexDeclaration logicalIndex,
        BoundedQueryDeclaration query,
        PhysicalTableDefinition definition,
        SharedDocumentStorageDefinition? sharedDefinition,
        QueryPagingSupport? identityTieBreakPaging)
    {
        var envelope = definition.Envelope ?? sharedDefinition?.Envelope;
        if (envelope is null)
            return null;

        var projectedColumns = definition.ProjectedColumns.ToDictionary(
            x => x.Path,
            x => x.LogicalName,
            StringComparer.Ordinal);
        var result = new List<PhysicalIndexColumnDefinition>();
        if (RequiresStorageScope(unit, logicalIndex))
            result.Add(new PhysicalIndexColumnDefinition(envelope.StorageScopeColumn, result.Count));

        var sortDirections = ResolveSortDirections(query, logicalIndex);
        var identityDemand = PhysicalQueryIdentityDemand.Resolve(logicalIndex, query);
        foreach (var (field, fieldOrder) in logicalIndex.Fields.Select((field, order) => (field, order)))
        {
            if (field.Path == PhysicalDocumentFieldPaths.Id)
            {
                IReadOnlyList<string> identityColumns = identityDemand switch
                {
                    PhysicalQueryIdentityEvidenceDemand.Exact =>
                    [envelope.IdLookupKeyColumn, envelope.IdComparisonKeyColumn],
                    PhysicalQueryIdentityEvidenceDemand.Ordered => [envelope.IdComparisonKeyColumn],
                    PhysicalQueryIdentityEvidenceDemand.None => [envelope.IdComparisonKeyColumn],
                    PhysicalQueryIdentityEvidenceDemand.Mixed => [],
                    _ => throw new ArgumentOutOfRangeException(nameof(identityDemand), identityDemand, null)
                };
                result.AddRange(identityColumns.Select(logicalName => new PhysicalIndexColumnDefinition(
                    logicalName,
                    result.Count,
                    sortDirections[fieldOrder])));
                continue;
            }

            string logicalName;
            if (PhysicalDocumentFieldPaths.IsEnvelope(field.Path))
            {
                logicalName = EnvelopeColumnName(envelope, field.Path);
            }
            else if (!projectedColumns.TryGetValue(field.Path, out logicalName!))
            {
                return null;
            }

            result.Add(new PhysicalIndexColumnDefinition(
                logicalName,
                result.Count,
                sortDirections[fieldOrder]));
        }
        if (identityTieBreakPaging is not null)
        {
            result.Add(new PhysicalIndexColumnDefinition(
                identityTieBreakPaging == QueryPagingSupport.Cursor
                    ? envelope.IdLookupKeyColumn
                    : envelope.IdComparisonKeyColumn,
                result.Count,
                PhysicalSortDirection.Ascending));
        }

        return result;
    }

    private static QueryPagingSupport? ResolveProviderAppliedIdentityTieBreakPaging(
        LogicalIndexDeclaration logicalIndex,
        IEnumerable<BoundedQueryDeclaration> queries)
    {
        var required = queries
            .Where(query => PhysicalQueryOrderRequirements.RequiresProviderAppliedIdentityTieBreak(
                logicalIndex,
                query))
            .Select(query => query.PagingSupport)
            .Distinct()
            .ToArray();
        return required.Length == 1 ? required[0] : null;
    }

    internal static string EnvelopeColumnName(DocumentEnvelopeDefinition envelope, string path) => path switch
    {
        PhysicalDocumentFieldPaths.Id => envelope.IdColumn,
        PhysicalDocumentFieldPaths.DocumentKind => envelope.DocumentKindColumn,
        PhysicalDocumentFieldPaths.StorageScope => envelope.StorageScopeColumn,
        PhysicalDocumentFieldPaths.Version => envelope.VersionColumn,
        PhysicalDocumentFieldPaths.SchemaVersion => envelope.SchemaVersionColumn,
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, null)
    };
}
