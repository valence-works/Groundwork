using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.Scoping;
using Groundwork.Core.Text;
using Groundwork.Core.Transactions;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Groundwork.Tests;

public sealed partial class PhysicalQueryPlanCompilerTests
{
    private static PlanningFixture CreateFixture(
        PhysicalStorageForm form,
        BoundedQueryExecutionClass executionClass) =>
        CreateFixture(form, Query(executionClass));

    private static PlanningFixture CreateAssignmentFixture(
        bool includeTarget = true,
        ProjectionCardinality targetCardinality = ProjectionCardinality.Scalar,
        string targetValue = "revoked",
        string targetPath = "status",
        PortablePhysicalType targetType = PortablePhysicalType.String)
    {
        var logicalIndex = StimulusTypeIndex();
        var query = Query(BoundedQueryExecutionClass.ScaleBearing);
        var projections = new List<ProjectedColumnDefinition>
        {
            new("stimulusType", "stimulusType", PortablePhysicalType.String)
        };
        if (includeTarget)
        {
            projections.Add(new ProjectedColumnDefinition(
                "assignmentTarget",
                targetPath,
                targetType,
                IsNullable: targetCardinality == ProjectionCardinality.CollectionElements,
                Cardinality: targetCardinality,
                MaxCollectionElements: targetCardinality == ProjectionCardinality.CollectionElements ? 16 : null));
        }
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "workflow_trigger_bindings",
            projections,
            indexes:
            [
                new PhysicalIndexDefinition(
                    logicalIndex.Identity,
                    ScopedStimulusTypeIndexColumns(query))
            ]);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logicalIndex],
            [query],
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "assign-status",
                    query.Identity,
                    BoundedMutationAction.Assign(targetPath, targetValue))
            ]);
        return Resolve(storage, null);
    }

    private static PlanningFixture CreateFixture(
        PhysicalStorageForm form,
        BoundedQueryDeclaration query)
    {
        var logicalIndex = StimulusTypeIndex();
        if (form == PhysicalStorageForm.PhysicalEntityTable)
            return CreateEntityFixture(logicalIndex, query);

        var binding = new SharedStorageBinding("runtime-documents");
        PhysicalTableDefinition definition = form switch
        {
            PhysicalStorageForm.SharedDocuments => PhysicalTableDefinition.SharedDocuments(
                binding,
                [new ProjectedColumnDefinition("stimulusType", "stimulusType", PortablePhysicalType.String)],
                [
                    new PhysicalIndexDefinition(
                        logicalIndex.Identity,
                        ScopedStimulusTypeIndexColumns(query))
                ],
                linkedProjectionLogicalName: "workflow_trigger_binding_index"),
            PhysicalStorageForm.DedicatedDocumentTable when query.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing =>
                PhysicalTableDefinition.DedicatedDocumentTable(
                    "workflow_trigger_bindings",
                    indexes:
                    [
                        new PhysicalIndexDefinition(
                            logicalIndex.Identity,
                            ScopedStimulusTypeIndexColumns(query))
                    ],
                    linkedProjectedColumns:
                    [new ProjectedColumnDefinition("stimulusType", "stimulusType", PortablePhysicalType.String)],
                    linkedProjectionLogicalName: "workflow_trigger_binding_index"),
            PhysicalStorageForm.DedicatedDocumentTable => PhysicalTableDefinition.DedicatedDocumentTable(
                "workflow_trigger_bindings"),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logicalIndex],
            [query]);
        return Resolve(storage, form == PhysicalStorageForm.SharedDocuments ? binding : null);
    }

    private static MissingValueBehavior MissingValues(bool isUnique) =>
        isUnique ? MissingValueBehavior.Excluded : MissingValueBehavior.IncludedAsNull;

    private static PlanningFixture CreateOffsetTieBreakFixture(
        bool includeTieBreak,
        PhysicalSortDirection tieBreakDirection = PhysicalSortDirection.Ascending,
        PortableQueryOperation predicateOperation = PortableQueryOperation.Equal,
        bool isUnique = false)
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-category-rank",
            [new IndexField("category"), new IndexField("rank", IndexValueKind.Number)],
            IndexValueKind.Keyword,
            isUnique,
            // A unique index over nullable columns has to exclude the missing values, or whether two
            // rows without a value collide would differ by provider.
            MissingValues(isUnique));
        var columns = new List<PhysicalIndexColumnDefinition>
        {
            new("storage_scope", 0),
            new("category", 1, PhysicalSortDirection.Ascending),
            new("rank", 2, PhysicalSortDirection.Ascending)
        };
        if (!isUnique)
            columns.Add(new("id_comparison_key", 3, PhysicalSortDirection.Ascending));
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "ordered_documents",
            [
                new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String),
                new ProjectedColumnDefinition("rank", "rank", PortablePhysicalType.Decimal, Precision: 18, Scale: 4)
            ],
            indexes: [new PhysicalIndexDefinition(
                logicalIndex.Identity, columns, isUnique, missingValueBehavior: MissingValues(isUnique))]);
        BoundedQueryDeclaration Query(PortableQueryOperation operation) => new(
            "list-by-category-rank",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { operation },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields: [new BoundedQuerySortField("rank", PhysicalSortDirection.Ascending)],
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "category",
                    new HashSet<PortableQueryOperation> { operation })
            ]);
        var admittedQuery = Query(PortableQueryOperation.Equal);
        var query = predicateOperation == PortableQueryOperation.Equal
            ? admittedQuery
            : Query(predicateOperation);
        var physicalStorage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logicalIndex],
            [admittedQuery]);
        var fixture = Resolve(physicalStorage, null);
        var routeColumns = fixture.Route.Indexes.Single().Columns
            .Where(column => includeTieBreak ||
                column.Column.LogicalName != "id_comparison_key")
            .Select(column => column.Column.LogicalName == "id_comparison_key"
                ? column with { Direction = tieBreakDirection }
                : column)
            .ToArray();
        return new PlanningFixture(
            ReplacePhysicalIndexColumns(fixture.Route, routeColumns),
            query == admittedQuery
                ? fixture.Storage
                : new StorageUnitPhysicalStorage(
                    fixture.Storage.ProvisioningMode,
                    fixture.Storage.Policy,
                    fixture.Storage.LogicalIndexes,
                    [query]));
    }

    private static ExecutableStorageRoute ReplacePhysicalIndexColumns(
        ExecutableStorageRoute route,
        IReadOnlyList<ExecutableIndexColumnRoute> columns)
    {
        var index = Assert.Single(route.Indexes);
        var definition = new PhysicalIndexDefinition(
            index.Definition.LogicalName,
            columns.Select(column => new PhysicalIndexColumnDefinition(
                column.Column.LogicalName,
                column.Order,
                column.Direction)).ToArray(),
            index.Definition.IsUnique,
            index.Definition.SchemaVersion,
            index.Definition.Evolution,
            index.Definition.Target,
            index.Definition.MissingValueBehavior);
        var replacement = new ExecutablePhysicalIndexRoute(
            definition,
            index.Name,
            index.Target,
            columns);
        return new ExecutableStorageRoute(
            route.StorageUnit,
            route.ProvisioningMode,
            route.Form,
            route.SharedStorage,
            route.ScopePolicy,
            route.PrimaryStorage,
            route.LinkedIndexStorage,
            route.Envelope,
            route.LinkedRelationship,
            route.Discriminator,
            route.ScopeKey,
            route.PrimaryKey,
            route.AuxiliaryKey,
            route.ProjectedColumns,
            route.CollectionElementStorages,
            route.Indexes.Select(candidate => candidate.Identity == index.Identity ? replacement : candidate).ToArray(),
            route.MaintenanceRoutes,
            route.CandidateQueryPaths,
            route.CapabilityRequirements,
            route.DefinitionFingerprint,
            route.Fingerprint);
    }

    private static PlanningFixture CreateIntrinsicMutationFixture(
        bool linked,
        BoundedMutationAction action,
        StringIdentityCasePolicy identityCasePolicy = StringIdentityCasePolicy.Ordinal)
    {
        var path = linked ? "id" : "schemaVersion";
        var index = new LogicalIndexDeclaration(
            $"by-{path}",
            [new IndexField(path)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var query = new BoundedQueryDeclaration(
            $"list-by-{path}",
            index.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing);
        var physicalIndex = new PhysicalIndexDefinition(
            index.Identity,
            linked
                ?
                [
                    new PhysicalIndexColumnDefinition("storage_scope", 0),
                    new PhysicalIndexColumnDefinition("id_lookup_key", 1),
                    new PhysicalIndexColumnDefinition("id_comparison_key", 2)
                ]
                :
                [
                    new PhysicalIndexColumnDefinition("storage_scope", 0),
                    new PhysicalIndexColumnDefinition("schema_version", 1)
                ],
            target: linked
                ? PhysicalIndexStorageTarget.LinkedIndexStorage
                : PhysicalIndexStorageTarget.PrimaryStorage);
        var definition = PhysicalTableDefinition.DedicatedDocumentTable(
            "intrinsic_documents",
            indexes: [physicalIndex],
            linkedProjectedColumns: linked
                ? [new ProjectedColumnDefinition("unused", "unused", PortablePhysicalType.String)]
                : null,
            linkedProjectionLogicalName: linked ? "intrinsic_index" : null);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [index],
            [query],
            boundedMutations: [new BoundedMutationDeclaration("mutate-intrinsic", query.Identity, action)]);
        return Resolve(storage, null, identityCasePolicy: identityCasePolicy);
    }

    private static PlanningFixture CreateIdentityQueryFixture(
        IReadOnlySet<PortableQueryOperation> operations,
        QuerySortSupport sortSupport = QuerySortSupport.None,
        IReadOnlyList<BoundedQuerySortField>? sortFields = null,
        StringIdentityCasePolicy identityCasePolicy = StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase,
        PhysicalQuerySourceKind source = PhysicalQuerySourceKind.PrimaryEnvelope,
        IdentityIndexLayout? indexLayout = null)
    {
        var index = new LogicalIndexDeclaration(
            "by-id",
            [new IndexField(PhysicalDocumentFieldPaths.Id)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var query = new BoundedQueryDeclaration(
            "list-by-id",
            index.Identity,
            operations,
            sortSupport,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.Ordinary,
            sortFields: sortFields);
        var layout = indexLayout ?? (operations.All(IsExactIdentityOperation)
            ? IdentityIndexLayout.Exact
            : IdentityIndexLayout.Ordered);
        var identityColumns = layout switch
        {
            IdentityIndexLayout.Exact => new[] { "id_lookup_key", "id_comparison_key" },
            IdentityIndexLayout.Ordered => ["id_comparison_key"],
            IdentityIndexLayout.Original => ["id"],
            _ => throw new ArgumentOutOfRangeException(nameof(indexLayout), indexLayout, null)
        };
        var physicalColumns = new List<PhysicalIndexColumnDefinition>
        {
            new("storage_scope", 0)
        };
        physicalColumns.AddRange(identityColumns.Select((column, order) =>
            new PhysicalIndexColumnDefinition(column, order + 1)));
        var linked = source == PhysicalQuerySourceKind.LinkedIndex;
        var definition = PhysicalTableDefinition.DedicatedDocumentTable(
            "identity_documents",
            indexes:
            [
                new PhysicalIndexDefinition(
                    index.Identity,
                    physicalColumns,
                    target: linked
                        ? PhysicalIndexStorageTarget.LinkedIndexStorage
                        : PhysicalIndexStorageTarget.PrimaryStorage)
            ],
            linkedProjectedColumns: linked
                ? [new ProjectedColumnDefinition("unused", "unused", PortablePhysicalType.String)]
                : null,
            linkedProjectionLogicalName: linked ? "identity_index" : null);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [index],
            [query]);
        return Resolve(storage, null, identityCasePolicy: identityCasePolicy);
    }

    private static bool IsExactIdentityOperation(PortableQueryOperation operation) => operation is
        PortableQueryOperation.Equal or
        PortableQueryOperation.In or
        PortableQueryOperation.NotEqual;

    private enum IdentityIndexLayout
    {
        Exact,
        Ordered,
        Original
    }

    private static PlanningFixture CreateEntityFixture(
        LogicalIndexDeclaration logicalIndex,
        BoundedQueryDeclaration query)
    {
        var projections = logicalIndex.Fields
            .Select(field => new ProjectedColumnDefinition(
                field.Path,
                field.Path,
                ToPortableType(logicalIndex.GetValueKind(field))))
            .Concat(query.ResidualPredicateFields.Select(field => new ProjectedColumnDefinition(
                field.Path,
                field.Path,
                ToPortableType(field.ValueKind))))
            .GroupBy(column => column.Path, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var columns = new List<PhysicalIndexColumnDefinition>
        {
            new("storage_scope", 0)
        };
        columns.AddRange(logicalIndex.Fields.Select((field, index) =>
            new PhysicalIndexColumnDefinition(
                field.Path,
                index + 1,
                query.SortFields.SingleOrDefault(sort => sort.Path == field.Path)?.Direction
                ?? PhysicalSortDirection.Ascending)));
        if (query.PagingSupport is QueryPagingSupport.Cursor or QueryPagingSupport.Offset &&
            logicalIndex.Fields.All(field => field.Path != PhysicalDocumentFieldPaths.Id))
        {
            columns.Add(new PhysicalIndexColumnDefinition(
                query.PagingSupport == QueryPagingSupport.Cursor
                    ? new DocumentEnvelopeDefinition().IdLookupKeyColumn
                    : new DocumentEnvelopeDefinition().IdComparisonKeyColumn,
                columns.Count));
        }
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "workflow_trigger_bindings",
            projections,
            indexes: [new PhysicalIndexDefinition(logicalIndex.Identity, columns)]);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logicalIndex],
            [query]);
        return Resolve(storage, null);
    }

    private static PlanningFixture CreateCollectionFixture(
        QueryPagingSupport pagingSupport = QueryPagingSupport.Offset,
        bool latestPerKey = false,
        IndexValueKind logicalValueKind = IndexValueKind.String,
        PortablePhysicalType physicalType = PortablePhysicalType.String,
        BoundedMutationAction? action = null,
        int maximumCollectionElements = 16,
        QuerySortSupport sortSupport = QuerySortSupport.None,
        IReadOnlyList<BoundedQuerySortField>? sortFields = null,
        IReadOnlySet<PortableQueryOperation>? operations = null,
        BoundedQueryExecutionClass executionClass = BoundedQueryExecutionClass.ScaleBearing)
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-values",
            [new IndexField("values")],
            logicalValueKind,
            false,
            MissingValueBehavior.IncludedAsNull);
        var query = new BoundedQueryDeclaration(
            "list-by-values",
            logicalIndex.Identity,
            operations ?? new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.CollectionContains,
                PortableQueryOperation.CollectionContainsAll
            },
            latestPerKey ? QuerySortSupport.Both : sortSupport,
            pagingSupport,
            executionClass,
            sortFields: latestPerKey
                ? [new BoundedQuerySortField("values", PhysicalSortDirection.Ascending)]
                : sortFields,
            latestPerKeyPath: latestPerKey ? "values" : null);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "collection_entities",
            [
                new ProjectedColumnDefinition(
                    "values",
                    "values",
                    physicalType,
                    Length: physicalType == PortablePhysicalType.String ? 128 : null,
                    IsNullable: true,
                    Cardinality: ProjectionCardinality.CollectionElements,
                    MaxCollectionElements: maximumCollectionElements)
            ]);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logicalIndex],
            [query]);
        var fixture = Resolve(storage, null);
        if (action is null)
            return fixture;
        var mutationStorage = new StorageUnitPhysicalStorage(
            storage.ProvisioningMode,
            storage.Policy,
            storage.LogicalIndexes,
            storage.BoundedQueries,
            storage.NameOverrides,
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "mutate-by-values",
                    query.Identity,
                    action)
            ]);
        return new PlanningFixture(fixture.Route, mutationStorage);
    }

    private static PlanningFixture CreateTypedFixture(
        bool projected,
        IndexValueKind valueKind,
        PortableQueryOperation operation,
        PortablePhysicalType? projectedType = null)
        => Resolve(CreateTypedStorage(projected, valueKind, operation, projectedType), null);

    private static StorageUnitPhysicalStorage CreateTypedStorage(
        bool projected,
        IndexValueKind valueKind,
        PortableQueryOperation operation,
        PortablePhysicalType? projectedType = null)
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-value",
            [new IndexField("value")],
            valueKind,
            false,
            MissingValueBehavior.IncludedAsNull);
        var query = new BoundedQueryDeclaration(
            "find-by-value",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { operation },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.Ordinary);
        var definition = projected
            ? PhysicalTableDefinition.PhysicalEntityTable(
                "typed_entities",
                [TypedProjection("value", projectedType ?? ToPortableType(valueKind))],
                indexes:
                [
                    new PhysicalIndexDefinition(
                        logicalIndex.Identity,
                        [
                            new PhysicalIndexColumnDefinition("storage_scope", 0),
                            new PhysicalIndexColumnDefinition("value", 1)
                        ])
                ])
            : PhysicalTableDefinition.DedicatedDocumentTable("typed_documents");
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logicalIndex],
            [query]);
        return storage;
    }

    private static PhysicalStorageResolutionResult Resolve(StorageUnitPhysicalStorage storage)
    {
        var template = SampleManifests.MetadataManifest();
        var manifest = template with
        {
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    Identity = new StorageUnitIdentity("workflowTriggerBinding"),
                    PhysicalStorage = storage
                }
            ]
        };
        return PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
    }

    private static PortablePhysicalType ToPortableType(IndexValueKind valueKind) => valueKind switch
    {
        IndexValueKind.String or IndexValueKind.Keyword => PortablePhysicalType.String,
        IndexValueKind.Number => PortablePhysicalType.Decimal,
        IndexValueKind.Boolean => PortablePhysicalType.Boolean,
        IndexValueKind.DateTime => PortablePhysicalType.DateTime,
        _ => throw new ArgumentOutOfRangeException(nameof(valueKind), valueKind, null)
    };

    private static ProjectedColumnDefinition TypedProjection(string path, PortablePhysicalType type) =>
        new(
            path,
            path,
            type,
            Precision: type == PortablePhysicalType.Decimal ? 18 : null,
            Scale: type == PortablePhysicalType.Decimal ? 4 : null);

    private static RelationshipPlanningFixture CreateRelationshipFixture(
        bool relatedTarget,
        bool includeReferenceIndex = true,
        bool includeMutation = true,
        string authorizationPhysicalName = "authorizations",
        string tokenPhysicalName = "tokens",
        PortablePhysicalType sourceReferenceType = PortablePhysicalType.String,
        ProjectionCardinality sourceReferenceCardinality = ProjectionCardinality.Scalar,
        IndexValueKind sourceReferenceValueKind = IndexValueKind.Keyword,
        StringIdentityCasePolicy targetIdentityCasePolicy = StringIdentityCasePolicy.Ordinal,
        StringIdentityCasePolicy referenceCasePolicy = StringIdentityCasePolicy.Ordinal,
        TenancyPolicy? authorizationTenancy = null,
        TenancyPolicy? tokenTenancy = null,
        bool nonLeadingSourceReference = false,
        bool nonLeadingTargetIdentity = false,
        bool nonLeadingTargetPredicate = false,
        string sourceReferencePath = "authorizationId",
        string sourceReferenceIndexIdentity = "token-by-authorization-id",
        string targetEqualityIndexIdentity = "authorization-by-id",
        StringIdentityCasePolicy sourceIdentityCasePolicy = StringIdentityCasePolicy.Ordinal,
        string? manifestIdentity = null,
        string authorizationIdentity = "authorization",
        string tokenIdentity = "token")
    {
        var authorizationStatusIndex = new LogicalIndexDeclaration(
            "authorization-by-status",
            [new IndexField("status")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var authorizationIdIndex = new LogicalIndexDeclaration(
            targetEqualityIndexIdentity,
            [new IndexField(PhysicalDocumentFieldPaths.Id)],
            IndexValueKind.Keyword,
            true,
            MissingValueBehavior.Excluded);
        var tokenStatusIndex = new LogicalIndexDeclaration(
            "token-by-status",
            [new IndexField("status")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var tokenReferenceIndex = new LogicalIndexDeclaration(
            sourceReferenceIndexIdentity,
            [new IndexField(sourceReferencePath)],
            sourceReferenceValueKind,
            false,
            MissingValueBehavior.IncludedAsNull);
        var nonLeadingAuthorizationStatusIndex = new LogicalIndexDeclaration(
            "authorization-by-status-after-id",
            [new IndexField("status")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var authorizationLogicalIndexes = nonLeadingTargetPredicate
            ? new[] { authorizationStatusIndex, authorizationIdIndex, nonLeadingAuthorizationStatusIndex }
            : new[] { authorizationStatusIndex, authorizationIdIndex };
        PhysicalIndexColumnDefinition[] AuthorizationIndex(params string[] fields)
        {
            var columns = new List<string>();
            if ((authorizationTenancy ?? TenancyPolicy.Scoped).Kind != TenancyKind.Global)
                columns.Add("storage_scope");
            columns.AddRange(fields);
            return columns
                .Select((field, index) => new PhysicalIndexColumnDefinition(field, index))
                .ToArray();
        }
        var authorizationPhysicalIndexes = new List<PhysicalIndexDefinition>
        {
            new(
                authorizationStatusIndex.Identity,
                AuthorizationIndex("status")),
            new(
                authorizationIdIndex.Identity,
                nonLeadingTargetIdentity
                    ? AuthorizationIndex("status", "id_comparison_key")
                    : AuthorizationIndex("id_comparison_key"),
                isUnique: true,
                missingValueBehavior: MissingValueBehavior.Excluded)
        };
        if (nonLeadingTargetPredicate)
        {
            authorizationPhysicalIndexes.Add(new PhysicalIndexDefinition(
                nonLeadingAuthorizationStatusIndex.Identity,
                AuthorizationIndex("id_comparison_key", "status")));
        }
        var authorizationStorage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.PhysicalEntityTable(
                authorizationPhysicalName,
                [new ProjectedColumnDefinition("status", "status", PortablePhysicalType.String)],
                indexes: authorizationPhysicalIndexes)),
            authorizationLogicalIndexes,
            [new BoundedQueryDeclaration(
                "prune-authorizations",
                authorizationStatusIndex.Identity,
                new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                QuerySortSupport.None,
                QueryPagingSupport.None,
                BoundedQueryExecutionClass.ScaleBearing)],
            boundedMutations: relatedTarget || !includeMutation
                ? []
                :
                [
                    new BoundedMutationDeclaration(
                        "guarded-prune",
                        "prune-authorizations",
                        BoundedMutationAction.Delete(),
                        [BoundedMutationRelationshipGuard.RequireNoReferences("token-authorization")])
                ]);
        var tokenIndexes = includeReferenceIndex
            ? new[] { tokenStatusIndex, tokenReferenceIndex }
            : new[] { tokenStatusIndex };
        PhysicalIndexColumnDefinition[] TokenIndex(params string[] fields)
        {
            var columns = new List<string>();
            if ((tokenTenancy ?? TenancyPolicy.Scoped).Kind != TenancyKind.Global)
                columns.Add("storage_scope");
            columns.AddRange(fields);
            return columns
                .Select((field, index) => new PhysicalIndexColumnDefinition(field, index))
                .ToArray();
        }
        IReadOnlyList<PhysicalIndexDefinition> tokenPhysicalIndexes = includeReferenceIndex
            ? new[]
            {
                new PhysicalIndexDefinition(
                    tokenStatusIndex.Identity,
                    TokenIndex("status")),
                new PhysicalIndexDefinition(
                    tokenReferenceIndex.Identity,
                    nonLeadingSourceReference
                        ? TokenIndex("status", sourceReferencePath)
                        : TokenIndex(sourceReferencePath))
            }
            :
            [
                new PhysicalIndexDefinition(
                    tokenStatusIndex.Identity,
                    TokenIndex("status"))
            ];
        var tokenStorage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.PhysicalEntityTable(
                tokenPhysicalName,
                [
                    new ProjectedColumnDefinition("status", "status", PortablePhysicalType.String),
                    new ProjectedColumnDefinition(
                        sourceReferencePath,
                        sourceReferencePath,
                        sourceReferenceType,
                        Precision: sourceReferenceType == PortablePhysicalType.Decimal ? 18 : null,
                        Scale: sourceReferenceType == PortablePhysicalType.Decimal ? 0 : null,
                        Cardinality: sourceReferenceCardinality,
                        MaxCollectionElements: sourceReferenceCardinality == ProjectionCardinality.CollectionElements
                            ? 8
                            : null)
                ],
                indexes: tokenPhysicalIndexes)),
            tokenIndexes,
            [new BoundedQueryDeclaration(
                "prune-tokens",
                tokenStatusIndex.Identity,
                new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                QuerySortSupport.None,
                QueryPagingSupport.None,
                BoundedQueryExecutionClass.ScaleBearing)],
            boundedMutations: relatedTarget && includeMutation
                ?
                [
                    new BoundedMutationDeclaration(
                        "guarded-prune",
                        "prune-tokens",
                        BoundedMutationAction.Delete(),
                        [BoundedMutationRelationshipGuard.RequireRelatedTargetNotEqual(
                            "token-authorization",
                            "status",
                            nonLeadingTargetPredicate
                                ? nonLeadingAuthorizationStatusIndex.Identity
                                : authorizationStatusIndex.Identity,
                            "valid")])
                ]
                : []);
        var template = SampleManifests.MetadataManifest();
        var original = template.StorageUnits.Single();
        var authorization = original with
        {
            Identity = new StorageUnitIdentity(authorizationIdentity),
            DisplayName = "Authorization",
            IdentityPolicy = IdentityPolicy.StringId(stringCasePolicy: targetIdentityCasePolicy),
            Tenancy = authorizationTenancy ?? TenancyPolicy.Scoped,
            PhysicalStorage = authorizationStorage
        };
        var token = original with
        {
            Identity = new StorageUnitIdentity(tokenIdentity),
            DisplayName = "Token",
            IdentityPolicy = IdentityPolicy.StringId(stringCasePolicy: sourceIdentityCasePolicy),
            Tenancy = tokenTenancy ?? TenancyPolicy.Scoped,
            PhysicalStorage = tokenStorage
        };
        var manifest = template with
        {
            Identity = manifestIdentity is null
                ? template.Identity
                : new StorageManifestIdentity(manifestIdentity),
            StorageUnits = [authorization, token],
            SharedDocumentStorages = [],
            Relationships =
            [
                new ManifestRelationshipDeclaration(
                    "token-authorization",
                    token.Identity,
                    sourceReferencePath,
                    tokenReferenceIndex.Identity,
                    authorization.Identity,
                    PhysicalDocumentFieldPaths.Id,
                    authorizationIdIndex.Identity,
                    referenceCasePolicy)
            ]
        };
        return ResolveRelationshipFixture(manifest, relatedTarget);
    }

    private static RelationshipPlanningFixture ResolveRelationshipFixture(
        StorageManifest manifest,
        bool relatedTarget)
    {
        var routeSet = CompileRelationshipRouteSet(manifest);
        var relationship = Assert.Single(manifest.Relationships);
        var mutationStorageUnit = relatedTarget
            ? relationship.SourceStorageUnit
            : relationship.TargetStorageUnit;
        var mutationStorage = manifest.StorageUnits
            .Single(unit => unit.Identity == mutationStorageUnit)
            .PhysicalStorage!;
        var mutationRoute = routeSet.Routes.Single(route =>
            route.StorageUnit == mutationStorageUnit);
        return new RelationshipPlanningFixture(manifest, routeSet, mutationRoute, mutationStorage);
    }

    private static string ExpectedRelationshipMaterializationRoot(
        StorageManifest manifest,
        PhysicalRelationshipPlan plan)
    {
        static string Join(params string?[] values) =>
            string.Concat(values.Select(value => value is null ? "-1:" : $"{value.Length}:{value}"));

        var relationship = plan.Declaration;
        var source = plan.SourceRoute;
        var target = plan.TargetRoute;
        var canonical = Join(
            manifest.Identity.Value,
            relationship.Identity,
            relationship.SourceStorageUnit.Value,
            relationship.SourceReferencePath,
            relationship.SourceReferenceIndexIdentity,
            relationship.TargetStorageUnit.Value,
            relationship.TargetIdentityPath,
            relationship.TargetEqualityIndexIdentity,
            ((int)relationship.ReferenceCasePolicy).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)source.ScopePolicy).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)target.ScopePolicy).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)source.Envelope.Identity.StringCasePolicy).ToString(System.Globalization.CultureInfo.InvariantCulture),
            source.Envelope.Identity.ComparisonAlgorithmId,
            source.Envelope.Identity.LookupAlgorithmId,
            target.Envelope.Identity.ComparisonAlgorithmId,
            target.Envelope.Identity.LookupAlgorithmId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static ManifestExecutableRouteSet CompileRelationshipRouteSet(StorageManifest manifest)
    {
        var result = ManifestExecutableRouteSetCompiler.Compile(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return Assert.IsType<ManifestExecutableRouteSet>(result.RouteSet);
    }

    private static PhysicalMutationPlan CompileRelationshipMutationPlan(RelationshipPlanningFixture fixture)
    {
        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return Assert.Single(result.Plans);
    }

    private static PlanningFixture Resolve(
        StorageUnitPhysicalStorage storage,
        SharedStorageBinding? binding,
        TenancyPolicy? tenancy = null,
        StringIdentityCasePolicy identityCasePolicy = StringIdentityCasePolicy.Ordinal)
    {
        var template = SampleManifests.MetadataManifest();
        var unit = template.StorageUnits.Single() with
        {
            Identity = new StorageUnitIdentity("workflowTriggerBinding"),
            IdentityPolicy = IdentityPolicy.StringId(stringCasePolicy: identityCasePolicy),
            Tenancy = tenancy ?? template.StorageUnits.Single().Tenancy,
            PhysicalStorage = storage
        };
        var manifest = template with
        {
            StorageUnits = [unit],
            SharedDocumentStorages = binding is null
                ? []
                : [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())]
        };
        var resolved = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
        Assert.True(resolved.IsValid, string.Join("; ", resolved.Diagnostics.Select(x => x.Message)));
        var routeResult = ExecutableStorageRouteCompiler.Compile(Assert.Single(resolved.Definitions));
        Assert.True(routeResult.IsValid, string.Join("; ", routeResult.Diagnostics.Select(x => x.Message)));
        return new PlanningFixture(Assert.Single(routeResult.Routes), storage);
    }

    private static LogicalIndexDeclaration StimulusTypeIndex() =>
        new(
            "by-stimulus-type",
            [new IndexField("stimulusType")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);

    private static LogicalIndexDeclaration SortResidualIndex() =>
        new(
            "by-last-modified-definition",
            [
                new IndexField("lastModifiedAt", IndexValueKind.DateTime),
                new IndexField("definitionId")
            ],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);

    private static BoundedQueryDeclaration SortResidualQuery(
        LogicalIndexDeclaration logicalIndex,
        string residualPath,
        bool useImplicitPredicate = false) =>
        new(
            "browse-definitions",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.Contains
            },
            QuerySortSupport.Both,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields:
            [
                new BoundedQuerySortField("lastModifiedAt", PhysicalSortDirection.Descending),
                new BoundedQuerySortField("definitionId", PhysicalSortDirection.Ascending)
            ],
            predicateFields: useImplicitPredicate
                ? null
                :
                [
                    new BoundedQueryPredicateField(
                        "lastModifiedAt",
                        new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
                ],
            residualPredicateFields:
            [
                new BoundedQueryResidualPredicateField(
                    residualPath,
                    residualPath == "lastModifiedAt"
                        ? IndexValueKind.DateTime
                        : IndexValueKind.String,
                    new HashSet<PortableQueryOperation>
                    {
                        residualPath == "lastModifiedAt"
                            ? PortableQueryOperation.Equal
                            : PortableQueryOperation.Contains
                    })
            ]);

    private static BoundedQueryDeclaration Query(
        BoundedQueryExecutionClass executionClass,
        IReadOnlyList<BoundedQueryPredicateField>? predicateFields = null,
        IReadOnlySet<BoundedQueryResultOperation>? resultOperations = null,
        QueryPagingSupport pagingSupport = QueryPagingSupport.Offset,
        bool supportsDisjunction = false,
        bool supportsTotalCount = true,
        string? latestPerKeyPath = null,
        IReadOnlyList<BoundedQuerySortField>? sortFields = null) =>
        new(
            "list-by-stimulus-type",
            "by-stimulus-type",
            ScalarQueryOperations(),
            sortFields is null ? QuerySortSupport.Both : QuerySortSupport.Descending,
            pagingSupport,
            executionClass,
            supportsDisjunction,
            supportsTotalCount,
            sortFields,
            predicateFields,
            resultOperations,
            latestPerKeyPath);

    private static IReadOnlyList<PhysicalIndexColumnDefinition> ScopedStimulusTypeIndexColumns(
        BoundedQueryDeclaration query)
    {
        var columns = new List<PhysicalIndexColumnDefinition>
        {
            new("storage_scope", 0),
            new("stimulusType", 1)
        };
        if (query.PagingSupport is QueryPagingSupport.Cursor or QueryPagingSupport.Offset)
        {
            columns.Add(new PhysicalIndexColumnDefinition(
                query.PagingSupport == QueryPagingSupport.Cursor
                    ? "id_lookup_key"
                    : "id_comparison_key",
                columns.Count));
        }
        return columns;
    }

    private static IReadOnlySet<PortableQueryOperation> ScalarQueryOperations() =>
        Enum.GetValues<PortableQueryOperation>()
            .Where(operation => operation is not (
                PortableQueryOperation.CollectionContains or
                PortableQueryOperation.CollectionContainsAll))
            .ToHashSet();

    private static PhysicalQueryPlannerCapabilities CapabilitiesFor(PhysicalQueryAccessKind accessKind) =>
        Capabilities(accessKind switch
        {
            PhysicalQueryAccessKind.LinkedIndexThenPrimary => PhysicalQuerySourceKind.LinkedIndex,
            PhysicalQueryAccessKind.PrimaryCanonicalJson => PhysicalQuerySourceKind.PrimaryCanonicalJson,
            PhysicalQueryAccessKind.PrimaryProjectedColumns => PhysicalQuerySourceKind.PrimaryProjectedColumns,
            _ => throw new ArgumentOutOfRangeException(nameof(accessKind), accessKind, null)
        });

    private static PhysicalQueryPlannerCapabilities Capabilities(params PhysicalQuerySourceKind[] sources) =>
        Capabilities(new ProviderIdentity("test-provider", "1.0.0"), sources);

    private static PhysicalQueryPlannerCapabilities Capabilities(
        ProviderIdentity provider,
        params PhysicalQuerySourceKind[] sources) =>
        CapabilitiesWithPaging(provider, true, true, sources);

    private static PhysicalQueryPlannerCapabilities CapabilitiesWithPaging(
        bool supportsKeysetPaging,
        bool supportsLatestPerKey,
        params PhysicalQuerySourceKind[] sources) =>
        CapabilitiesWithPaging(
            new ProviderIdentity("test-provider", "1.0.0"),
            supportsKeysetPaging,
            supportsLatestPerKey,
            sources);

    private static PhysicalQueryPlannerCapabilities CapabilitiesWithPaging(
        ProviderIdentity provider,
        bool supportsKeysetPaging,
        bool supportsLatestPerKey,
        params PhysicalQuerySourceKind[] sources) =>
        new(
            provider,
            sources,
            Enum.GetValues<PortableQueryOperation>().ToHashSet(),
            sources.ToDictionary(source => source, source => $"test.{source}"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stimulusType"] = "content.stimulusType",
                ["createdAt"] = "content.createdAt",
                ["id"] = "_id.id",
                ["storageScope"] = "storage_scope",
                ["documentKind"] = "document_kind"
            },
            supportsCompoundPredicates: true,
            supportsDisjunction: true,
            supportsOffsetPaging: true,
            supportsKeysetPaging,
            supportsCount: true,
            supportsAny: true,
            supportsFirst: true,
            supportsLatestPerKey);

    private static PhysicalQueryPlan AssertPlan(PhysicalQueryPlanCompilationResult result)
    {
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        return Assert.Single(result.Plans);
    }

    private static PhysicalQueryHandlerCertification CertificationFor(
        PhysicalQueryPlan plan,
        ProviderIdentity? provider = null,
        ProviderPhysicalObjectName? indexName = null,
        ExecutableStorageObjectRole? target = null,
        ProviderPhysicalObjectName? lookupObject = null,
        IReadOnlyDictionary<string, string>? fieldIdentifiers = null)
    {
        return new PhysicalQueryHandlerCertification(
            provider ?? plan.Provider,
            plan.StorageUnit,
            plan.QueryIdentity,
            plan.LogicalIndexIdentity,
            plan.LogicalIndexPaths,
            plan.AccessKind,
            target ?? plan.Scope.Field.Target,
            lookupObject ?? plan.LookupObject,
            plan.PrimaryObject,
            indexName ?? plan.IndexName,
            fieldIdentifiers ?? PlanFieldIdentifiers(plan),
            plan.RouteFingerprint);
    }

    private static Dictionary<string, string> PlanFieldIdentifiers(PhysicalQueryPlan plan) =>
        plan.RequiredFields
            .GroupBy(field => field.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Identifier, StringComparer.Ordinal);

    private sealed record PlanningFixture(ExecutableStorageRoute Route, StorageUnitPhysicalStorage Storage);

    private sealed record RelationshipPlanningFixture(
        StorageManifest Manifest,
        ManifestExecutableRouteSet RouteSet,
        ExecutableStorageRoute MutationRoute,
        StorageUnitPhysicalStorage MutationStorage);

    private sealed class RecordingHandler(
        string identity,
        PhysicalQuerySourceKind source,
        IReadOnlySet<PortableQueryOperation>? supportedOperations = null,
        IReadOnlyList<PhysicalQueryHandlerCertification>? certifications = null) : IPhysicalDocumentQueryHandler
    {
        public string Identity { get; } = identity;
        public PhysicalQuerySourceKind Source { get; } = source;
        public IReadOnlySet<PortableQueryOperation> SupportedOperations { get; } =
            supportedOperations ?? Enum.GetValues<PortableQueryOperation>().ToHashSet();
        public IReadOnlyDictionary<string, string> NativeFieldIdentifiers { get; } =
            new Dictionary<string, string>
            {
                ["stimulusType"] = "content.stimulusType",
                ["createdAt"] = "content.createdAt",
                ["id"] = "_id.id",
                ["storageScope"] = "storage_scope",
                ["documentKind"] = "document_kind"
            };
        public IReadOnlyList<PhysicalQueryHandlerCertification> Certifications { get; } =
            certifications ?? [];
        public bool SupportsCompoundPredicates => true;
        public bool SupportsDisjunction => true;
        public bool SupportsOffsetPaging => true;
        public bool SupportsKeysetPaging => true;
        public bool SupportsCount => true;
        public bool SupportsAny => true;
        public bool SupportsFirst => true;
        public bool SupportsLatestPerKey => true;
        public PhysicalQueryPlan? LastPlan { get; private set; }

        public Task<DocumentQueryResult> QueryAsync(
            DocumentQuery query,
            PhysicalQueryPlan plan,
            CancellationToken cancellationToken)
        {
            LastPlan = plan;
            return Task.FromResult(DocumentQueryResult.Empty);
        }

        public Task<long> CountAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
        {
            LastPlan = plan;
            return Task.FromResult(0L);
        }

        public Task<DocumentEnvelope?> FirstOrDefaultAsync(
            DocumentQuery query,
            PhysicalQueryPlan plan,
            CancellationToken cancellationToken)
        {
            LastPlan = plan;
            return Task.FromResult<DocumentEnvelope?>(null);
        }

        public Task<bool> AnyAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
        {
            LastPlan = plan;
            return Task.FromResult(false);
        }
    }

    private sealed class RecordingMutationHandler(
        string identity,
        PhysicalQuerySourceKind source,
        IReadOnlyList<PhysicalMutationHandlerCertification> certifications,
        IReadOnlySet<BoundedMutationActionKind>? supportedActions = null) : IPhysicalDocumentMutationHandler
    {
        public string Identity { get; } = identity;
        public PhysicalQuerySourceKind Source { get; } = source;
        public IReadOnlySet<PortableQueryOperation> SupportedOperations { get; } =
            Enum.GetValues<PortableQueryOperation>().ToHashSet();
        public IReadOnlySet<BoundedMutationActionKind> SupportedActions { get; } =
            supportedActions ?? Enum.GetValues<BoundedMutationActionKind>().ToHashSet();
        public IReadOnlyDictionary<string, string> NativeFieldIdentifiers { get; } =
            new Dictionary<string, string>();
        public IReadOnlyList<PhysicalMutationHandlerCertification> Certifications { get; } = certifications;
        public bool SupportsCompoundPredicates => true;
        public bool SupportsDisjunction => true;
        public int ExecutionCount { get; private set; }

        public Task<BoundedMutationResult> ExecuteAsync(
            DocumentMutation mutation,
            PhysicalMutationPlan plan,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(BoundedMutationResult.Completed(3));
        }
    }

}
