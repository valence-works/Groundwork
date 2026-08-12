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
using Xunit;

namespace Groundwork.Tests;

public sealed partial class PhysicalQueryPlanCompilerTests
{
    [Fact]
    public void OneClosedDeclarationPlansEveryScalarBoundedOperatorAndTerminal()
    {
        var predicate = new BoundedQueryPredicateField(
            "stimulusType",
            ScalarQueryOperations());
        var query = Query(
            BoundedQueryExecutionClass.Ordinary,
            predicateFields: [predicate],
            resultOperations: Enum.GetValues<BoundedQueryResultOperation>().ToHashSet(),
            pagingSupport: QueryPagingSupport.Offset,
            supportsDisjunction: true,
            supportsTotalCount: true);
        var fixture = CreateFixture(PhysicalStorageForm.DedicatedDocumentTable, query);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson)));

        Assert.Equal(
            ScalarQueryOperations().Order(),
            Assert.Single(plan.Predicates).Operations.Order());
        Assert.Equal(
            Enum.GetValues<BoundedQueryResultOperation>().Order(),
            plan.ResultOperations.Order());
        Assert.True(plan.SupportsDisjunction);
        Assert.Equal(QueryPagingSupport.Offset, plan.PagingSupport);
    }

    [Fact]
    public void ScaleBearingQueryPlansDeclaredResidualPredicatesOnTheIndexedPrimaryRoute()
    {
        var indexedPredicate = new BoundedQueryPredicateField(
            "stimulusType",
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal });
        var residualPredicate = new BoundedQueryResidualPredicateField(
            "status",
            IndexValueKind.Keyword,
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.In
            });
        var query = new BoundedQueryDeclaration(
            "list-by-stimulus-type",
            "by-stimulus-type",
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.In
            },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            predicateFields: [indexedPredicate],
            residualPredicateFields: [residualPredicate]);
        var fixture = CreateEntityFixture(StimulusTypeIndex(), query);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));

        Assert.Equal(PhysicalQueryAccessKind.PrimaryProjectedColumns, plan.AccessKind);
        Assert.NotNull(plan.IndexName);
        Assert.Empty(plan.RequiredEqualityPrefixPaths);
        Assert.Collection(
            plan.Predicates,
            predicate =>
            {
                Assert.Equal("stimulusType", predicate.Path);
                Assert.False(predicate.IsResidual);
            },
            predicate =>
            {
                Assert.Equal("status", predicate.Path);
                Assert.True(predicate.IsResidual);
                Assert.Equal(IndexValueKind.Keyword, predicate.Field.ValueKind);
                Assert.Equal(
                    new[] { PortableQueryOperation.Equal, PortableQueryOperation.In },
                    predicate.Operations.Order());
            });
        Assert.Contains("\"residual\":true", PhysicalQueryPlanSerializer.Serialize(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void SortOnlyLogicalIndexPathCompilesAsAResidualPredicate()
    {
        var logicalIndex = SortResidualIndex();
        var query = SortResidualQuery(logicalIndex, "definitionId");
        var fixture = CreateEntityFixture(logicalIndex, query);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));

        var residual = Assert.Single(plan.Predicates, predicate => predicate.IsResidual);
        Assert.Equal("definitionId", residual.Path);
        Assert.Contains(PortableQueryOperation.Contains, residual.Operations);
        Assert.Equal(
            ["lastModifiedAt", "definitionId", "storageScope", "id"],
            plan.Order.Select(order => order.Path));
    }

    [Fact]
    public void PredicatePrefixLogicalIndexPathDoesNotCompileAsAResidualPredicate()
    {
        var logicalIndex = SortResidualIndex();
        var fixture = CreateEntityFixture(
            logicalIndex,
            SortResidualQuery(logicalIndex, "definitionId"));
        var invalidStorage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            [SortResidualQuery(logicalIndex, "lastModifiedAt")]);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            invalidStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-013" &&
            diagnostic.Message.Contains("lastModifiedAt", StringComparison.Ordinal));
    }

    [Fact]
    public void ImplicitFirstIndexPredicateDoesNotCompileAsAResidualPredicate()
    {
        var logicalIndex = SortResidualIndex();
        var fixture = CreateEntityFixture(
            logicalIndex,
            SortResidualQuery(logicalIndex, "definitionId"));
        var invalidStorage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            [SortResidualQuery(logicalIndex, "lastModifiedAt", useImplicitPredicate: true)]);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            invalidStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-013" &&
            diagnostic.Message.Contains("lastModifiedAt", StringComparison.Ordinal));
    }

    [Fact]
    public void ResidualPredicateShapeParticipatesInThePlanFingerprint()
    {
        PlanningFixture Fixture(IReadOnlySet<PortableQueryOperation> residualOperations)
        {
            var query = new BoundedQueryDeclaration(
                "list-by-stimulus-type",
                "by-stimulus-type",
                new HashSet<PortableQueryOperation>
                {
                    PortableQueryOperation.Equal,
                    PortableQueryOperation.In
                },
                QuerySortSupport.None,
                QueryPagingSupport.None,
                BoundedQueryExecutionClass.ScaleBearing,
                predicateFields:
                [
                    new BoundedQueryPredicateField(
                        "stimulusType",
                        new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
                ],
                residualPredicateFields:
                [
                    new BoundedQueryResidualPredicateField(
                        "status",
                        IndexValueKind.Keyword,
                        residualOperations)
                ]);
            return CreateEntityFixture(StimulusTypeIndex(), query);
        }

        var equalityFixture = Fixture(new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal });
        var membershipFixture = Fixture(new HashSet<PortableQueryOperation>
        {
            PortableQueryOperation.Equal,
            PortableQueryOperation.In
        });
        var equality = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            equalityFixture.Route,
            equalityFixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));
        var membership = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            membershipFixture.Route,
            membershipFixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));

        Assert.NotEqual(equality.Fingerprint, membership.Fingerprint);
    }

    [Fact]
    public async Task RequiredResidualPredicateMustBeSuppliedBeforeHandlerDispatch()
    {
        var query = new BoundedQueryDeclaration(
            "list-by-stimulus-type",
            "by-stimulus-type",
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing,
            residualPredicateFields:
            [
                new BoundedQueryResidualPredicateField(
                    "status",
                    IndexValueKind.Keyword,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    isRequired: true)
            ]);
        var fixture = CreateEntityFixture(StimulusTypeIndex(), query);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            capabilities));
        var handler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryProjectedColumns,
            certifications: [CertificationFor(plan)]);
        var store = new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]);
        var missing = new DocumentQuery(
            "workflowTriggerBinding",
            query.Identity);
        var supplied = missing.Where(DocumentQueryClause.Of(
            DocumentQueryComparison.Equal("status", "ready")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.QueryAsync(missing));
        await store.QueryAsync(supplied);

        Assert.Contains("status", exception.Message, StringComparison.Ordinal);
        Assert.True(plan.Predicates.Single(predicate => predicate.Path == "status").IsRequired);
        Assert.Equal(plan, handler.LastPlan);
    }

    [Fact]
    public void ResidualEnvelopePredicateMustUseTheIntrinsicValueKind()
    {
        var query = new BoundedQueryDeclaration(
            "list-by-stimulus-type",
            "by-stimulus-type",
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.Ordinary,
            residualPredicateFields:
            [
                new BoundedQueryResidualPredicateField(
                    PhysicalDocumentFieldPaths.Version,
                    IndexValueKind.Keyword,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var fixture = CreateFixture(PhysicalStorageForm.DedicatedDocumentTable, query);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-010" &&
            diagnostic.Message.Contains(PhysicalDocumentFieldPaths.Version, StringComparison.Ordinal));
    }

    [Fact]
    public void ScaleBearingResidualPredicateUsesALinkedProjectionBeforeHydration()
    {
        var logicalIndex = StimulusTypeIndex();
        var query = new BoundedQueryDeclaration(
            "list-by-stimulus-type",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing,
            residualPredicateFields:
            [
                new BoundedQueryResidualPredicateField(
                    "status",
                    IndexValueKind.Keyword,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var definition = PhysicalTableDefinition.DedicatedDocumentTable(
            "workflow_trigger_bindings",
            indexes:
            [
                new PhysicalIndexDefinition(
                    logicalIndex.Identity,
                    [
                        new PhysicalIndexColumnDefinition("storage_scope", 0),
                        new PhysicalIndexColumnDefinition("stimulusType", 1)
                    ])
            ],
            linkedProjectedColumns:
            [
                new ProjectedColumnDefinition("stimulusType", "stimulusType", PortablePhysicalType.String),
                new ProjectedColumnDefinition("status", "status", PortablePhysicalType.String)
            ],
            linkedProjectionLogicalName: "workflow_trigger_binding_index");
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logicalIndex],
            [query]);
        var fixture = Resolve(storage, null);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.LinkedIndex));

        var plan = AssertPlan(result);
        Assert.Equal(PhysicalQueryAccessKind.LinkedIndexThenPrimary, plan.AccessKind);
        Assert.Equal(
            ExecutableStorageObjectRole.LinkedIndexStorage,
            plan.Predicates.Single(predicate => predicate.Path == "status").Field.Target);
    }

    [Fact]
    public void BoundedMutationRejectsAQueryWithResidualPredicates()
    {
        var query = new BoundedQueryDeclaration(
            "list-by-stimulus-type",
            "by-stimulus-type",
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing,
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "stimulusType",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ],
            residualPredicateFields:
            [
                new BoundedQueryResidualPredicateField(
                    "status",
                    IndexValueKind.Keyword,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.PhysicalEntityTable(
                "workflow_trigger_bindings",
                [
                    new ProjectedColumnDefinition("stimulusType", "stimulusType", PortablePhysicalType.String),
                    new ProjectedColumnDefinition("status", "status", PortablePhysicalType.String)
                ],
                indexes:
                [
                    new PhysicalIndexDefinition(
                        "by-stimulus-type",
                        [
                            new PhysicalIndexColumnDefinition("storage_scope", 0),
                            new PhysicalIndexColumnDefinition("stimulusType", 1)
                        ])
                ])),
            [StimulusTypeIndex()],
            [query],
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "delete-by-stimulus-type",
                    query.Identity,
                    BoundedMutationAction.Delete())
            ]);
        var fixture = Resolve(storage, null);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-MUTATION-006");
    }

    [Fact]
    public void CollectionMembershipPlanBindsTheValueLedMembershipIndex()
    {
        var fixture = CreateCollectionFixture();

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.CollectionElements)));
        var collection = Assert.Single(fixture.Route.CollectionElementStorages);

        Assert.Equal(PhysicalQueryAccessKind.CollectionElementsThenPrimary, plan.AccessKind);
        Assert.Equal(collection.Storage.Name, plan.LookupObject);
        Assert.Equal(collection.MembershipKey.Name, plan.IndexName);
        var predicate = Assert.Single(plan.Predicates);
        Assert.Equal(collection.Value.Column.Identifier, predicate.Field.Identifier);
        Assert.Equal(
            new PhysicalQueryCollectionConstraint(PortablePhysicalType.String, 16),
            predicate.CollectionConstraint);
    }

    [Theory]
    [InlineData(false, PortableQueryOperation.CollectionContains)]
    [InlineData(false, PortableQueryOperation.CollectionContainsAll)]
    [InlineData(true, PortableQueryOperation.CollectionContains)]
    [InlineData(true, PortableQueryOperation.CollectionContainsAll)]
    public void CollectionMembershipOperationsRejectScalarLogicalIndexesAndProjections(
        bool projected,
        PortableQueryOperation operation)
    {
        var fixture = CreateTypedFixture(projected, IndexValueKind.String, operation);
        var source = projected
            ? PhysicalQuerySourceKind.PrimaryProjectedColumns
            : PhysicalQuerySourceKind.PrimaryCanonicalJson;

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(source));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-016" &&
            diagnostic.Message.Contains(operation.ToString(), StringComparison.Ordinal) &&
            diagnostic.Message.Contains(ProjectionCardinality.Scalar.ToString(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollectionProjectionRejectsScalarAndMixedOperationDemand(bool mixed)
    {
        var operations = mixed
            ? new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.CollectionContains,
                PortableQueryOperation.Equal
            }
            : new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal };
        var fixture = CreateCollectionFixture(
            operations: operations,
            executionClass: BoundedQueryExecutionClass.Ordinary);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.CollectionElements));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-016" &&
            diagnostic.Message.Contains(ProjectionCardinality.CollectionElements.ToString(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PortableQueryOperation.CollectionContains)]
    [InlineData(PortableQueryOperation.CollectionContainsAll)]
    public void CollectionMembershipOperationsRemainValidForCollectionProjections(
        PortableQueryOperation operation)
    {
        var fixture = CreateCollectionFixture(
            operations: new HashSet<PortableQueryOperation> { operation });

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.CollectionElements)));

        Assert.Contains(operation, Assert.Single(plan.Predicates).Operations);
    }

    [Fact]
    public void CollectionMembershipOperationsRejectAResolvedScalarSource()
    {
        var fixture = CreateCollectionFixture(executionClass: BoundedQueryExecutionClass.Ordinary);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(
                PhysicalQuerySourceKind.PrimaryCanonicalJson,
                PhysicalQuerySourceKind.CollectionElements));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-016" &&
            diagnostic.Message.Contains(
                PhysicalQuerySourceKind.PrimaryCanonicalJson.ToString(),
                StringComparison.Ordinal) &&
            diagnostic.Message.Contains(
                ProjectionCardinality.CollectionElements.ToString(),
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollectionValuedOrderingIsRejectedAtCompileTime(bool explicitSort)
    {
        var fixture = CreateCollectionFixture(
            sortSupport: explicitSort ? QuerySortSupport.Both : QuerySortSupport.Ascending,
            sortFields: explicitSort
                ? [new BoundedQuerySortField("values", PhysicalSortDirection.Ascending)]
                : null);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.CollectionElements));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-014" &&
            diagnostic.Message.Contains("reconstruct-only", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(QueryPagingSupport.Cursor, false)]
    [InlineData(QueryPagingSupport.Offset, true)]
    public void CollectionMembershipPlanRejectsUncertifiedPagingAndLatestPerKeyShapes(
        QueryPagingSupport paging,
        bool latestPerKey)
    {
        var fixture = CreateCollectionFixture(pagingSupport: paging, latestPerKey: latestPerKey);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.CollectionElements));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-008" &&
            diagnostic.Message.Contains("collection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CollectionMembershipPlanRejectsLogicalAndPhysicalValueKindDrift()
    {
        var fixture = CreateCollectionFixture(
            logicalValueKind: IndexValueKind.String,
            physicalType: PortablePhysicalType.Int32);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.CollectionElements));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-009" &&
            diagnostic.Message.Contains("Int32", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollectionSelectedMutationsAreRejectedBeforeOwnerAndElementRowsCanDiverge(bool transition)
    {
        var action = transition
            ? BoundedMutationAction.Transition("values", ["a"], "b")
            : BoundedMutationAction.Delete();
        var fixture = CreateCollectionFixture(action: action);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.CollectionElements),
            supportsAtomicCollectionMaintenance: true);

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-MUTATION-007" &&
            diagnostic.Message.Contains("owner-and-element", StringComparison.Ordinal));
    }

    [Fact]
    public void CollectionBearingRouteRequiresProviderCertificationAndAdmitsCertifiedScalarDeletion()
    {
        var categoryIndex = new LogicalIndexDeclaration(
            "by-category",
            [new IndexField("category")],
            IndexValueKind.String,
            false,
            MissingValueBehavior.IncludedAsNull);
        var categoryQuery = new BoundedQueryDeclaration(
            "list-by-category",
            categoryIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.PhysicalEntityTable(
                "collection_mutation_entities",
                [
                    new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String),
                    new ProjectedColumnDefinition(
                        "tags",
                        "tags",
                        PortablePhysicalType.String,
                        Length: 128,
                        IsNullable: true,
                        Cardinality: ProjectionCardinality.CollectionElements,
                        MaxCollectionElements: 8)
                ],
                indexes:
                [
                    new PhysicalIndexDefinition(
                        categoryIndex.Identity,
                        [
                            new PhysicalIndexColumnDefinition("storage_scope", 0),
                            new PhysicalIndexColumnDefinition("category", 1),
                            new PhysicalIndexColumnDefinition("id_comparison_key", 2)
                        ])
                ])),
            [categoryIndex],
            [categoryQuery],
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "delete-by-category",
                    categoryQuery.Identity,
                    BoundedMutationAction.Delete())
            ]);
        var fixture = Resolve(storage, null);

        var uncertified = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));
        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns),
            supportsAtomicCollectionMaintenance: true);

        Assert.NotEmpty(fixture.Route.CollectionElementStorages);
        Assert.False(uncertified.IsValid);
        Assert.Empty(uncertified.Plans);
        Assert.Contains(uncertified.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-MUTATION-007" &&
            diagnostic.Message.Contains("provider has not certified", StringComparison.Ordinal));
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var plan = Assert.Single(result.Plans);
        Assert.Equal("delete-by-category", plan.MutationIdentity);
        Assert.IsType<PhysicalDeleteMutationAction>(plan.Action);
        Assert.NotEqual(PhysicalQueryAccessKind.CollectionElementsThenPrimary, plan.Predicate.AccessKind);
    }

    [Fact]
    public async Task CollectionRequestBoundUsesTypedSetSemanticsAndRejectsAmplificationBeforeDispatch()
    {
        var fixture = CreateCollectionFixture(
            logicalValueKind: IndexValueKind.Number,
            physicalType: PortablePhysicalType.Int32,
            maximumCollectionElements: 2);
        var capabilities = Capabilities(PhysicalQuerySourceKind.CollectionElements);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            capabilities));
        var acceptedHandler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.CollectionElements,
            certifications: [CertificationFor(plan)]);
        var acceptedStore = new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [acceptedHandler]);
        var atLimit = new DocumentQuery(
            "workflowTriggerBinding",
            "list-by-values",
            [DocumentQueryClause.Of(DocumentQueryComparison.CollectionContainsAll(
                "values",
                ["1", "1.0", "1e0", "2"]))]);

        await acceptedStore.QueryAsync(atLimit);

        Assert.Equal(plan, acceptedHandler.LastPlan);
        var rejectedHandler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.CollectionElements,
            certifications: [CertificationFor(plan)]);
        var rejectedStore = new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [rejectedHandler]);
        var overLimit = new DocumentQuery(
            "workflowTriggerBinding",
            "list-by-values",
            [DocumentQueryClause.Of(DocumentQueryComparison.CollectionContainsAll(
                "values",
                ["1", "1.0", "2", "3"]))]);

        var exception = await Assert.ThrowsAsync<PhysicalQueryRequestValidationException>(() =>
            rejectedStore.QueryAsync(overLimit));

        Assert.Equal("GW-QUERY-015", exception.Diagnostic.Code);
        Assert.Contains("compiled maximum of 2", exception.Message, StringComparison.Ordinal);
        Assert.Null(rejectedHandler.LastPlan);
        Assert.Throws<ArgumentException>(() =>
            DocumentQueryComparison.CollectionContainsAll("values", []));
    }

    [Fact]
    public async Task CollectionRequestBoundAggregatesTypedValuesAcrossClausesBeforeDispatch()
    {
        var fixture = CreateCollectionFixture(
            logicalValueKind: IndexValueKind.Number,
            physicalType: PortablePhysicalType.Int32,
            maximumCollectionElements: 2);
        var capabilities = Capabilities(PhysicalQuerySourceKind.CollectionElements);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            capabilities));
        var handler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.CollectionElements,
            certifications: [CertificationFor(plan)]);
        var store = new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]);
        var overLimit = new DocumentQuery(
            "workflowTriggerBinding",
            "list-by-values",
            Enumerable.Range(0, 3)
                .Select(offset => DocumentQueryClause.Of(DocumentQueryComparison.CollectionContainsAll(
                    "values",
                    [$"{(offset * 2) + 1}", $"{(offset * 2) + 2}"])))
                .ToArray());

        var exception = await Assert.ThrowsAsync<PhysicalQueryRequestValidationException>(() =>
            store.QueryAsync(overLimit));

        Assert.Equal("GW-QUERY-015", exception.Diagnostic.Code);
        Assert.Contains("requests 6 distinct typed values", exception.Message, StringComparison.Ordinal);
        Assert.Null(handler.LastPlan);

        var atLimit = new DocumentQuery(
            "workflowTriggerBinding",
            "list-by-values",
            [
                DocumentQueryClause.Of(DocumentQueryComparison.CollectionContains("values", "1")),
                DocumentQueryClause.Of(DocumentQueryComparison.CollectionContains("values", "1.0")),
                DocumentQueryClause.Of(DocumentQueryComparison.CollectionContainsAll("values", ["1e0", "2"]))
            ]);

        await store.QueryAsync(atLimit);

        Assert.Equal(plan, handler.LastPlan);
    }

    [Fact]
    public void CompoundPrefixDirectionAndIdentityTieBreakAreDeterministic()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-stimulus-created",
            [new IndexField("stimulusType"), new IndexField("createdAt", IndexValueKind.DateTime)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var query = new BoundedQueryDeclaration(
            "latest-by-stimulus-type",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Descending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields: [new BoundedQuerySortField("createdAt", PhysicalSortDirection.Descending)],
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "stimulusType",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ],
            resultOperations: new HashSet<BoundedQueryResultOperation>
            {
                BoundedQueryResultOperation.Documents,
                BoundedQueryResultOperation.First
            });
        var fixture = CreateEntityFixture(logicalIndex, query);

        var first = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));
        var second = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));

        Assert.Collection(
            first.Order,
            order =>
            {
                Assert.Equal("createdAt", order.Path);
                Assert.Equal(PhysicalSortDirection.Descending, order.Direction);
                Assert.False(order.IsIdentityTieBreak);
            },
            order =>
            {
                Assert.Equal("storageScope", order.Path);
                Assert.Equal(PhysicalSortDirection.Ascending, order.Direction);
                Assert.True(order.IsIdentityTieBreak);
            },
            order =>
            {
                Assert.Equal("id", order.Path);
                Assert.Equal(PhysicalSortDirection.Ascending, order.Direction);
                Assert.True(order.IsIdentityTieBreak);
            });
        Assert.Equal(first, second);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<PhysicalQueryOrder>>(first.Order).Clear());
    }

    [Fact]
    public void RuntimeOrderPrefixRetainsTheRemainingDeclaredCompoundOrderBeforeTieBreaks()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-stimulus-created",
            [new IndexField("stimulusType"), new IndexField("createdAt", IndexValueKind.DateTime)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var declaration = new BoundedQueryDeclaration(
            "list-by-stimulus-created",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Both,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields:
            [
                new BoundedQuerySortField("stimulusType", PhysicalSortDirection.Ascending),
                new BoundedQuerySortField("createdAt", PhysicalSortDirection.Descending)
            ]);
        var fixture = CreateEntityFixture(logicalIndex, declaration);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));
        var query = new DocumentQuery(
            "workflowTriggerBinding",
            declaration.Identity,
            order: [new DocumentQueryOrder("stimulusType", PhysicalSortDirection.Ascending)]);

        var resolved = DocumentQueryOrderResolver.Resolve(query, plan);

        Assert.Equal(
            ["stimulusType", "createdAt", "storageScope", "id"],
            resolved.Select(order => order.Path));
        Assert.Equal(
            [
                PhysicalSortDirection.Ascending,
                PhysicalSortDirection.Descending,
                PhysicalSortDirection.Ascending,
                PhysicalSortDirection.Ascending
            ],
            resolved.Select(order => order.Direction));
    }

    [Fact]
    public void LatestPerKeyAndKeysetMustBeServedByDeclaredProviderHandlers()
    {
        var query = Query(
            BoundedQueryExecutionClass.Ordinary,
            pagingSupport: QueryPagingSupport.Cursor,
            latestPerKeyPath: "stimulusType",
            sortFields: [new BoundedQuerySortField("stimulusType", PhysicalSortDirection.Descending)]);
        var fixture = CreateFixture(PhysicalStorageForm.DedicatedDocumentTable, query);
        var unsupported = CapabilitiesWithPaging(
            supportsKeysetPaging: false,
            supportsLatestPerKey: false,
            sources: [PhysicalQuerySourceKind.PrimaryCanonicalJson]);

        var result = PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, unsupported);

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-007");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-008");
    }

    [Fact]
    public void LatestPerKeyRejectsCursorPagingEvenWhenTheProviderSupportsBothCapabilitiesSeparately()
    {
        var query = Query(
            BoundedQueryExecutionClass.Ordinary,
            pagingSupport: QueryPagingSupport.Cursor,
            latestPerKeyPath: "stimulusType",
            sortFields: [new BoundedQuerySortField("stimulusType", PhysicalSortDirection.Descending)]);
        var fixture = CreateFixture(PhysicalStorageForm.DedicatedDocumentTable, query);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            CapabilitiesWithPaging(
                supportsKeysetPaging: true,
                supportsLatestPerKey: true,
                sources: [PhysicalQuerySourceKind.PrimaryCanonicalJson]));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-008" &&
            diagnostic.Message.Contains("cursor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LatestPerKeyRequiresTheGroupingPathToLeadTheDeclaredOrder()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-category-created-stimulus",
            [
                new IndexField("category"),
                new IndexField("createdAt", IndexValueKind.DateTime),
                new IndexField("stimulusType")
            ],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var query = new BoundedQueryDeclaration(
            "latest-by-stimulus",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Both,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.Ordinary,
            sortFields:
            [
                new BoundedQuerySortField("createdAt", PhysicalSortDirection.Ascending),
                new BoundedQuerySortField("stimulusType", PhysicalSortDirection.Ascending)
            ],
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "category",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ],
            latestPerKeyPath: "stimulusType");
        var fixture = CreateEntityFixture(logicalIndex, query);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            CapabilitiesWithPaging(
                supportsKeysetPaging: true,
                supportsLatestPerKey: true,
                sources: [PhysicalQuerySourceKind.PrimaryCanonicalJson]));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-008" &&
            diagnostic.Message.Contains("lead", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScaleBearingQueryWithoutExecutableIndexedRouteFailsBeforeTraffic()
    {
        var fixture = CreateFixture(PhysicalStorageForm.DedicatedDocumentTable, BoundedQueryExecutionClass.Ordinary);
        var scaleBearing = Query(BoundedQueryExecutionClass.ScaleBearing);
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            [scaleBearing]);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-005");
    }

    [Fact]
    public void ScaleBearingQueryMustBeBoundToTheCompiledRouteFingerprintInput()
    {
        var logicalIndex = StimulusTypeIndex();
        var routedQuery = Query(BoundedQueryExecutionClass.ScaleBearing);
        var fixture = CreateEntityFixture(logicalIndex, routedQuery);
        var staleQuery = new BoundedQueryDeclaration(
            "renamed-after-route-compilation",
            routedQuery.IndexIdentity,
            routedQuery.Operations,
            routedQuery.SortSupport,
            routedQuery.PagingSupport,
            routedQuery.ExecutionClass,
            routedQuery.SupportsDisjunction,
            routedQuery.SupportsTotalCount,
            routedQuery.SortFields,
            routedQuery.PredicateBindingMode == BoundedQueryPredicateBindingMode.ImplicitFirstLogicalIndexField
                ? null
                : routedQuery.PredicateFields,
            routedQuery.ResultOperations,
            routedQuery.LatestPerKeyPath);
        var staleStorage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            [staleQuery]);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            staleStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-005");
    }

    [Fact]
    public void UnsupportedCompoundPrefixIsRejectedInsteadOfUsingClientFallback()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-stimulus-created",
            [new IndexField("stimulusType"), new IndexField("createdAt", IndexValueKind.DateTime)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var query = new BoundedQueryDeclaration(
            "valid-prefix",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "createdAt",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var validQuery = new BoundedQueryDeclaration(
            "valid-prefix",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "stimulusType",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var fixture = CreateEntityFixture(logicalIndex, validQuery);
        var invalidStorage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            [query]);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            invalidStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-006");
    }

    [Fact]
    public void OrderedCompoundSuffixRejectsRangePrefix()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-stimulus-created",
            [new IndexField("stimulusType"), new IndexField("createdAt", IndexValueKind.DateTime)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var routedQuery = new BoundedQueryDeclaration(
            "range-prefix",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Descending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields: [new BoundedQuerySortField("createdAt", PhysicalSortDirection.Descending)],
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "stimulusType",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var fixture = CreateEntityFixture(logicalIndex, routedQuery);
        var rangeQuery = new BoundedQueryDeclaration(
            routedQuery.Identity,
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.GreaterThan },
            routedQuery.SortSupport,
            routedQuery.PagingSupport,
            routedQuery.ExecutionClass,
            sortFields: routedQuery.SortFields,
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "stimulusType",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.GreaterThan })
            ]);
        var invalidStorage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            [rangeQuery]);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            invalidStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-006");
    }

    [Fact]
    public void OrderedCompoundRangeBoundaryAcceptsEqualityPrefixAndRequiresOnlyThatPrefix()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-tenant-category-created-id",
            [
                new IndexField("tenant"),
                new IndexField("category"),
                new IndexField("createdAt", IndexValueKind.DateTime),
                new IndexField("itemId")
            ],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var query = new BoundedQueryDeclaration(
            "page-by-tenant-category-created",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.GreaterThan,
                PortableQueryOperation.LessThanOrEqual
            },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields:
            [
                new BoundedQuerySortField("createdAt", PhysicalSortDirection.Ascending),
                new BoundedQuerySortField("itemId", PhysicalSortDirection.Ascending)
            ],
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "tenant",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }),
                new BoundedQueryPredicateField(
                    "category",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }),
                new BoundedQueryPredicateField(
                    "createdAt",
                    new HashSet<PortableQueryOperation>
                    {
                        PortableQueryOperation.GreaterThan,
                        PortableQueryOperation.LessThanOrEqual
                    })
            ]);
        var fixture = CreateEntityFixture(logicalIndex, query);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));

        Assert.Equal(["tenant", "category"], plan.RequiredEqualityPrefixPaths);
        Assert.Equal(["createdAt", "itemId"], plan.Order.Take(2).Select(order => order.Path));
    }

    [Fact]
    public void OrderedCompoundRangeBoundaryRejectsNonEqualityBeforeTheOverlap()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-tenant-category-created-id",
            [
                new IndexField("tenant"),
                new IndexField("category"),
                new IndexField("createdAt", IndexValueKind.DateTime),
                new IndexField("itemId")
            ],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var validQuery = new BoundedQueryDeclaration(
            "page-by-tenant-category-created",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.GreaterThan,
                PortableQueryOperation.LessThanOrEqual
            },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields:
            [
                new BoundedQuerySortField("createdAt", PhysicalSortDirection.Ascending),
                new BoundedQuerySortField("itemId", PhysicalSortDirection.Ascending)
            ],
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "tenant",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }),
                new BoundedQueryPredicateField(
                    "category",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }),
                new BoundedQueryPredicateField(
                    "createdAt",
                    new HashSet<PortableQueryOperation>
                    {
                        PortableQueryOperation.GreaterThan,
                        PortableQueryOperation.LessThanOrEqual
                    })
            ]);
        var fixture = CreateEntityFixture(logicalIndex, validQuery);
        var invalidQuery = new BoundedQueryDeclaration(
            validQuery.Identity,
            validQuery.IndexIdentity,
            validQuery.Operations,
            validQuery.SortSupport,
            validQuery.PagingSupport,
            validQuery.ExecutionClass,
            sortFields: validQuery.SortFields,
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "tenant",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.GreaterThan }),
                new BoundedQueryPredicateField(
                    "category",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }),
                new BoundedQueryPredicateField(
                    "createdAt",
                    new HashSet<PortableQueryOperation>
                    {
                        PortableQueryOperation.GreaterThan,
                        PortableQueryOperation.LessThanOrEqual
                    })
            ]);
        var invalidStorage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            [invalidQuery]);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            invalidStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-006");
    }

    [Fact]
    public async Task RuntimeSuffixOrderingRequiresOneStandaloneEqualityForEverySkippedPrefix()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-stimulus-created",
            [new IndexField("stimulusType"), new IndexField("createdAt", IndexValueKind.DateTime)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var query = new BoundedQueryDeclaration(
            "latest-by-stimulus-type",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Descending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields: [new BoundedQuerySortField("createdAt", PhysicalSortDirection.Descending)],
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "stimulusType",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var fixture = CreateEntityFixture(logicalIndex, query);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, capabilities));
        var handler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryProjectedColumns,
            certifications: [CertificationFor(plan)]);
        var store = new PhysicalQueryDocumentStore(fixture.Route, fixture.Storage, capabilities, [handler]);
        var missingPrefix = new DocumentQuery(
            "workflowTriggerBinding",
            query.Identity,
            order: [new DocumentQueryOrder("createdAt", PhysicalSortDirection.Descending)],
            take: 25);
        var disjunctivePrefix = new DocumentQuery(
            "workflowTriggerBinding",
            query.Identity,
            [DocumentQueryClause.AnyOf(
                DocumentQueryComparison.Equal("stimulusType", "http"),
                DocumentQueryComparison.Equal("stimulusType", "timer"))],
            [new DocumentQueryOrder("createdAt", PhysicalSortDirection.Descending)],
            take: 25);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueryAsync(missingPrefix));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueryAsync(disjunctivePrefix));

        await store.QueryAsync(new DocumentQuery(
            "workflowTriggerBinding",
            query.Identity,
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))],
            [new DocumentQueryOrder("createdAt", PhysicalSortDirection.Descending)],
            take: 25));
    }

    [Fact]
    public void NativeScopeAndDiscriminatorUseNativeFieldMetadata()
    {
        var fixture = CreateFixture(PhysicalStorageForm.PhysicalEntityTable, BoundedQueryExecutionClass.ScaleBearing);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.NativeDocumentFields)));

        Assert.Equal(PhysicalQueryFieldSource.NativeDocumentField, plan.Scope.Field.Source);
        Assert.Equal(PhysicalQueryFieldSource.NativeDocumentField, plan.Discriminator.Source);
    }

    [Fact]
    public void LegacyBridgeRejectsCompoundStablePathsInsteadOfCollapsingThemToOneIndexIdentity()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-stimulus-created",
            [new IndexField("stimulusType"), new IndexField("createdAt", IndexValueKind.DateTime)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var queryDeclaration = new BoundedQueryDeclaration(
            "search-by-stimulus-created",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.Ordinary,
            predicateFields:
            [
                new BoundedQueryPredicateField("stimulusType", new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }),
                new BoundedQueryPredicateField("createdAt", new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var fixture = CreateEntityFixture(logicalIndex, queryDeclaration);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson)));
        var exception = Assert.Throws<ArgumentException>(() => new LegacyPortableDocumentQueryHandler(
            plan.HandlerIdentity,
            new CapturingDocumentStore(),
            [CertificationFor(plan)]));

        Assert.Contains("single-field", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyBridgeMapsOneStablePathAndPreservesThePlannedDefaultOrder()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, capabilities));
        var legacyStore = new CapturingDocumentStore();
        var handler = new LegacyPortableDocumentQueryHandler(
            plan.HandlerIdentity,
            legacyStore,
            [CertificationFor(plan)]);
        var query = new DocumentQuery(
            "workflowTriggerBinding",
            plan.QueryIdentity,
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))]);

        await handler.QueryAsync(query, plan, CancellationToken.None);

#pragma warning disable GW0004
        var bridged = Assert.IsType<PortableDocumentQuery>(legacyStore.LastQuery);
#pragma warning restore GW0004
        Assert.Equal(plan.LogicalIndexIdentity, Assert.Single(Assert.Single(bridged.Clauses).Comparisons).IndexName);
        Assert.Equal(plan.LogicalIndexIdentity, bridged.Order!.IndexName);
        Assert.False(bridged.Order.Descending);
    }
}
