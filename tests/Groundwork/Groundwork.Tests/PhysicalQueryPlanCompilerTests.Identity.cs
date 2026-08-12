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
    public void Exact_identity_query_binding_projects_provider_neutral_linked_evidence()
    {
        var fixture = CreateIntrinsicMutationFixture(
            linked: true,
            BoundedMutationAction.Delete(),
            StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.LinkedIndex)));
        var binding = plan.DocumentIdentity;
        var value = binding.Bind(
            PortableQueryOperation.Equal,
            "METRIC-\U00010400-\u00c9");
        var equivalent = binding.Bind(
            PortableQueryOperation.Equal,
            "metric-\U00010428-\u00e9");
        var exact = Assert.IsType<PhysicalQueryIdentityValue.Exact>(value);
        var equivalentExact = Assert.IsType<PhysicalQueryIdentityValue.Exact>(equivalent);

        Assert.Equal(fixture.Route.LinkedRelationship!.Identity.OriginalId.Identifier, binding.Original.Identifier);
        Assert.Equal(fixture.Route.LinkedRelationship.Identity.ComparisonKey.Identifier, binding.Comparison.Identifier);
        Assert.Equal(fixture.Route.LinkedRelationship.Identity.LookupKey.Identifier, binding.Lookup.Identifier);
        Assert.Equal("00004D00004500005400005200004900004300002D01040000002D0000C9", value.ComparisonKey);
        Assert.Equal("61c4070c8bb733ab75c6a4366219266bcf058446787a62365c57dd598de56181", exact.LookupKey);
        Assert.Equal(value.ComparisonKey, equivalent.ComparisonKey);
        Assert.Equal(exact.LookupKey, equivalentExact.LookupKey);
    }

    [Theory]
    [InlineData(PhysicalQuerySourceKind.PrimaryEnvelope, PhysicalQueryFieldSource.Envelope, "id")]
    [InlineData(PhysicalQuerySourceKind.NativeDocumentFields, PhysicalQueryFieldSource.NativeDocumentField, "_id.id")]
    public void Identity_query_binding_uses_the_selected_primary_or_native_source(
        PhysicalQuerySourceKind source,
        PhysicalQueryFieldSource expectedFieldSource,
        string expectedOriginalIdentifier)
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal });

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(source)));

        Assert.Equal(expectedFieldSource, plan.DocumentIdentity.Original.Source);
        Assert.Equal(expectedOriginalIdentifier, plan.DocumentIdentity.Original.Identifier);
        Assert.Equal(fixture.Route.Envelope.Identity.ComparisonKey.Identifier, plan.DocumentIdentity.Comparison.Identifier);
        Assert.Equal(fixture.Route.Envelope.Identity.LookupKey.Identifier, plan.DocumentIdentity.Lookup.Identifier);
    }

    [Fact]
    public void Identity_contains_is_rejected_before_a_physical_plan_is_published()
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Contains });

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-011" &&
            diagnostic.Message.Contains("identity", StringComparison.OrdinalIgnoreCase) &&
            diagnostic.Message.Contains("Contains", StringComparison.Ordinal));
    }

    [Fact]
    public void Canonical_query_plan_serializes_the_complete_identity_binding()
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal });
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope)));

        var canonical = PhysicalQueryPlanSerializer.Serialize(plan);

        Assert.Contains("\"documentIdentity\":", canonical, StringComparison.Ordinal);
        Assert.Contains("\"stringCasePolicy\":\"UnicodeOrdinalIgnoreCase\"", canonical, StringComparison.Ordinal);
        Assert.Contains("\"original\":{\"path\":\"id.original\",\"identifier\":\"id\"", canonical, StringComparison.Ordinal);
        Assert.Contains("\"comparison\":{\"path\":\"id.comparison\",\"identifier\":\"id_comparison_key\"", canonical, StringComparison.Ordinal);
        Assert.Contains("\"lookup\":{\"path\":\"id.lookup\",\"identifier\":\"id_lookup_key\"", canonical, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PortableQueryOperation.Equal, true)]
    [InlineData(PortableQueryOperation.In, true)]
    [InlineData(PortableQueryOperation.NotEqual, true)]
    [InlineData(PortableQueryOperation.StartsWith, false)]
    [InlineData(PortableQueryOperation.GreaterThan, false)]
    [InlineData(PortableQueryOperation.GreaterThanOrEqual, false)]
    [InlineData(PortableQueryOperation.LessThan, false)]
    [InlineData(PortableQueryOperation.LessThanOrEqual, false)]
    public void Identity_operators_bind_structurally_valid_exact_or_ordered_evidence_without_adapter_policy(
        PortableQueryOperation operation,
        bool exact)
    {
        var fixture = CreateIdentityQueryFixture(new HashSet<PortableQueryOperation> { operation });
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope)));

        var value = plan.DocumentIdentity.Bind(operation, "metric-\U00010428-\u00e9");

        Assert.Equal("00004D00004500005400005200004900004300002D01040000002D0000C9", value.ComparisonKey);
        if (exact)
        {
            var exactValue = Assert.IsType<PhysicalQueryIdentityValue.Exact>(value);
            Assert.Equal("61c4070c8bb733ab75c6a4366219266bcf058446787a62365c57dd598de56181", exactValue.LookupKey);
        }
        else
        {
            Assert.IsType<PhysicalQueryIdentityValue.Ordered>(value);
        }
        var tieBreak = Assert.Single(plan.Order, order =>
            order.Path == PhysicalDocumentFieldPaths.Id && order.IsIdentityTieBreak);
        Assert.Equal(plan.DocumentIdentity.Comparison, tieBreak.Field);
    }

    [Fact]
    public void Identity_binding_rejects_null_instead_of_publishing_partial_evidence()
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal });
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope)));

        Assert.Throws<ArgumentNullException>(() =>
            plan.DocumentIdentity.Bind(PortableQueryOperation.Equal, null!));
    }

    [Theory]
    [InlineData(PhysicalQuerySourceKind.PrimaryEnvelope)]
    [InlineData(PhysicalQuerySourceKind.LinkedIndex)]
    [InlineData(PhysicalQuerySourceKind.NativeDocumentFields)]
    public void Exact_identity_plan_certifies_only_lookup_leading_full_comparison_indexes(
        PhysicalQuerySourceKind source)
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            source: source,
            indexLayout: IdentityIndexLayout.Exact);

        var result = PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, Capabilities(source));

        var plan = AssertPlan(result);
        Assert.NotNull(plan.IndexName);
        Assert.Equal(plan.DocumentIdentity.Comparison, plan.Predicates.Single().Field);
    }

    [Theory]
    [InlineData(PhysicalQuerySourceKind.PrimaryEnvelope)]
    [InlineData(PhysicalQuerySourceKind.LinkedIndex)]
    [InlineData(PhysicalQuerySourceKind.NativeDocumentFields)]
    public void Ordered_identity_plan_certifies_only_comparison_key_indexes(
        PhysicalQuerySourceKind source)
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.GreaterThan },
            source: source,
            indexLayout: IdentityIndexLayout.Ordered);

        var result = PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, Capabilities(source));

        var plan = AssertPlan(result);
        Assert.NotNull(plan.IndexName);
        Assert.Equal(plan.DocumentIdentity.Comparison, plan.Predicates.Single().Field);
    }

    [Theory]
    [InlineData(PhysicalQuerySourceKind.PrimaryEnvelope)]
    [InlineData(PhysicalQuerySourceKind.LinkedIndex)]
    [InlineData(PhysicalQuerySourceKind.NativeDocumentFields)]
    public void Retained_original_identity_index_cannot_certify_projected_query_evidence(
        PhysicalQuerySourceKind source)
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            source: source,
            indexLayout: IdentityIndexLayout.Original);

        var result = PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, Capabilities(source));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-006" &&
            diagnostic.Message.Contains("id.lookup", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("id.comparison", StringComparison.Ordinal));
    }

    [Fact]
    public void Scale_bearing_mixed_exact_and_ordered_identity_demand_is_rejected_without_choosing_index_order()
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.GreaterThan
            },
            indexLayout: IdentityIndexLayout.Exact);
        var ordinary = fixture.Storage.BoundedQueries.Single();
        var scaleBearing = new BoundedQueryDeclaration(
            ordinary.Identity,
            ordinary.IndexIdentity,
            ordinary.Operations,
            ordinary.SortSupport,
            ordinary.PagingSupport,
            BoundedQueryExecutionClass.ScaleBearing,
            ordinary.SupportsDisjunction,
            ordinary.SupportsTotalCount,
            ordinary.SortFields,
            ordinary.PredicateBindingMode == BoundedQueryPredicateBindingMode.ImplicitFirstLogicalIndexField
                ? null
                : ordinary.PredicateFields,
            ordinary.ResultOperations,
            ordinary.LatestPerKeyPath);
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            [scaleBearing],
            fixture.Storage.NameOverrides,
            fixture.Storage.BoundedMutations);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-012" &&
            diagnostic.Message.Contains("mixed exact and ordered", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ordinary_mixed_exact_and_ordered_identity_demand_uses_server_execution_without_certifying_one_index_shape()
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.GreaterThan
            },
            indexLayout: IdentityIndexLayout.Exact);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope));

        var plan = AssertPlan(result);
        Assert.Null(plan.IndexName);
        Assert.False(plan.IsScaleBearing);
    }
}
