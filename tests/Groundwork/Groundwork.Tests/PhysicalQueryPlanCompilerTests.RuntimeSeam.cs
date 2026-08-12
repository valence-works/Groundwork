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
    public async Task RuntimeSeamPreservesQueryIdentityAndDispatchesTheCompiledHandler()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson);
        var planned = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            capabilities));
        var handler = new RecordingHandler(
            "test.PrimaryCanonicalJson",
            PhysicalQuerySourceKind.PrimaryCanonicalJson,
            certifications: [CertificationFor(planned)]);
        var store = new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]);
        var query = new DocumentQuery(
            "workflowTriggerBinding",
            "list-by-stimulus-type",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))]);

        await store.QueryAsync(query);
        Assert.False(await store.AnyAsync(query.Select(BoundedQueryResultOperation.Any)));

        Assert.Equal("list-by-stimulus-type", handler.LastPlan!.QueryIdentity);
        Assert.Equal("by-stimulus-type", handler.LastPlan.LogicalIndexIdentity);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueryAsync(
            new DocumentQuery(
                "workflowTriggerBinding",
                "unknown-query",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))])));
    }

    [Fact]
    public void Runtime_invocation_fingerprint_omits_raw_values_and_covers_query_route_scope_and_exact_utf16()
    {
        const string sensitiveValue = "tenant-secret-value";
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);
        var alternateFixture = CreateFixture(
            PhysicalStorageForm.SharedDocuments,
            BoundedQueryExecutionClass.Ordinary);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson)));
        var alternatePlan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            alternateFixture.Route,
            alternateFixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson)));
        var query = new DocumentQuery(
            "workflowTriggerBinding",
            "list-by-stimulus-type",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", sensitiveValue))],
            [new DocumentQueryOrder("createdAt", PhysicalSortDirection.Descending)],
            skip: 3,
            take: 25,
            latestPerKeyPath: "correlationId");
        var scoped = new DocumentScopeSelection("tenant-a", new StorageScope("tenant-a"), false);
        var acrossScopes = new DocumentScopeSelection(null, null, true);

        var fingerprint = PhysicalDocumentQueryInvocationFingerprint.Compute(query, plan, scoped);

        Assert.Equal(fingerprint, PhysicalDocumentQueryInvocationFingerprint.Compute(query, plan, scoped));
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
        Assert.DoesNotContain(sensitiveValue, fingerprint, StringComparison.Ordinal);
        Assert.NotEqual(fingerprint, PhysicalDocumentQueryInvocationFingerprint.Compute(query.Page(4, 25), plan, scoped));
        Assert.NotEqual(fingerprint, PhysicalDocumentQueryInvocationFingerprint.Compute(new DocumentQuery(
            query.DocumentKind,
            query.QueryIdentity,
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "different"))],
            query.Order,
            query.Skip,
            query.Take,
            query.Continuation,
            query.LatestPerKeyPath,
            query.ResultOperation), plan, scoped));
        Assert.NotEqual(fingerprint, PhysicalDocumentQueryInvocationFingerprint.Compute(new DocumentQuery(
            query.DocumentKind,
            query.QueryIdentity,
            [DocumentQueryClause.Of(DocumentQueryComparison.NotContains("stimulusType", sensitiveValue))],
            query.Order,
            query.Skip,
            query.Take,
            query.Continuation,
            query.LatestPerKeyPath,
            query.ResultOperation), plan, scoped));
        Assert.NotEqual(fingerprint, PhysicalDocumentQueryInvocationFingerprint.Compute(
            query.Select(BoundedQueryResultOperation.Count), plan, scoped));
        Assert.NotEqual(fingerprint, PhysicalDocumentQueryInvocationFingerprint.Compute(query, alternatePlan, scoped));
        Assert.NotEqual(fingerprint, PhysicalDocumentQueryInvocationFingerprint.Compute(query, plan, acrossScopes));
        Assert.NotEqual(
            PhysicalDocumentQueryInvocationFingerprint.Compute(QueryWithValue("\ud800"), plan, scoped),
            PhysicalDocumentQueryInvocationFingerprint.Compute(QueryWithValue("\ud801"), plan, scoped));

        DocumentQuery QueryWithValue(string value) => new(
            query.DocumentKind,
            query.QueryIdentity,
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", value))]);
    }

    [Fact]
    public void Continuation_codec_allows_page_size_changes_and_rejects_query_scope_and_token_rewriting()
    {
        var declaration = Query(
            BoundedQueryExecutionClass.ScaleBearing,
            pagingSupport: QueryPagingSupport.Cursor);
        var fixture = CreateEntityFixture(StimulusTypeIndex(), declaration);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            CapabilitiesWithPaging(
                supportsKeysetPaging: true,
                supportsLatestPerKey: false,
                sources: [PhysicalQuerySourceKind.PrimaryProjectedColumns])));
        var upgradedPlan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            CapabilitiesWithPaging(
                new ProviderIdentity("test-provider", "2.0.0"),
                supportsKeysetPaging: true,
                supportsLatestPerKey: false,
                sources: [PhysicalQuerySourceKind.PrimaryProjectedColumns])));
        var query = new DocumentQuery(
            "workflowTriggerBinding",
            declaration.Identity,
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))],
            take: 10);
        var scope = new DocumentScopeSelection("tenant-a", new StorageScope("tenant-a"), false);
        var values = DocumentQueryOrderResolver.Resolve(query, plan)
            .Select((order, index) => new DocumentQueryContinuationValue(
                order.Field.ValueKind,
                DocumentQueryContinuationScalarKind.String,
                $"value-{index}"))
            .ToArray();

        var token = DocumentQueryContinuationCodec.Encode(query, plan, scope, values);

        Assert.Equal(values, DocumentQueryContinuationCodec.Decode(
            token,
            new DocumentQuery(
                query.DocumentKind,
                query.QueryIdentity,
                query.Clauses,
                query.Order,
                take: 50,
                continuation: token),
            plan,
            scope));
        Assert.Throws<InvalidDocumentQueryContinuationException>(() =>
            DocumentQueryContinuationCodec.Decode(
                token,
                new DocumentQuery(
                    query.DocumentKind,
                    query.QueryIdentity,
                    [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "timer"))],
                    take: 10,
                    continuation: token),
                plan,
                scope));
        Assert.Throws<InvalidDocumentQueryContinuationException>(() =>
            DocumentQueryContinuationCodec.Decode(
                token,
                query.ContinueAfter(token),
                plan,
                new DocumentScopeSelection("tenant-b", new StorageScope("tenant-b"), false)));
        Assert.Throws<InvalidDocumentQueryContinuationException>(() =>
            DocumentQueryContinuationCodec.Decode(
                token[..^1] + (token[^1] == 'a' ? 'b' : 'a'),
                query.ContinueAfter(token[..^1] + (token[^1] == 'a' ? 'b' : 'a')),
                plan,
                scope));
        Assert.Throws<InvalidDocumentQueryContinuationException>(() =>
            DocumentQueryContinuationCodec.Decode(
                "not-a-groundwork-continuation",
                query.ContinueAfter("not-a-groundwork-continuation"),
                plan,
                scope));
        Assert.Throws<InvalidDocumentQueryContinuationException>(() =>
            DocumentQueryContinuationCodec.Decode(" ", query, plan, scope));
        Assert.Throws<InvalidDocumentQueryContinuationException>(() =>
            DocumentQueryContinuationCodec.Encode(
                query,
                plan,
                scope,
                values.Select((value, index) => index == 0
                        ? value with
                        {
                            ScalarKind = DocumentQueryContinuationScalarKind.Int64,
                            Value = "not-an-integer"
                        }
                        : value)
                    .ToArray()));
        Assert.Throws<InvalidDocumentQueryContinuationException>(() =>
            DocumentQueryContinuationCodec.Decode(
                token,
                new DocumentQuery(
                    query.DocumentKind,
                    query.QueryIdentity,
                    query.Clauses,
                    [new DocumentQueryOrder("stimulusType", PhysicalSortDirection.Descending)],
                    take: 10,
                    continuation: token),
                plan,
                scope));
        Assert.Throws<InvalidDocumentQueryContinuationException>(() =>
            DocumentQueryContinuationCodec.Decode(
                token,
                query.ContinueAfter(token),
                upgradedPlan,
                scope));
        Assert.Throws<InvalidOperationException>(() =>
            DocumentQueryContinuationCodec.ValidateScope(
                plan,
                new DocumentScopeSelection(null, null, true)));
    }

    [Fact]
    public async Task Runtime_explain_uses_the_same_resolution_path_and_fails_closed_for_custom_handlers()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson);
        var planned = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            capabilities));
        var handler = new RecordingHandler(
            planned.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryCanonicalJson,
            certifications: [CertificationFor(planned)]);
        var store = new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]);
        var invalid = new DocumentQuery(
            "workflowTriggerBinding",
            "unknown-query",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))]);

        var execution = await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueryAsync(invalid));
        var explain = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExplainAsync(invalid));
        var unsupported = new List<NotSupportedException>();
        foreach (var operation in Enum.GetValues<BoundedQueryResultOperation>())
        {
            unsupported.Add(await Assert.ThrowsAsync<NotSupportedException>(() => store.ExplainAsync(
                new DocumentQuery(
                    "workflowTriggerBinding",
                    "list-by-stimulus-type",
                    [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))],
                    resultOperation: operation))));
        }

        Assert.Equal(execution.Message, explain.Message);
        Assert.All(unsupported, exception =>
            Assert.Contains(handler.Identity, exception.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeSeamFailsBeforeTrafficWhenScalePlanHasNoRegisteredIndexedHandler()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);
        var scaleStorage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            [Query(BoundedQueryExecutionClass.ScaleBearing)]);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson);
        var handler = new RecordingHandler(
            "test.PrimaryCanonicalJson",
            PhysicalQuerySourceKind.PrimaryCanonicalJson,
            certifications: []);

        var exception = Assert.Throws<InvalidOperationException>(() => new PhysicalQueryDocumentStore(
            fixture.Route,
            scaleStorage,
            capabilities,
            [handler]));

        Assert.Contains("GW-QUERY-005", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkedIndexCannotCertifyScaleBearingNativePrimaryFieldHandler()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.SharedDocuments,
            BoundedQueryExecutionClass.ScaleBearing);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.NativeDocumentFields));

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-005");
    }

    [Fact]
    public void RuntimeSeamRejectsCapabilityClaimsNotBackedByRegisteredHandler()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson);
        var limitedHandler = new RecordingHandler(
            "test.PrimaryCanonicalJson",
            PhysicalQuerySourceKind.PrimaryCanonicalJson,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            []);

        Assert.Throws<ArgumentException>(() => new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [limitedHandler]));
    }

    [Fact]
    public void RuntimeSeamRejectsHandlerCertificationForAnotherProvider()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, capabilities));
        var handler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryCanonicalJson,
            certifications:
            [
                CertificationFor(plan, provider: new ProviderIdentity("another-provider", "1.0.0"))
            ]);

        var exception = Assert.Throws<ArgumentException>(() => new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]));

        Assert.Contains("certify", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeSeamRejectsHandlerCertificationForUnrelatedPhysicalIndex()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, capabilities));
        var handler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryProjectedColumns,
            certifications:
            [
                CertificationFor(
                    plan,
                    indexName: plan.IndexName! with { Identifier = "ix_unrelated" })
            ]);

        var exception = Assert.Throws<ArgumentException>(() => new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]));

        Assert.Contains("certify", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeSeamRejectsHandlerCertificationForWrongObjectAndRole()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, capabilities));
        var handler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryProjectedColumns,
            certifications:
            [
                CertificationFor(
                    plan,
                    target: ExecutableStorageObjectRole.LinkedIndexStorage,
                    lookupObject: plan.LookupObject with { Identifier = "unrelated_object" })
            ]);

        var exception = Assert.Throws<ArgumentException>(() => new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]));

        Assert.Contains("certify", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeSeamRejectsHandlerCertificationForWrongFieldMapping()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, capabilities));
        var fields = PlanFieldIdentifiers(plan);
        fields["stimulusType"] = "unrelated_field";
        var handler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryProjectedColumns,
            certifications: [CertificationFor(plan, fieldIdentifiers: fields)]);

        var exception = Assert.Throws<ArgumentException>(() => new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]));

        Assert.Contains("certify", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runtime_seam_rejects_an_incomplete_identity_binding_certification()
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal });
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            capabilities));
        var fields = PlanFieldIdentifiers(plan);
        fields.Remove(plan.DocumentIdentity.Lookup.Path);
        var handler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryEnvelope,
            certifications: [CertificationFor(plan, fieldIdentifiers: fields)]);

        var exception = Assert.Throws<ArgumentException>(() => new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]));

        Assert.Contains("certify", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CapabilityProfileIsDeeplyImmutableAndUsesStructuralEquality()
    {
        var sources = new List<PhysicalQuerySourceKind> { PhysicalQuerySourceKind.NativeDocumentFields };
        var operations = new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal };
        var handlers = new Dictionary<PhysicalQuerySourceKind, string>
        {
            [PhysicalQuerySourceKind.NativeDocumentFields] = "mongo.native"
        };
        var fields = new Dictionary<string, string> { ["stimulusType"] = "content.stimulusType" };
        var valueKinds = new HashSet<IndexValueKind> { IndexValueKind.Keyword };
        var sourceValueKinds = new Dictionary<PhysicalQuerySourceKind, IReadOnlySet<IndexValueKind>>
        {
            [PhysicalQuerySourceKind.NativeDocumentFields] = valueKinds
        };
        var first = new PhysicalQueryPlannerCapabilities(
            new ProviderIdentity("provider", "1"),
            sources,
            operations,
            handlers,
            fields,
            true, true, true, true, true, true, true, true,
            sourceValueKinds);
        var second = new PhysicalQueryPlannerCapabilities(
            new ProviderIdentity("provider", "1"),
            [PhysicalQuerySourceKind.NativeDocumentFields],
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            new Dictionary<PhysicalQuerySourceKind, string>
            {
                [PhysicalQuerySourceKind.NativeDocumentFields] = "mongo.native"
            },
            new Dictionary<string, string> { ["stimulusType"] = "content.stimulusType" },
            true, true, true, true, true, true, true, true,
            new Dictionary<PhysicalQuerySourceKind, IReadOnlySet<IndexValueKind>>
            {
                [PhysicalQuerySourceKind.NativeDocumentFields] = new HashSet<IndexValueKind> { IndexValueKind.Keyword }
            });

        sources.Clear();
        operations.Clear();
        handlers.Clear();
        fields.Clear();
        valueKinds.Clear();
        sourceValueKinds.Clear();

        Assert.Single(first.SourcePreference);
        Assert.Single(first.SupportedOperations);
        Assert.Single(first.HandlerIdentities);
        Assert.Single(first.NativeFieldIdentifiers);
        Assert.Equal(IndexValueKind.Keyword, Assert.Single(first.SourceValueKinds.Single().Value));
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void HandlerCertificationIsDeeplyImmutableAndUsesStructuralEquality()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));
        var paths = plan.LogicalIndexPaths.ToList();
        var fields = PlanFieldIdentifiers(plan);
        var first = new PhysicalQueryHandlerCertification(
            plan.Provider,
            plan.StorageUnit,
            plan.QueryIdentity,
            plan.LogicalIndexIdentity,
            paths,
            plan.AccessKind,
            plan.Scope.Field.Target,
            plan.LookupObject,
            plan.PrimaryObject,
            plan.IndexName,
            fields,
            plan.RouteFingerprint);
        var second = CertificationFor(plan);

        paths.Clear();
        fields.Clear();

        Assert.Single(first.LogicalIndexPaths);
        Assert.NotEmpty(first.FieldIdentifiers);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<string>>(first.LogicalIndexPaths).Clear());
    }
}
