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
using System.Text.Json;
using Xunit;

namespace Groundwork.Tests;

public sealed partial class PhysicalQueryPlanCompilerTests
{
    [Fact]
    public void NamedDeleteMutationCompilesFromAClosedBoundedPredicateAndInheritsRouteScope()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var mutation = new BoundedMutationDeclaration(
            "prune-by-stimulus-type",
            "list-by-stimulus-type",
            BoundedMutationAction.Delete());
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            fixture.Storage.BoundedQueries,
            fixture.Storage.NameOverrides,
            boundedMutations: [mutation]);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        var plan = Assert.Single(result.Plans);
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        Assert.Equal("prune-by-stimulus-type", plan.MutationIdentity);
        Assert.Equal(BoundedMutationActionKind.Delete, plan.Action.Kind);
        Assert.Equal("list-by-stimulus-type", plan.Predicate.QueryIdentity);
        Assert.Equal("test.PrimaryProjectedColumns", plan.HandlerIdentity);
        Assert.Equal(fixture.Route.Fingerprint, plan.RouteFingerprint);
        Assert.Equal(fixture.Route.ScopePolicy, plan.Predicate.Scope.Policy);
        Assert.True(plan.Predicate.Scope.IsMandatory);
    }

    [Fact]
    public void NamedDeleteMutationRejectsAnExplicitlyUnfilteredPredicate()
    {
        var index = new LogicalIndexDeclaration(
            "by-id",
            [new IndexField(PhysicalDocumentFieldPaths.Id)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var query = new BoundedQueryDeclaration(
            "list-all",
            index.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields: [new BoundedQuerySortField(PhysicalDocumentFieldPaths.Id, PhysicalSortDirection.Ascending)],
            predicateFields: []);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "global_documents",
            [new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String)],
            indexes:
            [
                new PhysicalIndexDefinition(
                    index.Identity,
                    [new PhysicalIndexColumnDefinition("id_comparison_key", 0)])
            ]);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [index],
            [query],
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "unsafe-delete-all",
                    query.Identity,
                    BoundedMutationAction.Delete())
            ]);
        var fixture = Resolve(storage, binding: null, tenancy: TenancyPolicy.Global);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(
            result.Diagnostics,
            diagnostic =>
                diagnostic.Code == "GW-MUTATION-008" &&
                diagnostic.Message.Contains("unfiltered", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NamedTransitionFixesTheAllowedSourceAndTargetValuesAtCompileTime()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var mutation = new BoundedMutationDeclaration(
            "revoke-http-stimuli",
            "list-by-stimulus-type",
            BoundedMutationAction.Transition(
                "stimulusType",
                ["active", "inactive"],
                "revoked"));
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            fixture.Storage.BoundedQueries,
            fixture.Storage.NameOverrides,
            boundedMutations: [mutation]);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        var transition = Assert.IsType<PhysicalTransitionMutationAction>(Assert.Single(result.Plans).Action);
        Assert.Equal("stimulusType", transition.Path);
        Assert.Equal(new[] { "active", "inactive" }, transition.AllowedSourceValues);
        Assert.Equal("revoked", transition.TargetValue);
        Assert.Equal(IndexValueKind.Keyword, transition.Field.ValueKind);
        Assert.Equal(Assert.Single(fixture.Route.ProjectedColumns).Column.Identifier, transition.Field.Identifier);
    }

    [Fact]
    public void NamedAssignmentFixesAProjectedTargetIndependentlyOfTheSelectionPredicate()
    {
        var fixture = CreateAssignmentFixture();

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        var assignment = Assert.IsType<PhysicalAssignMutationAction>(Assert.Single(result.Plans).Action);
        Assert.Equal(BoundedMutationActionKind.Assign, assignment.Kind);
        Assert.Equal("status", assignment.Path);
        Assert.Equal("revoked", assignment.TargetValue);
        Assert.Equal(IndexValueKind.Keyword, assignment.Field.ValueKind);
        Assert.Equal(
            fixture.Route.ProjectedColumns.Single(column => column.Definition.Path == "status").Column.Identifier,
            assignment.Field.Identifier);
        Assert.Equal("list-by-stimulus-type", Assert.Single(result.Plans).Predicate.QueryIdentity);
    }

    [Fact]
    public void AssignmentDeclarationHasClosedValueSemantics()
    {
        var assignment = BoundedMutationAction.Assign("status", "revoked");
        var equivalent = BoundedMutationAction.Assign("status", "revoked");

        Assert.Equal(assignment, equivalent);
        Assert.Equal(assignment.GetHashCode(), equivalent.GetHashCode());
        Assert.NotEqual(assignment, BoundedMutationAction.Assign("status", "disabled"));
        Assert.NotEqual(assignment, BoundedMutationAction.Assign("state", "revoked"));
        Assert.NotEqual(
            assignment,
            BoundedMutationAction.Transition("status", ["pending"], "revoked"));
        Assert.Throws<ArgumentException>(() => BoundedMutationAction.Assign("", "revoked"));
        Assert.Throws<ArgumentException>(() => BoundedMutationAction.Assign("status", ""));
    }

    [Fact]
    public void AssignmentDeclarationRoundTripsThroughThePolymorphicManifestContract()
    {
        BoundedMutationAction assignment = BoundedMutationAction.Assign("status", "revoked");

        var json = JsonSerializer.Serialize(assignment);
        var roundTripped = JsonSerializer.Deserialize<BoundedMutationAction>(json);

        Assert.Equal(assignment, roundTripped);
        Assert.Contains("\"$action\":\"assign\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AssignmentIsRejectedBeforeProviderIoWhenTheHandlerDoesNotAdvertiseIt()
    {
        var fixture = CreateAssignmentFixture();
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns);
        var plan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            capabilities).Plans);
        var handler = new RecordingMutationHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryProjectedColumns,
            [new PhysicalMutationHandlerCertification(plan)],
            new HashSet<BoundedMutationActionKind>
            {
                BoundedMutationActionKind.Delete,
                BoundedMutationActionKind.Transition
            });

        Assert.Throws<ArgumentException>(() => new PhysicalMutationDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]));
        Assert.Equal(0, handler.ExecutionCount);
    }

    [Theory]
    [InlineData(false, ProjectionCardinality.Scalar)]
    [InlineData(true, ProjectionCardinality.CollectionElements)]
    public void NamedAssignmentRejectsMissingOrCollectionTargetProjection(
        bool includeTarget,
        ProjectionCardinality cardinality)
    {
        var fixture = CreateAssignmentFixture(includeTarget, cardinality);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == (includeTarget ? "GW-MUTATION-007" : "GW-MUTATION-009"));
    }

    [Theory]
    [InlineData(PortablePhysicalType.Binary)]
    [InlineData(PortablePhysicalType.Json)]
    public void NamedAssignmentRejectsUnsupportedProjectedTypes(PortablePhysicalType targetType)
    {
        var fixture = CreateAssignmentFixture(targetType: targetType);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-MUTATION-009" &&
            diagnostic.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PhysicalDocumentFieldPaths.Id)]
    [InlineData(PhysicalDocumentFieldPaths.DocumentKind)]
    [InlineData(PhysicalDocumentFieldPaths.StorageScope)]
    [InlineData(PhysicalDocumentFieldPaths.Version)]
    [InlineData(PhysicalDocumentFieldPaths.SchemaVersion)]
    public void NamedAssignmentRejectsEnvelopePathsEvenWhenAProjectionSharesThePath(string targetPath)
    {
        var fixture = CreateAssignmentFixture(targetPath: targetPath);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-MUTATION-005" &&
            diagnostic.Message.Contains("content", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssignmentRequestFingerprintBindsTheManifestFixedTargetValue()
    {
        var revoked = CreateAssignmentFixture(targetValue: "revoked");
        var disabled = CreateAssignmentFixture(targetValue: "disabled");
        var revokedPlan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            revoked.Route,
            revoked.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)).Plans);
        var disabledPlan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            disabled.Route,
            disabled.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)).Plans);
        var request = new DocumentMutation(
            "workflowTriggerBinding",
            "assign-status",
            "operation-a",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))]);

        Assert.NotEqual(
            BoundedMutationRequestFingerprint.Create(request, revokedPlan, "tenant-a"),
            BoundedMutationRequestFingerprint.Create(request, disabledPlan, "tenant-a"));
    }

    [Fact]
    public void MutationRequestFingerprintRemainsCompatibleForAssignmentTransitionAndDelete()
    {
        var fixture = CreateAssignmentFixture();
        var actions = new BoundedMutationAction[]
        {
            BoundedMutationAction.Assign("status", "revoked"),
            BoundedMutationAction.Transition("stimulusType", ["http"], "revoked"),
            BoundedMutationAction.Delete()
        };
        var plans = actions.Select(action =>
        {
            var storage = new StorageUnitPhysicalStorage(
                fixture.Storage.ProvisioningMode,
                fixture.Storage.Policy,
                fixture.Storage.LogicalIndexes,
                fixture.Storage.BoundedQueries,
                fixture.Storage.NameOverrides,
                boundedMutations:
                [
                    new BoundedMutationDeclaration(
                        "mutate",
                        "list-by-stimulus-type",
                        action)
                ]);
            return Assert.Single(PhysicalMutationPlanCompiler.Compile(
                fixture.Route,
                storage,
                Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)).Plans);
        }).ToArray();
        var request = new DocumentMutation(
            "workflowTriggerBinding",
            "mutate",
            "operation-a",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))]);

        var fingerprints = plans
            .Select(plan => BoundedMutationRequestFingerprint.Create(request, plan, "tenant-a"))
            .ToArray();

        // These moved once, deliberately: MissingValueBehavior participates in the plan shape and the
        // fixture's index now declares the default IncludedAsNull rather than Excluded. The values are
        // pinned to catch accidental drift, so any future change to them wants the same justification.
        Assert.Equal(
            new[]
            {
                "e9720b8a527b512f5d7fbcfda3e4f835c4b2fa7dfafb21231b621a77fed36651",
                "6bcd4fd6febc6b1e08826b568dd53cc47c13fe3734b8cb0d73a44d4e35672116",
                "c15968fc09bc28f5979d4169fc7f191d3668411022bbdd68f9c020ab6a682395"
            },
            fingerprints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamedTransitionRejectsEnvelopeAndLinkedRelationshipFields(bool linked)
    {
        var fixture = CreateIntrinsicMutationFixture(
            linked,
            BoundedMutationAction.Transition(linked ? "id" : "schemaVersion", ["1"], "2"));

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(linked ? PhysicalQuerySourceKind.LinkedIndex : PhysicalQuerySourceKind.PrimaryEnvelope));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-MUTATION-005" &&
            diagnostic.Message.Contains("content", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false, PhysicalQueryFieldSource.Envelope)]
    [InlineData(true, PhysicalQueryFieldSource.LinkedRelationship)]
    public void NamedDeleteRetainsEnvelopeAndLinkedRelationshipPredicates(
        bool linked,
        PhysicalQueryFieldSource expectedSource)
    {
        var fixture = CreateIntrinsicMutationFixture(linked, BoundedMutationAction.Delete());

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(linked ? PhysicalQuerySourceKind.LinkedIndex : PhysicalQuerySourceKind.PrimaryEnvelope));

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        var plan = Assert.Single(result.Plans);
        Assert.IsType<PhysicalDeleteMutationAction>(plan.Action);
        Assert.Equal(expectedSource, Assert.Single(plan.Predicate.Predicates).Field.Source);
    }

    [Fact]
    public void MutationCompilationRejectsAnOrdinaryPredicateThatCouldScanWithoutAnIndex()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            fixture.Storage.BoundedQueries,
            fixture.Storage.NameOverrides,
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "unsafe-prune",
                    "list-by-stimulus-type",
                    BoundedMutationAction.Delete())
            ]);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-MUTATION-004" &&
            diagnostic.Message.Contains("indexed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MutationRuntimeRejectsUndeclaredWorkBeforeDispatchingProviderIo()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            fixture.Storage.BoundedQueries,
            fixture.Storage.NameOverrides,
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "prune-by-stimulus-type",
                    "list-by-stimulus-type",
                    BoundedMutationAction.Delete())
            ]);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns);
        var plan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            capabilities).Plans);
        var handler = new RecordingMutationHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryProjectedColumns,
            [new PhysicalMutationHandlerCertification(plan)]);
        var mutations = new PhysicalMutationDocumentStore(
            fixture.Route,
            storage,
            capabilities,
            [handler]);

        var completed = await mutations.ExecuteAsync(new DocumentMutation(
            "workflowTriggerBinding",
            "prune-by-stimulus-type",
            "operation-1",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))]));

        Assert.Equal(BoundedMutationStatus.Completed, completed.Status);
        Assert.Equal(3, completed.AffectedCount);
        Assert.Equal(1, handler.ExecutionCount);
        Assert.Equal(plan, mutations.ResolvePlan(new DocumentMutation(
            "workflowTriggerBinding",
            "prune-by-stimulus-type",
            "inspection-only",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))])));
        await Assert.ThrowsAsync<NotSupportedException>(() => mutations.ExplainAsync(new DocumentMutation(
            "workflowTriggerBinding",
            "prune-by-stimulus-type",
            "missing-evidence",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))])));

        var request = new DocumentMutation(
            "workflowTriggerBinding",
            "prune-by-stimulus-type",
            "fingerprint-request",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))]);
        var drifted = new DocumentMutation(
            "workflowTriggerBinding",
            "prune-by-stimulus-type",
            "fingerprint-request",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "timer"))]);
        var primary = new PhysicalDocumentMutationSelectorEvidence(
            ExecutableStorageObjectRole.PrimaryStorage,
            plan.Predicate.PrimaryObject,
            plan.Predicate.IndexName!,
            plan.Predicate.PrimaryObject.Identifier,
            plan.Predicate.IndexName!.Identifier);
        PhysicalMutationDocumentStore EvidenceRuntime(
            DocumentMutation fingerprintSource,
            IReadOnlyList<PhysicalDocumentMutationSelectorEvidence> selectors) =>
            new(
                fixture.Route,
                storage,
                capabilities,
                [handler],
                (_, admitted, _) => Task.FromResult(new PhysicalDocumentMutationExplanation(
                    admitted,
                    BoundedMutationRequestFingerprint.Create(fingerprintSource, admitted, "scope-a"),
                    "test-plan",
                    "native-plan",
                    selectors)),
                (mutation, admitted) => BoundedMutationRequestFingerprint.Create(mutation, admitted, "scope-a"));

        Assert.Equal(plan, (await EvidenceRuntime(request, [primary]).ExplainAsync(request)).Plan);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EvidenceRuntime(drifted, [primary]).ExplainAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EvidenceRuntime(request, []).ExplainAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EvidenceRuntime(request, [primary, primary]).ExplainAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EvidenceRuntime(request,
            [
                new PhysicalDocumentMutationSelectorEvidence(
                    ExecutableStorageObjectRole.LinkedIndexStorage,
                    plan.Predicate.PrimaryObject,
                    plan.Predicate.IndexName!,
                    plan.Predicate.PrimaryObject.Identifier,
                    plan.Predicate.IndexName!.Identifier)
            ]).ExplainAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.ExecuteAsync(new DocumentMutation(
            "workflowTriggerBinding",
            "undeclared-prune",
            "operation-2")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.ExecuteAsync(new DocumentMutation(
            "workflowTriggerBinding",
            "prune-by-stimulus-type",
            "operation-3",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("undeclaredPath", "http"))])));
        Assert.Equal(1, handler.ExecutionCount);
    }

    [Fact]
    public async Task LinkedMutationEvidenceRequiresExactlyPrimaryAndLinkedSelectors()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            fixture.Storage.BoundedQueries,
            fixture.Storage.NameOverrides,
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "prune-by-stimulus-type",
                    "list-by-stimulus-type",
                    BoundedMutationAction.Delete())
            ]);
        var capabilities = Capabilities(PhysicalQuerySourceKind.LinkedIndex);
        var plan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            capabilities).Plans);
        var handler = new RecordingMutationHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.LinkedIndex,
            [new PhysicalMutationHandlerCertification(plan)]);
        var request = new DocumentMutation(
            "workflowTriggerBinding",
            "prune-by-stimulus-type",
            "linked-evidence",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))]);
        var primary = new PhysicalDocumentMutationSelectorEvidence(
            ExecutableStorageObjectRole.PrimaryStorage,
            plan.Predicate.PrimaryObject,
            index: null,
            plan.Predicate.PrimaryObject.Identifier,
            "provider-primary-index");
        var linked = new PhysicalDocumentMutationSelectorEvidence(
            ExecutableStorageObjectRole.LinkedIndexStorage,
            plan.Predicate.LookupObject,
            plan.Predicate.IndexName!,
            plan.Predicate.LookupObject.Identifier,
            plan.Predicate.IndexName!.Identifier);
        var stagedHandler = new RecordingMutationHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.LinkedIndex,
            [
                new PhysicalMutationHandlerCertification(
                    plan,
                    evidenceStages:
                    [
                        new PhysicalMutationEvidenceStageCertification(
                            PhysicalDocumentMutationCommandKind.CandidateDiscovery,
                            PhysicalDocumentMutationCommandIdentities.CandidateDiscovery,
                            [
                                new PhysicalMutationSelectorCertification(
                                    ExecutableStorageObjectRole.LinkedIndexStorage,
                                    plan.Predicate.LookupObject,
                                    plan.Predicate.IndexName!,
                                    new Dictionary<string, string>())
                            ]),
                        new PhysicalMutationEvidenceStageCertification(
                            PhysicalDocumentMutationCommandKind.PredicateRecheck,
                            PhysicalDocumentMutationCommandIdentities.PredicateRecheck,
                            [
                                new PhysicalMutationSelectorCertification(
                                    ExecutableStorageObjectRole.PrimaryStorage,
                                    plan.Predicate.PrimaryObject,
                                    index: null,
                                    new Dictionary<string, string>()),
                                new PhysicalMutationSelectorCertification(
                                    ExecutableStorageObjectRole.LinkedIndexStorage,
                                    plan.Predicate.LookupObject,
                                    plan.Predicate.IndexName!,
                                    new Dictionary<string, string>())
                            ])
                    ])
            ]);
        PhysicalMutationDocumentStore Runtime(
            IReadOnlyList<PhysicalDocumentMutationSelectorEvidence> selectors) =>
            new(
                fixture.Route,
                storage,
                capabilities,
                [handler],
                (_, admitted, _) => Task.FromResult(new PhysicalDocumentMutationExplanation(
                    admitted,
                    BoundedMutationRequestFingerprint.Create(request, admitted, "scope-a"),
                    "test-plan",
                    "native-plan",
                    selectors)),
                (mutation, admitted) => BoundedMutationRequestFingerprint.Create(mutation, admitted, "scope-a"));

        Assert.Equal(2, (await Runtime([primary, linked]).ExplainAsync(request)).Selectors.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runtime([primary]).ExplainAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runtime([linked]).ExplainAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Runtime([primary, linked, linked]).ExplainAsync(request));

        PhysicalDocumentMutationCommandExplanation Command(
            string identity,
            IReadOnlyList<PhysicalDocumentMutationSelectorEvidence> selectors) =>
            new(
                identity == PhysicalDocumentMutationCommandIdentities.CandidateDiscovery
                    ? PhysicalDocumentMutationCommandKind.CandidateDiscovery
                    : PhysicalDocumentMutationCommandKind.PredicateRecheck,
                identity,
                "test-plan",
                "native-plan",
                selectors,
                $"SELECT /* {identity} */");
        PhysicalMutationDocumentStore StagedRuntime(
            IReadOnlyList<PhysicalDocumentMutationCommandExplanation> commands,
            IPhysicalDocumentMutationHandler? certifiedHandler = null) =>
            new(
                fixture.Route,
                storage,
                capabilities,
                [certifiedHandler ?? stagedHandler],
                (_, admitted, _) => Task.FromResult(new PhysicalDocumentMutationExplanation(
                    admitted,
                    BoundedMutationRequestFingerprint.Create(request, admitted, "scope-a"),
                    commands)),
                (mutation, admitted) => BoundedMutationRequestFingerprint.Create(mutation, admitted, "scope-a"));
        var discovery = Command(
            PhysicalDocumentMutationCommandIdentities.CandidateDiscovery,
            [linked]);
        var recheck = Command(
            PhysicalDocumentMutationCommandIdentities.PredicateRecheck,
            [primary, linked]);

        Assert.Equal(2, (await StagedRuntime([discovery, recheck]).ExplainAsync(request)).Commands.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StagedRuntime([discovery, recheck], handler).ExplainAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StagedRuntime([
                new PhysicalDocumentMutationCommandExplanation(
                    PhysicalDocumentMutationCommandKind.Selection,
                    PhysicalDocumentMutationCommandIdentities.Selection,
                    "test-plan",
                    "native-plan",
                    [primary, linked])
            ]).ExplainAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StagedRuntime([discovery]).ExplainAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StagedRuntime([recheck, discovery]).ExplainAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StagedRuntime([
                Command(PhysicalDocumentMutationCommandIdentities.CandidateDiscovery, [primary]),
                recheck
            ]).ExplainAsync(request));
    }

    [Fact]
    public void MutationRequestFingerprintIsDeterministicForEquivalentSetPredicates()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            fixture.Storage.BoundedQueries,
            fixture.Storage.NameOverrides,
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "prune-by-stimulus-type",
                    "list-by-stimulus-type",
                    BoundedMutationAction.Delete())
            ]);
        var plan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)).Plans);
        var first = new DocumentMutation(
            "workflowTriggerBinding",
            plan.MutationIdentity,
            "first",
            [DocumentQueryClause.Of(DocumentQueryComparison.In("stimulusType", ["timer", "http", "http"]))]);
        var equivalent = new DocumentMutation(
            "workflowTriggerBinding",
            plan.MutationIdentity,
            "second",
            [DocumentQueryClause.Of(DocumentQueryComparison.In("stimulusType", ["http", "timer"]))]);
        var different = new DocumentMutation(
            "workflowTriggerBinding",
            plan.MutationIdentity,
            "third",
            [DocumentQueryClause.Of(DocumentQueryComparison.In("stimulusType", ["http"]))]);

        var fingerprint = BoundedMutationRequestFingerprint.Create(first, plan, "tenant-a");

        Assert.Equal(fingerprint, BoundedMutationRequestFingerprint.Create(equivalent, plan, "tenant-a"));
        Assert.NotEqual(fingerprint, BoundedMutationRequestFingerprint.Create(different, plan, "tenant-a"));
        Assert.NotEqual(fingerprint, BoundedMutationRequestFingerprint.Create(first, plan, "tenant-b"));
    }

    [Fact]
    public void Mutation_request_fingerprint_uses_canonical_identity_evidence_but_not_operation_id_case()
    {
        var fixture = CreateIntrinsicMutationFixture(
            linked: true,
            BoundedMutationAction.Delete(),
            StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        var plan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.LinkedIndex)).Plans);
        var retainedSpelling = new DocumentMutation(
            "workflowTriggerBinding",
            plan.MutationIdentity,
            "Operation-A",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal(
                PhysicalDocumentFieldPaths.Id,
                "metric-\U00010428-\u00e9"))]);
        var equivalentSpelling = new DocumentMutation(
            "workflowTriggerBinding",
            plan.MutationIdentity,
            "operation-a",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal(
                PhysicalDocumentFieldPaths.Id,
                "METRIC-\U00010400-\u00c9"))]);

        Assert.NotEqual(retainedSpelling.OperationId, equivalentSpelling.OperationId);
        Assert.Equal(
            BoundedMutationRequestFingerprint.Create(retainedSpelling, plan, "tenant-a"),
            BoundedMutationRequestFingerprint.Create(equivalentSpelling, plan, "tenant-a"));
    }

    [Fact]
    public void Bounded_mutation_selection_receives_the_same_canonical_identity_values_as_replay()
    {
        var fixture = CreateIntrinsicMutationFixture(
            linked: true,
            BoundedMutationAction.Delete(),
            StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        var plan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.LinkedIndex)).Plans);
        var comparison = DocumentQueryComparison.In(
            PhysicalDocumentFieldPaths.Id,
            ["metric-\U00010428-\u00e9", "METRIC-\U00010400-\u00c9"]);

        var bound = PhysicalDocumentIdentityQuery.Bind(plan.Predicate, comparison);

        Assert.Equal(plan.Predicate.DocumentIdentity, bound.Identity);
        var value = Assert.Single(bound.Values);
        Assert.IsType<PhysicalQueryIdentityValue.Exact>(value);
        Assert.Equal("00004D00004500005400005200004900004300002D01040000002D0000C9", value.ComparisonKey);
        Assert.Equal(
            "61c4070c8bb733ab75c6a4366219266bcf058446787a62365c57dd598de56181",
            ((PhysicalQueryIdentityValue.Exact)value).LookupKey);
    }

    [Fact]
    public void MutationRequestFingerprintIsStableAcrossProviderVersionUpgrades()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            fixture.Storage.BoundedQueries,
            fixture.Storage.NameOverrides,
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "prune-by-stimulus-type",
                    "list-by-stimulus-type",
                    BoundedMutationAction.Delete())
            ]);
        var firstPlan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(
                new ProviderIdentity("test-provider", "1.0.0"),
                PhysicalQuerySourceKind.PrimaryProjectedColumns)).Plans);
        var upgradedPlan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(
                new ProviderIdentity("test-provider", "2.0.0"),
                PhysicalQuerySourceKind.PrimaryProjectedColumns)).Plans);
        var mutation = new DocumentMutation(
            "workflowTriggerBinding",
            firstPlan.MutationIdentity,
            "rolling-upgrade",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))]);

        Assert.Equal(
            BoundedMutationRequestFingerprint.Create(mutation, firstPlan, "tenant-a"),
            BoundedMutationRequestFingerprint.Create(mutation, upgradedPlan, "tenant-a"));
    }
}
