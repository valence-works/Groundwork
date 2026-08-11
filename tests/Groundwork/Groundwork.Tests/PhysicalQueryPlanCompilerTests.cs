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
using System.Text.Json;
using Xunit;

namespace Groundwork.Tests;

public sealed class PhysicalQueryPlanCompilerTests
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

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments, PhysicalQueryAccessKind.LinkedIndexThenPrimary)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable, PhysicalQueryAccessKind.LinkedIndexThenPrimary)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable, PhysicalQueryAccessKind.PrimaryProjectedColumns)]
    public void TypeFilteredLookupPlansAcrossAllThreeForms(
        PhysicalStorageForm form,
        PhysicalQueryAccessKind expectedAccess)
    {
        var fixture = CreateFixture(form, BoundedQueryExecutionClass.ScaleBearing);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            CapabilitiesFor(expectedAccess));

        var plan = AssertPlan(result);
        Assert.Equal("list-by-stimulus-type", plan.QueryIdentity);
        Assert.Equal(expectedAccess, plan.AccessKind);
        Assert.Equal("stimulusType", Assert.Single(plan.Predicates).Path);
        Assert.True(plan.Scope.IsMandatory);
        Assert.Equal(fixture.Route.ScopeKey.Column.Identifier, plan.Scope.Field.Identifier);
        Assert.Equal(expectedAccess == PhysicalQueryAccessKind.LinkedIndexThenPrimary, plan.RequiresPrimaryLookup);
    }

    [Fact]
    public void OrdinaryDedicatedLookupCanUsePrimaryCanonicalJsonWithoutClientFallback()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson)));

        Assert.Equal(PhysicalQueryAccessKind.PrimaryCanonicalJson, plan.AccessKind);
        var field = Assert.Single(plan.Predicates).Field;
        Assert.Equal(PhysicalQueryFieldSource.CanonicalJsonPath, field.Source);
        Assert.Equal(IndexValueKind.Keyword, field.ValueKind);
        Assert.Contains("\"valueKind\":\"Keyword\"", PhysicalQueryPlanSerializer.Serialize(plan));
    }

    [Theory]
    [InlineData(false, IndexValueKind.Number, PortableQueryOperation.Contains)]
    [InlineData(false, IndexValueKind.Number, PortableQueryOperation.NotContains)]
    [InlineData(false, IndexValueKind.Number, PortableQueryOperation.StartsWith)]
    [InlineData(false, IndexValueKind.Boolean, PortableQueryOperation.Contains)]
    [InlineData(false, IndexValueKind.Boolean, PortableQueryOperation.NotContains)]
    [InlineData(false, IndexValueKind.Boolean, PortableQueryOperation.StartsWith)]
    [InlineData(false, IndexValueKind.DateTime, PortableQueryOperation.Contains)]
    [InlineData(false, IndexValueKind.DateTime, PortableQueryOperation.NotContains)]
    [InlineData(false, IndexValueKind.DateTime, PortableQueryOperation.StartsWith)]
    [InlineData(true, IndexValueKind.Number, PortableQueryOperation.Contains)]
    [InlineData(true, IndexValueKind.Number, PortableQueryOperation.NotContains)]
    [InlineData(true, IndexValueKind.Number, PortableQueryOperation.StartsWith)]
    [InlineData(true, IndexValueKind.Boolean, PortableQueryOperation.Contains)]
    [InlineData(true, IndexValueKind.Boolean, PortableQueryOperation.NotContains)]
    [InlineData(true, IndexValueKind.Boolean, PortableQueryOperation.StartsWith)]
    [InlineData(true, IndexValueKind.DateTime, PortableQueryOperation.Contains)]
    [InlineData(true, IndexValueKind.DateTime, PortableQueryOperation.NotContains)]
    [InlineData(true, IndexValueKind.DateTime, PortableQueryOperation.StartsWith)]
    public void TextOperationsCannotBeCertifiedForNonTextCanonicalOrProjectedValues(
        bool projected,
        IndexValueKind valueKind,
        PortableQueryOperation operation)
    {
        var fixture = CreateTypedFixture(projected, valueKind, operation);
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
            diagnostic.Code == "GW-QUERY-009" &&
            diagnostic.Message.Contains(operation.ToString(), StringComparison.Ordinal) &&
            diagnostic.Message.Contains(valueKind.ToString(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, IndexValueKind.String, PortableQueryOperation.Contains)]
    [InlineData(false, IndexValueKind.String, PortableQueryOperation.NotContains)]
    [InlineData(false, IndexValueKind.Keyword, PortableQueryOperation.StartsWith)]
    [InlineData(true, IndexValueKind.String, PortableQueryOperation.Contains)]
    [InlineData(true, IndexValueKind.String, PortableQueryOperation.NotContains)]
    [InlineData(true, IndexValueKind.Keyword, PortableQueryOperation.StartsWith)]
    public void TextOperationsRemainExecutableForTextCanonicalAndProjectedValues(
        bool projected,
        IndexValueKind valueKind,
        PortableQueryOperation operation)
    {
        var fixture = CreateTypedFixture(projected, valueKind, operation);
        var source = projected
            ? PhysicalQuerySourceKind.PrimaryProjectedColumns
            : PhysicalQuerySourceKind.PrimaryCanonicalJson;

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(source)));

        Assert.Equal(valueKind, Assert.Single(plan.Predicates).Field.ValueKind);
        Assert.Contains(operation, Assert.Single(plan.Predicates).Operations);
    }

    [Theory]
    [InlineData(PortablePhysicalType.Guid, PortableQueryOperation.Contains)]
    [InlineData(PortablePhysicalType.Guid, PortableQueryOperation.NotContains)]
    [InlineData(PortablePhysicalType.Guid, PortableQueryOperation.StartsWith)]
    [InlineData(PortablePhysicalType.Json, PortableQueryOperation.Contains)]
    [InlineData(PortablePhysicalType.Json, PortableQueryOperation.NotContains)]
    [InlineData(PortablePhysicalType.Json, PortableQueryOperation.StartsWith)]
    [InlineData(PortablePhysicalType.Binary, PortableQueryOperation.Contains)]
    [InlineData(PortablePhysicalType.Binary, PortableQueryOperation.NotContains)]
    [InlineData(PortablePhysicalType.Binary, PortableQueryOperation.StartsWith)]
    public void TextOperationsCannotBeCertifiedForOtherNonStringProjectedTypes(
        PortablePhysicalType physicalType,
        PortableQueryOperation operation)
    {
        var fixture = CreateTypedFixture(true, IndexValueKind.Keyword, operation, physicalType);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-009");
    }

    [Theory]
    [InlineData(IndexValueKind.Number, PortablePhysicalType.String, PortableQueryOperation.GreaterThan)]
    [InlineData(IndexValueKind.Number, PortablePhysicalType.String, PortableQueryOperation.Contains)]
    [InlineData(IndexValueKind.Boolean, PortablePhysicalType.String, PortableQueryOperation.Contains)]
    [InlineData(IndexValueKind.DateTime, PortablePhysicalType.String, PortableQueryOperation.Contains)]
    [InlineData(IndexValueKind.Keyword, PortablePhysicalType.Int32, PortableQueryOperation.Equal)]
    [InlineData(IndexValueKind.String, PortablePhysicalType.Guid, PortableQueryOperation.Equal)]
    public void Logical_value_kind_cannot_be_silently_reinterpreted_by_projected_storage(
        IndexValueKind logicalKind,
        PortablePhysicalType physicalType,
        PortableQueryOperation operation)
    {
        var result = Resolve(CreateTypedStorage(true, logicalKind, operation, physicalType));

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-PHYSICAL-031" &&
            diagnostic.Message.Contains(logicalKind.ToString(), StringComparison.Ordinal) &&
            diagnostic.Message.Contains(physicalType.ToString(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(29, 4)]
    [InlineData(8, null)]
    public void Decimal_projections_require_explicit_supported_precision_and_scale(int? precision, int? scale)
    {
        var logical = new LogicalIndexDeclaration(
            "by-value",
            [new IndexField("value")],
            IndexValueKind.Number,
            false,
            MissingValueBehavior.IncludedAsNull);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "decimal_entities",
            [new ProjectedColumnDefinition("value", "value", PortablePhysicalType.Decimal, Precision: precision, Scale: scale)],
            indexes:
            [new PhysicalIndexDefinition(logical.Identity, [new PhysicalIndexColumnDefinition("value", 0)])]);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logical],
            []);

        var result = Resolve(storage);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-018");
    }

    [Theory]
    [InlineData(IndexValueKind.String, PortablePhysicalType.String)]
    [InlineData(IndexValueKind.Keyword, PortablePhysicalType.String)]
    [InlineData(IndexValueKind.Number, PortablePhysicalType.Int32)]
    [InlineData(IndexValueKind.Number, PortablePhysicalType.Int64)]
    [InlineData(IndexValueKind.Number, PortablePhysicalType.Decimal)]
    [InlineData(IndexValueKind.Boolean, PortablePhysicalType.Boolean)]
    [InlineData(IndexValueKind.DateTime, PortablePhysicalType.DateTime)]
    [InlineData(IndexValueKind.Keyword, PortablePhysicalType.Guid)]
    [InlineData(IndexValueKind.Keyword, PortablePhysicalType.Json)]
    [InlineData(IndexValueKind.Keyword, PortablePhysicalType.Binary)]
    public void Compatible_projected_storage_preserves_the_declared_logical_value_kind(
        IndexValueKind logicalKind,
        PortablePhysicalType physicalType)
    {
        var fixture = CreateTypedFixture(
            true,
            logicalKind,
            PortableQueryOperation.Equal,
            physicalType);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));

        Assert.Equal(logicalKind, Assert.Single(plan.Predicates).Field.ValueKind);
    }

    [Fact]
    public void Unselected_projection_does_not_change_canonical_json_semantics()
    {
        var original = CreateTypedStorage(
            true,
            IndexValueKind.Number,
            PortableQueryOperation.GreaterThan,
            PortablePhysicalType.String);
        var explicitPolicy = Assert.IsType<PhysicalStoragePolicy.ExplicitPolicy>(original.Policy);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            explicitPolicy.Definition.FeatureDefaultLogicalName!,
            explicitPolicy.Definition.ProjectedColumns,
            explicitPolicy.Definition.Envelope);
        var storage = new StorageUnitPhysicalStorage(
            original.ProvisioningMode,
            PhysicalStoragePolicy.Explicit(definition),
            original.LogicalIndexes,
            original.BoundedQueries);

        var fixture = Resolve(storage, null);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson)));

        var field = Assert.Single(plan.Predicates).Field;
        Assert.Equal(PhysicalQueryFieldSource.CanonicalJsonPath, field.Source);
        Assert.Equal(IndexValueKind.Number, field.ValueKind);
    }

    [Theory]
    [InlineData("id", IndexValueKind.Number, IndexValueKind.Keyword)]
    [InlineData("version", IndexValueKind.Keyword, IndexValueKind.Number)]
    public void Envelope_fields_reject_declared_kinds_that_change_intrinsic_semantics(
        string path,
        IndexValueKind declared,
        IndexValueKind intrinsic)
    {
        var logical = new LogicalIndexDeclaration(
            "by-envelope",
            [new IndexField(path)],
            declared,
            false,
            MissingValueBehavior.IncludedAsNull);
        var query = new BoundedQueryDeclaration(
            "by-envelope",
            logical.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.GreaterThan },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.Ordinary);
        var definition = PhysicalTableDefinition.DedicatedDocumentTable(
            "envelope_documents",
            indexes:
            [
                new PhysicalIndexDefinition(
                    logical.Identity,
                    [
                        new PhysicalIndexColumnDefinition("storage_scope", 0),
                        new PhysicalIndexColumnDefinition(path, 1)
                    ])
            ]);
        var fixture = Resolve(new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logical],
            [query]), null);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope));

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-010" &&
            diagnostic.Message.Contains(intrinsic.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void EnvelopeIndexCanBeSelectedInPrimaryStorage()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-document-kind",
            [new IndexField("documentKind")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var query = new BoundedQueryDeclaration(
            "list-by-document-kind",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing);
        var definition = PhysicalTableDefinition.DedicatedDocumentTable(
            "workflow_trigger_bindings",
            indexes:
            [
                new PhysicalIndexDefinition(
                    logicalIndex.Identity,
                    [
                        new PhysicalIndexColumnDefinition("storage_scope", 0),
                        new PhysicalIndexColumnDefinition("document_kind", 1),
                        new PhysicalIndexColumnDefinition("id_comparison_key", 2)
                    ],
                    target: PhysicalIndexStorageTarget.PrimaryStorage)
            ]);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logicalIndex],
            [query]);
        var fixture = Resolve(storage, null);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope)));

        Assert.Equal(PhysicalQueryAccessKind.PrimaryEnvelope, plan.AccessKind);
        Assert.Equal(PhysicalQueryFieldSource.Envelope, Assert.Single(plan.Predicates).Field.Source);
    }

    [Fact]
    public void ProviderPreferenceCanSelectNativeDocumentFields()
    {
        var fixture = CreateFixture(PhysicalStorageForm.PhysicalEntityTable, BoundedQueryExecutionClass.ScaleBearing);
        var capabilities = Capabilities(
            PhysicalQuerySourceKind.NativeDocumentFields,
            PhysicalQuerySourceKind.PrimaryCanonicalJson);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, capabilities));

        Assert.Equal(PhysicalQueryAccessKind.NativeDocumentFields, plan.AccessKind);
        Assert.Equal(PhysicalQueryFieldSource.NativeDocumentField, Assert.Single(plan.Predicates).Field.Source);
        Assert.Equal("content.stimulusType", Assert.Single(plan.Predicates).Field.Identifier);
        Assert.Equal("storage_scope", plan.Scope.Field.Identifier);
        Assert.Collection(
            plan.Order.Where(order => order.IsIdentityTieBreak),
            order => Assert.Equal("storage_scope", order.Field.Identifier),
            order => Assert.Equal(plan.DocumentIdentity.Comparison.Identifier, order.Field.Identifier));
    }

    [Fact]
    public void Global_route_uses_only_id_as_the_structural_identity_tie_break()
    {
        var scoped = CreateFixture(PhysicalStorageForm.DedicatedDocumentTable, BoundedQueryExecutionClass.Ordinary);
        var global = Resolve(scoped.Storage, binding: null, TenancyPolicy.Global);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            global.Route,
            global.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson)));

        Assert.Equal("id", Assert.Single(plan.Order, order => order.IsIdentityTieBreak).Path);
    }

    [Fact]
    public void Explicit_empty_predicate_fields_plan_an_unfiltered_global_id_ordered_offset_count_route()
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
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true,
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
            [query]);
        var fixture = Resolve(storage, binding: null, tenancy: TenancyPolicy.Global);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope)));

        Assert.Empty(plan.Predicates);
        Assert.Equal(PhysicalQueryAccessKind.PrimaryEnvelope, plan.AccessKind);
        Assert.Equal("id_comparison_key", Assert.Single(plan.Order).Field.Identifier);
        Assert.Equal(Assert.Single(fixture.Route.Indexes).Name, plan.IndexName);
        Assert.Contains(BoundedQueryResultOperation.Count, plan.ResultOperations);
    }

    [Fact]
    public void Explicitly_unfiltered_route_without_index_backed_identity_tie_break_is_rejected_before_dispatch()
    {
        var index = new LogicalIndexDeclaration(
            "by-category",
            [new IndexField("category")],
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
            sortFields: [new BoundedQuerySortField("category", PhysicalSortDirection.Ascending)],
            predicateFields: []);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "global_documents",
            [new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String)],
            indexes:
            [
                new PhysicalIndexDefinition(
                    index.Identity,
                    [new PhysicalIndexColumnDefinition("category", 0)])
            ]);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [index],
            []);
        var fixture = Resolve(storage, binding: null, tenancy: TenancyPolicy.Global);
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
        Assert.Contains(
            result.Diagnostics,
            diagnostic =>
                diagnostic.Code == "GW-QUERY-005" &&
                diagnostic.Message.Contains("no executable indexed server-side route", StringComparison.Ordinal));
    }

    [Fact]
    public void Explicitly_unfiltered_route_accepts_an_index_backed_identity_tie_break()
    {
        var index = new LogicalIndexDeclaration(
            "by-category-id",
            [new IndexField("category"), new IndexField(PhysicalDocumentFieldPaths.Id)],
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
            sortFields: [new BoundedQuerySortField("category", PhysicalSortDirection.Ascending)],
            predicateFields: []);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "global_documents",
            [new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String)],
            indexes:
            [
                new PhysicalIndexDefinition(
                    index.Identity,
                    [
                        new PhysicalIndexColumnDefinition("category", 0),
                        new PhysicalIndexColumnDefinition("id_comparison_key", 1)
                    ])
            ]);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [index],
            [query]);
        var fixture = Resolve(storage, binding: null, tenancy: TenancyPolicy.Global);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));

        Assert.Empty(plan.Predicates);
        Assert.Equal(
            ["category", "id_comparison_key"],
            plan.Order.Select(order => order.Field.Identifier));
        Assert.True(plan.Order[^1].IsIdentityTieBreak);
    }

    [Fact]
    public void Explicitly_unfiltered_cursor_route_uses_an_index_backed_lookup_tie_break()
    {
        var index = new LogicalIndexDeclaration(
            "by-category",
            [new IndexField("category")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var query = new BoundedQueryDeclaration(
            "list-all",
            index.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields: [new BoundedQuerySortField("category", PhysicalSortDirection.Ascending)],
            predicateFields: []);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "global_documents",
            [new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String)],
            indexes:
            [
                new PhysicalIndexDefinition(
                    index.Identity,
                    [
                        new PhysicalIndexColumnDefinition("category", 0),
                        new PhysicalIndexColumnDefinition("id_lookup_key", 1)
                    ])
            ]);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [index],
            [query]);
        var fixture = Resolve(storage, binding: null, tenancy: TenancyPolicy.Global);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));

        Assert.Equal(
            ["category", "id_lookup_key"],
            plan.Order.Select(order => order.Field.Identifier));
        Assert.True(plan.Order[^1].IsIdentityTieBreak);
    }

    [Fact]
    public void Scale_bearing_ordered_offset_route_rejects_a_physical_index_missing_the_comparison_tie_break()
    {
        var fixture = CreateOffsetTieBreakFixture(includeTieBreak: false);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-006" &&
            diagnostic.Message.Contains(PhysicalDocumentIdentityFieldPaths.Comparison, StringComparison.Ordinal));
    }

    [Fact]
    public void Scale_bearing_ordered_offset_route_accepts_the_comparison_tie_break()
    {
        var fixture = CreateOffsetTieBreakFixture(includeTieBreak: true);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));

        Assert.Equal(PhysicalDocumentIdentityFieldPaths.Comparison, plan.Order[^1].Field.Path);
        Assert.True(plan.Order[^1].IsIdentityTieBreak);
    }

    [Fact]
    public void Scale_bearing_ordered_offset_route_rejects_a_reversed_comparison_tie_break_direction()
    {
        var fixture = CreateOffsetTieBreakFixture(
            includeTieBreak: true,
            tieBreakDirection: PhysicalSortDirection.Descending);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-006" &&
            diagnostic.Message.Contains("directions", StringComparison.Ordinal));
    }

    [Fact]
    public void Scale_bearing_ordered_offset_route_keeps_its_equality_prefix_requirement()
    {
        var fixture = CreateOffsetTieBreakFixture(
            includeTieBreak: true,
            predicateOperation: PortableQueryOperation.GreaterThan);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-006" &&
            diagnostic.Message.Contains("single-value equality", StringComparison.Ordinal));
    }

    [Fact]
    public void Scale_bearing_unique_offset_route_uses_the_unique_key_without_an_identity_tail()
    {
        var fixture = CreateOffsetTieBreakFixture(includeTieBreak: false, isUnique: true);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));

        Assert.DoesNotContain(plan.Order, order => order.Path == PhysicalDocumentFieldPaths.Id);
        Assert.DoesNotContain(
            Assert.Single(fixture.Route.Indexes).Columns,
            column => column.Column.LogicalName == "id_comparison_key");
    }

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
    public void Relationship_guards_require_complete_manifest_route_admission()
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
                    "guarded-prune",
                    "list-by-stimulus-type",
                    BoundedMutationAction.Delete(),
                    [BoundedMutationRelationshipGuard.RequireNoReferences("token-authorization")])
            ]);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-MUTATION-009");
    }

    [Fact]
    public void Manifest_relationship_requires_a_stable_source_reference_path()
    {
        var source = new StorageUnitIdentity("token");
        var target = new StorageUnitIdentity("authorization");

        Assert.Throws<ArgumentException>(() => new ManifestRelationshipDeclaration(
            "token-authorization",
            source,
            null!,
            "token-by-authorization-id",
            target,
            PhysicalDocumentFieldPaths.Id,
            "authorization-by-id"));
        Assert.Throws<ArgumentException>(() => new ManifestRelationshipDeclaration(
            "token-authorization",
            source,
            " ",
            "token-by-authorization-id",
            target,
            PhysicalDocumentFieldPaths.Id,
            "authorization-by-id"));
    }

    [Fact]
    public void No_reference_guard_binds_the_referencing_route_and_indexed_scalar_path()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var guard = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(Assert.Single(result.Plans).RelationshipGuards));
        Assert.Equal("token-authorization", guard.Relationship.Identity);
        Assert.Equal("token", guard.Relationship.SourceRoute.StorageUnit.Value);
        Assert.Equal("authorizationId", guard.Relationship.SourceCanonicalJsonReference.Path);
        Assert.Equal(PhysicalQueryFieldSource.CanonicalJsonPath, guard.Relationship.SourceCanonicalJsonReference.Source);
        Assert.Equal("token-by-authorization-id", guard.Relationship.SourceReferenceDeclarationIndex.Identity);
        Assert.Equal("authorization", guard.Relationship.TargetRoute.StorageUnit.Value);
        Assert.Equal("authorization-by-id", guard.Relationship.TargetEqualityIndex.Identity);
    }

    [Fact]
    public void Related_target_guard_binds_route_evidence_and_changes_the_request_fingerprint()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: true);
        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var plan = Assert.Single(result.Plans);
        var guard = Assert.IsType<PhysicalRequireRelatedTargetNotEqualMutationGuard>(
            Assert.Single(plan.RelationshipGuards));
        Assert.Equal("authorizationId", guard.Relationship.SourceCanonicalJsonReference.Path);
        Assert.Equal("authorization", guard.Relationship.TargetRoute.StorageUnit.Value);
        Assert.Equal("status", guard.TargetPredicateField.Path);
        Assert.Equal("authorization-by-status", guard.TargetPredicateIndex.Identity);
        Assert.Equal("valid", guard.DisallowedTargetValue);

        var changed = plan with
        {
            RelationshipGuards =
            [
                new PhysicalRequireRelatedTargetNotEqualMutationGuard(
                    guard.Relationship,
                    guard.TargetPredicateField,
                    guard.TargetPredicateIndex,
                    "revoked")
            ]
        };
        var request = new DocumentMutation(
            "token",
            "guarded-prune",
            "operation-1",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "invalid"))]);

        Assert.NotEqual(
            BoundedMutationRequestFingerprint.Create(request, plan, "global"),
            BoundedMutationRequestFingerprint.Create(request, changed, "global"));

        var providerChanged = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(
                new ProviderIdentity("provider-after-upgrade", "9.0"),
                PhysicalQuerySourceKind.PrimaryProjectedColumns)).Plans);
        Assert.NotEqual(plan.Fingerprint, providerChanged.Fingerprint);
        Assert.Equal(
            BoundedMutationRequestFingerprint.Create(request, plan, "global"),
            BoundedMutationRequestFingerprint.Create(request, providerChanged, "global"));
    }

    [Fact]
    public void Relationship_guard_rejects_an_unindexed_reference_path()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false, includeReferenceIndex: false);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-RELATIONSHIP-010");
    }

    [Fact]
    public void Relationship_plan_preserves_an_optional_missing_reference_and_projects_present_values_with_the_target_policy()
    {
        var fixture = CreateRelationshipFixture(
            relatedTarget: false,
            includeMutation: false,
            targetIdentityCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase,
            referenceCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);

        var plan = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(fixture.RouteSet).Plans);

        Assert.Null(plan.ProjectReference(null));
        Assert.Throws<ArgumentException>(() => plan.ProjectReference(""));
        Assert.Throws<ArgumentException>(() => plan.ProjectReference(" "));
        Assert.Null(plan.ProjectReferenceIdentity(null));
        var identity = Assert.IsType<PortableStringIdentityProjection>(plan.ProjectReferenceIdentity("AUTH-1"));
        Assert.Equal(
            plan.TargetRoute.Envelope.Identity.Project("AUTH-1").ComparisonKey,
            plan.ProjectReference("AUTH-1"));
        Assert.Equal(plan.TargetRoute.Envelope.Identity.Project("AUTH-1"), identity);
        Assert.Equal(PhysicalQueryFieldSource.CanonicalJsonPath, plan.SourceCanonicalJsonReference.Source);
        Assert.NotEqual(
            plan.SourceReferenceDeclarationIndex.Name,
            plan.TargetEqualityIndex.Name);
    }

    [Fact]
    public void Relationship_plan_exposes_a_canonical_collision_safe_reference_and_fence_schema()
    {
        var plan = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(relatedTarget: false, includeMutation: false).RouteSet).Plans);

        var schema = plan.MaterializationSchema;

        Assert.Equal(plan.Identity, schema.RelationshipIdentity);
        Assert.Equal(plan.Materialization.ReferenceStorageIdentity, schema.GenerationIdentity);
        Assert.Equal(plan.Materialization.ReferenceStorageIdentity, schema.Reference.StorageIdentity);
        Assert.Equal(plan.Materialization.FenceStorageIdentity, schema.Fence.StorageIdentity);
        Assert.Equal(
            [
                PhysicalRelationshipSidecarField.MaterializationGeneration,
                PhysicalRelationshipSidecarField.SourceScope,
                PhysicalRelationshipSidecarField.SourceLookupKey,
                PhysicalRelationshipSidecarField.SourceComparisonKey,
                PhysicalRelationshipSidecarField.TargetScope,
                PhysicalRelationshipSidecarField.TargetLookupKey,
                PhysicalRelationshipSidecarField.TargetComparisonKey
            ],
            schema.Reference.Fields);
        Assert.Equal(
            [
                PhysicalRelationshipSidecarField.MaterializationGeneration,
                PhysicalRelationshipSidecarField.SourceScope,
                PhysicalRelationshipSidecarField.SourceLookupKey,
                PhysicalRelationshipSidecarField.SourceComparisonKey
            ],
            schema.Reference.UniqueSourceAccessPath.Fields);
        Assert.True(schema.Reference.UniqueSourceAccessPath.IsUnique);
        Assert.Equal(
            [
                PhysicalRelationshipSidecarField.MaterializationGeneration,
                PhysicalRelationshipSidecarField.TargetScope,
                PhysicalRelationshipSidecarField.TargetLookupKey,
                PhysicalRelationshipSidecarField.TargetComparisonKey
            ],
            schema.Reference.TargetSeekAccessPath.Fields);
        Assert.False(schema.Reference.TargetSeekAccessPath.IsUnique);
        Assert.Equal(schema.Reference.TargetSeekAccessPath.Fields, schema.Fence.Fields);
        Assert.Equal(schema.Fence.Fields, schema.Fence.UniqueTargetFenceAccessPath.Fields);
        Assert.True(schema.Fence.UniqueTargetFenceAccessPath.IsUnique);
        Assert.Contains("\"SourceLookupKey\",\"SourceComparisonKey\"", schema.CanonicalJson, StringComparison.Ordinal);
        Assert.Contains("\"TargetLookupKey\",\"TargetComparisonKey\"", schema.CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Relationship_sidecar_schema_is_deterministic_and_tracks_semantic_generation_only()
    {
        var baseline = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(relatedTarget: false, includeMutation: false).RouteSet).Plans)
            .MaterializationSchema;
        var same = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(relatedTarget: false, includeMutation: false).RouteSet).Plans)
            .MaterializationSchema;
        var renamed = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(
                relatedTarget: false,
                includeMutation: false,
                authorizationPhysicalName: "authorizations_v2",
                tokenPhysicalName: "tokens_v2").RouteSet).Plans)
            .MaterializationSchema;
        var semanticDrift = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(
                relatedTarget: false,
                includeMutation: false,
                targetIdentityCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase,
                referenceCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase).RouteSet).Plans)
            .MaterializationSchema;

        Assert.Equal(baseline.CanonicalJson, same.CanonicalJson);
        Assert.Equal(baseline.Fingerprint, same.Fingerprint);
        Assert.Equal(baseline, same);
        Assert.Equal(baseline.CanonicalJson, renamed.CanonicalJson);
        Assert.Equal(baseline.Fingerprint, renamed.Fingerprint);
        Assert.Equal(baseline, renamed);
        Assert.NotEqual(baseline.GenerationIdentity, semanticDrift.GenerationIdentity);
        Assert.NotEqual(baseline.Fingerprint, semanticDrift.Fingerprint);
    }

    [Fact]
    public void Relationship_sidecar_schema_canonical_payload_and_fingerprint_bind_every_generated_identity()
    {
        var schema = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(relatedTarget: false, includeMutation: false).RouteSet).Plans)
            .MaterializationSchema;
        var expectedPayload =
            $"{{\"schemaVersion\":1,\"relationship\":\"{schema.RelationshipIdentity}\",\"generation\":\"{schema.GenerationIdentity}\"," +
            $"\"reference\":{{\"storageIdentity\":\"{schema.Reference.StorageIdentity}\",\"generation\":\"{schema.Reference.GenerationIdentity}\"," +
            "\"fields\":[\"MaterializationGeneration\",\"SourceScope\",\"SourceLookupKey\",\"SourceComparisonKey\",\"TargetScope\",\"TargetLookupKey\",\"TargetComparisonKey\"]," +
            $"\"uniqueSourceAccessPath\":{{\"identity\":\"{schema.Reference.UniqueSourceAccessPath.Identity}\",\"unique\":true," +
            "\"fields\":[\"MaterializationGeneration\",\"SourceScope\",\"SourceLookupKey\",\"SourceComparisonKey\"]}," +
            $"\"targetSeekAccessPath\":{{\"identity\":\"{schema.Reference.TargetSeekAccessPath.Identity}\",\"unique\":false," +
            "\"fields\":[\"MaterializationGeneration\",\"TargetScope\",\"TargetLookupKey\",\"TargetComparisonKey\"]}}," +
            $"\"fence\":{{\"storageIdentity\":\"{schema.Fence.StorageIdentity}\",\"generation\":\"{schema.Fence.GenerationIdentity}\"," +
            "\"fields\":[\"MaterializationGeneration\",\"TargetScope\",\"TargetLookupKey\",\"TargetComparisonKey\"]," +
            $"\"uniqueTargetFenceAccessPath\":{{\"identity\":\"{schema.Fence.UniqueTargetFenceAccessPath.Identity}\",\"unique\":true," +
            "\"fields\":[\"MaterializationGeneration\",\"TargetScope\",\"TargetLookupKey\",\"TargetComparisonKey\"]}}}";
        var expectedFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(expectedPayload))).ToLowerInvariant();
        var expectedCanonicalJson =
            $"{expectedPayload[..^1]},\"fingerprint\":\"{expectedFingerprint}\"}}";

        Assert.Equal(expectedFingerprint, schema.Fingerprint);
        Assert.Equal(expectedCanonicalJson, schema.CanonicalJson);

        var alteredReference = new PhysicalRelationshipReferenceMaterializationSchema(
            schema.Reference.StorageIdentity,
            schema.Reference.GenerationIdentity,
            schema.Reference.UniqueSourceAccessPath,
            new PhysicalRelationshipSidecarAccessPath(
                "relationship-reference-by-target:altered",
                isUnique: false,
                schema.Reference.TargetSeekAccessPath.Fields));
        var altered = new PhysicalRelationshipMaterializationSchema(
            schema.RelationshipIdentity,
            schema.GenerationIdentity,
            alteredReference,
            schema.Fence);

        Assert.Equal(schema.GenerationIdentity, altered.GenerationIdentity);
        Assert.NotEqual(schema.Fingerprint, altered.Fingerprint);
    }

    [Fact]
    public void Relationship_sidecar_schema_rejects_non_contract_access_paths_and_mixed_generations()
    {
        var schema = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(relatedTarget: false, includeMutation: false).RouteSet).Plans)
            .MaterializationSchema;
        var reference = schema.Reference;
        var fence = schema.Fence;

        var sourceWithWrongOrder = new PhysicalRelationshipSidecarAccessPath(
            "invalid-source-order",
            isUnique: true,
            [
                PhysicalRelationshipSidecarField.MaterializationGeneration,
                PhysicalRelationshipSidecarField.SourceScope,
                PhysicalRelationshipSidecarField.SourceComparisonKey,
                PhysicalRelationshipSidecarField.SourceLookupKey
            ]);
        var targetWithWrongUniqueness = new PhysicalRelationshipSidecarAccessPath(
            "invalid-target-uniqueness",
            isUnique: true,
            reference.TargetSeekAccessPath.Fields);
        var targetWithDuplicateIdentity = new PhysicalRelationshipSidecarAccessPath(
            reference.UniqueSourceAccessPath.Identity,
            isUnique: false,
            reference.TargetSeekAccessPath.Fields);
        var referenceWithMismatchedStorageGeneration =
            new PhysicalRelationshipReferenceMaterializationSchema(
                "relationship-reference:old-generation",
                schema.GenerationIdentity,
                reference.UniqueSourceAccessPath,
                reference.TargetSeekAccessPath);
        var differentGenerationFence = new PhysicalRelationshipTargetFenceSchema(
            fence.StorageIdentity,
            "relationship-reference:other-generation",
            fence.UniqueTargetFenceAccessPath);

        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipReferenceMaterializationSchema(
            reference.StorageIdentity,
            reference.GenerationIdentity,
            sourceWithWrongOrder,
            reference.TargetSeekAccessPath));
        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipReferenceMaterializationSchema(
            reference.StorageIdentity,
            reference.GenerationIdentity,
            reference.UniqueSourceAccessPath,
            targetWithWrongUniqueness));
        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipReferenceMaterializationSchema(
            reference.StorageIdentity,
            reference.GenerationIdentity,
            reference.UniqueSourceAccessPath,
            targetWithDuplicateIdentity));
        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipMaterializationSchema(
            schema.RelationshipIdentity,
            schema.GenerationIdentity,
            referenceWithMismatchedStorageGeneration,
            fence));
        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipMaterializationSchema(
            schema.RelationshipIdentity,
            schema.GenerationIdentity,
            reference,
            differentGenerationFence));
    }

    [Fact]
    public void Every_manifest_relationship_is_admitted_even_when_no_mutation_guard_references_it()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false, includeMutation: false);

        var relationships = PhysicalRelationshipPlanCompiler.Compile(fixture.RouteSet);
        var mutations = PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.True(relationships.IsValid, string.Join("; ", relationships.Diagnostics.Select(item => item.Message)));
        Assert.Single(relationships.Plans);
        Assert.True(mutations.IsValid, string.Join("; ", mutations.Diagnostics.Select(item => item.Message)));
        Assert.Empty(mutations.Plans);
    }

    [Fact]
    public void Provider_admission_rejects_relationship_manifests_without_a_public_preview_override()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false, includeMutation: false);
        var provider = new ProviderIdentity("provider-under-test", "1.0");

        var exception = Assert.Throws<PhysicalRelationshipProviderNotSupportedException>(() =>
            PhysicalRelationshipProviderAdmission.RequireMaterializationSupport(
                fixture.Manifest,
                provider));

        Assert.Contains("GW-RELATIONSHIP-012", exception.Message);
        Assert.Equal(["token-authorization"], exception.RelationshipIdentities);
        Assert.Equal(
            2,
            typeof(PhysicalRelationshipProviderAdmission)
                .GetMethod(nameof(PhysicalRelationshipProviderAdmission.RequireMaterializationSupport))!
                .GetParameters()
                .Length);
    }

    [Fact]
    public void Relationship_materialization_identity_rejects_invalid_unicode_before_hashing()
    {
        var invalid = new string('\uD800', 1);
        var replacement = "\uFFFD";
        var invalidFixtures = new[]
        {
            CreateRelationshipFixture(
                relatedTarget: false,
                includeMutation: false,
                manifestIdentity: $"manifest-{invalid}"),
            CreateRelationshipFixture(
                relatedTarget: false,
                includeMutation: false,
                tokenIdentity: $"token-{invalid}"),
            CreateRelationshipFixture(
                relatedTarget: false,
                includeMutation: false,
                sourceReferencePath: $"authorization-{invalid}"),
            CreateRelationshipFixture(
                relatedTarget: false,
                includeMutation: false,
                sourceReferenceIndexIdentity: $"token-by-authorization-{invalid}")
        };

        Assert.All(invalidFixtures, fixture =>
            Assert.Throws<ArgumentException>(() =>
                PhysicalRelationshipPlanCompiler.Compile(fixture.RouteSet)));

        var replacementPlan = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(
                relatedTarget: false,
                includeMutation: false,
                manifestIdentity: $"manifest-{replacement}").RouteSet).Plans);
        Assert.NotEmpty(replacementPlan.Materialization.ReferenceStorageIdentity);
    }

    [Fact]
    public void Provider_admission_rejects_guarded_mutations_without_relationship_declarations()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);
        var guardOnly = fixture.Manifest with { Relationships = [] };
        var provider = new ProviderIdentity("provider-under-test", "1.0");

        var exception = Assert.Throws<PhysicalRelationshipProviderNotSupportedException>(() =>
            PhysicalRelationshipProviderAdmission.RequireMaterializationSupport(
                guardOnly,
                provider));

        Assert.Equal(["token-authorization"], exception.RelationshipIdentities);
    }

    [Fact]
    public void Relationship_admission_rejects_non_string_and_collection_reference_paths()
    {
        var nonString = CreateRelationshipFixture(
            relatedTarget: false,
            sourceReferenceType: PortablePhysicalType.Decimal,
            sourceReferenceValueKind: IndexValueKind.Number);
        var collection = CreateRelationshipFixture(
            relatedTarget: false,
            sourceReferenceCardinality: ProjectionCardinality.CollectionElements);

        var nonStringResult = PhysicalRelationshipPlanCompiler.Compile(nonString.RouteSet);
        var collectionResult = PhysicalRelationshipPlanCompiler.Compile(collection.RouteSet);

        Assert.Contains(nonStringResult.Diagnostics, diagnostic => diagnostic.Code == "GW-RELATIONSHIP-010");
        Assert.Contains(collectionResult.Diagnostics, diagnostic => diagnostic.Code == "GW-RELATIONSHIP-010");
    }

    [Fact]
    public void Relationship_admission_rejects_case_and_scope_policy_mismatches()
    {
        var caseMismatch = CreateRelationshipFixture(
            relatedTarget: false,
            targetIdentityCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        var scopeMismatch = CreateRelationshipFixture(
            relatedTarget: false,
            tokenTenancy: TenancyPolicy.Global);

        var caseResult = PhysicalRelationshipPlanCompiler.Compile(caseMismatch.RouteSet);
        var scopeResult = PhysicalRelationshipPlanCompiler.Compile(scopeMismatch.RouteSet);

        Assert.Contains(caseResult.Diagnostics, diagnostic => diagnostic.Code == "GW-RELATIONSHIP-008");
        Assert.Contains(scopeResult.Diagnostics, diagnostic => diagnostic.Code == "GW-RELATIONSHIP-006");
    }

    [Fact]
    public void Relationship_admission_accepts_matching_global_scope_without_scope_index_prefix()
    {
        var fixture = CreateRelationshipFixture(
            relatedTarget: false,
            authorizationTenancy: TenancyPolicy.Global,
            tokenTenancy: TenancyPolicy.Global);

        var result = PhysicalRelationshipPlanCompiler.Compile(fixture.RouteSet);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Single(result.Plans);
    }

    [Theory]
    [InlineData("token-by-status", "authorization-by-id", "GW-RELATIONSHIP-010")]
    [InlineData("missing-source-index", "authorization-by-id", "GW-RELATIONSHIP-010")]
    [InlineData("token-by-authorization-id", "authorization-by-status", "GW-RELATIONSHIP-011")]
    [InlineData("token-by-authorization-id", "missing-target-index", "GW-RELATIONSHIP-011")]
    public void Relationship_admission_rejects_wrong_or_missing_indexes(
        string sourceIndex,
        string targetIndex,
        string expectedCode)
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);
        var relationship = Assert.Single(fixture.Manifest.Relationships);
        var changed = fixture.Manifest with
        {
            Relationships =
            [
                new ManifestRelationshipDeclaration(
                    relationship.Identity,
                    relationship.SourceStorageUnit,
                    relationship.SourceReferencePath,
                    sourceIndex,
                    relationship.TargetStorageUnit,
                    relationship.TargetIdentityPath,
                    targetIndex,
                    relationship.ReferenceCasePolicy)
            ]
        };

        var result = PhysicalRelationshipPlanCompiler.Compile(CompileRelationshipRouteSet(changed));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Theory]
    [InlineData(true, false, "GW-RELATIONSHIP-010")]
    [InlineData(false, true, "GW-RELATIONSHIP-011")]
    public void Relationship_admission_rejects_non_leading_equality_indexes(
        bool nonLeadingSourceReference,
        bool nonLeadingTargetIdentity,
        string expectedCode)
    {
        var fixture = CreateRelationshipFixture(
            relatedTarget: false,
            nonLeadingSourceReference: nonLeadingSourceReference,
            nonLeadingTargetIdentity: nonLeadingTargetIdentity);

        var result = PhysicalRelationshipPlanCompiler.Compile(fixture.RouteSet);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void Related_target_guard_rejects_a_wrong_predicate_index()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: true);
        var changedMutation = new BoundedMutationDeclaration(
            "guarded-prune",
            "prune-tokens",
            BoundedMutationAction.Delete(),
            [BoundedMutationRelationshipGuard.RequireRelatedTargetNotEqual(
                "token-authorization",
                "status",
                "authorization-by-id",
                "valid")]);
        var storage = new StorageUnitPhysicalStorage(
            fixture.MutationStorage.ProvisioningMode,
            fixture.MutationStorage.Policy,
            fixture.MutationStorage.LogicalIndexes,
            fixture.MutationStorage.BoundedQueries,
            fixture.MutationStorage.NameOverrides,
            [changedMutation]);
        var manifest = fixture.Manifest with
        {
            StorageUnits = fixture.Manifest.StorageUnits
                .Select(unit => unit.Identity.Value == "token"
                    ? unit with { PhysicalStorage = storage }
                    : unit)
                .ToArray()
        };

        var result = PhysicalMutationPlanCompiler.Compile(
            CompileRelationshipRouteSet(manifest),
            fixture.MutationRoute,
            storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-MUTATION-012");
    }

    [Fact]
    public void Related_target_guard_rejects_a_non_leading_predicate_index()
    {
        var fixture = CreateRelationshipFixture(
            relatedTarget: true,
            nonLeadingTargetPredicate: true);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-MUTATION-012");
    }

    [Fact]
    public void Relationship_admission_rejects_same_unit_and_duplicate_relationship_topologies()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);
        var authorization = fixture.Manifest.StorageUnits.Single(unit => unit.Identity.Value == "authorization");
        var sameUnit = new ManifestRelationshipDeclaration(
            "authorization-self",
            authorization.Identity,
            "status",
            "authorization-by-status",
            authorization.Identity,
            PhysicalDocumentFieldPaths.Id,
            "authorization-by-id");
        var duplicate = Assert.Single(fixture.Manifest.Relationships);

        var sameUnitResult = PhysicalRelationshipPlanCompiler.Compile(
            CompileRelationshipRouteSet(fixture.Manifest with { Relationships = [sameUnit] }));
        var duplicateResult = ManifestExecutableRouteSetCompiler.Compile(
            fixture.Manifest with { Relationships = [duplicate, duplicate] },
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.Contains(sameUnitResult.Diagnostics, diagnostic => diagnostic.Code == "GW-RELATIONSHIP-005");
        Assert.False(duplicateResult.IsValid);
        Assert.Contains(duplicateResult.Diagnostics, diagnostic => diagnostic.Code == "GW-MANIFEST-006");
    }

    [Fact]
    public void Full_mutation_admission_rejects_rogue_local_route_and_storage_instances()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);
        var rogue = CreateRelationshipFixture(
            relatedTarget: false,
            authorizationPhysicalName: "rogue_authorizations");

        var routeResult = PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            rogue.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));
        var storageResult = PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            rogue.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.Contains(routeResult.Diagnostics, diagnostic => diagnostic.Code == "GW-MUTATION-019");
        Assert.Contains(storageResult.Diagnostics, diagnostic => diagnostic.Code == "GW-MUTATION-020");
    }

    [Fact]
    public void Complete_manifest_admission_accepts_only_resolver_compiled_sealed_route_sets()
    {
        Assert.Empty(typeof(ManifestExecutableRouteSet).GetConstructors());
        Assert.DoesNotContain(
            typeof(PhysicalRelationshipPlanCompiler).GetMethods(),
            method => method.Name == nameof(PhysicalRelationshipPlanCompiler.Compile) &&
                      method.GetParameters().Any(parameter =>
                          parameter.ParameterType == typeof(IReadOnlyList<ExecutableStorageRoute>)));
        Assert.DoesNotContain(
            typeof(PhysicalMutationPlanCompiler).GetMethods(),
            method => method.Name == nameof(PhysicalMutationPlanCompiler.Compile) &&
                      method.GetParameters().Any(parameter =>
                          parameter.ParameterType == typeof(IReadOnlyList<ExecutableStorageRoute>)));
    }

    [Fact]
    public void Complete_manifest_admission_snapshots_caller_owned_manifest_collections()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);
        var units = fixture.Manifest.StorageUnits.ToList();
        var relationships = fixture.Manifest.Relationships.ToList();
        var callerOwned = new StorageManifest(
            fixture.Manifest.Identity,
            fixture.Manifest.Owner,
            fixture.Manifest.Version,
            units,
            fixture.Manifest.RequiredCapabilities,
            fixture.Manifest.CompatibilityNotes)
        {
            SharedDocumentStorages = fixture.Manifest.SharedDocumentStorages,
            Relationships = relationships
        };
        var mutatedCallerCollections = false;
        var compilation = ManifestExecutableRouteSetCompiler.Compile(
            callerOwned,
            new DelegatePhysicalNamePolicy(context =>
            {
                if (!mutatedCallerCollections)
                {
                    units.Clear();
                    relationships.Clear();
                    mutatedCallerCollections = true;
                }
                return context.FeatureDefaultLogicalName;
            }),
            ProviderPhysicalNameNormalizer.Identity);
        var routeSet = Assert.IsType<ManifestExecutableRouteSet>(compilation.RouteSet);

        Assert.True(mutatedCallerCollections);
        var result = PhysicalRelationshipPlanCompiler.Compile(routeSet);
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Single(result.Plans);
    }

    [Fact]
    public void Mutation_plan_fingerprint_binds_both_relationship_route_fingerprints()
    {
        var baseline = CompileRelationshipMutationPlan(CreateRelationshipFixture(relatedTarget: false));
        var changedSource = CompileRelationshipMutationPlan(CreateRelationshipFixture(
            relatedTarget: false,
            tokenPhysicalName: "tokens_v2"));
        var changedTarget = CompileRelationshipMutationPlan(CreateRelationshipFixture(
            relatedTarget: false,
            authorizationPhysicalName: "authorizations_v2"));

        Assert.NotEqual(baseline.Fingerprint, changedSource.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, changedTarget.Fingerprint);
    }

    [Fact]
    public void Relationship_materialization_identity_survives_compatible_physical_renames()
    {
        var baseline = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(
                CreateRelationshipFixture(relatedTarget: false)).RelationshipGuards));
        var renamed = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(CreateRelationshipFixture(
                relatedTarget: false,
                authorizationPhysicalName: "authorizations_v2",
                tokenPhysicalName: "tokens_v2")).RelationshipGuards));

        Assert.Equal(baseline.Relationship.Materialization, renamed.Relationship.Materialization);
    }

    [Fact]
    public void Relationship_materialization_identity_rotates_on_semantic_case_policy_drift()
    {
        var baseline = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(
                CreateRelationshipFixture(relatedTarget: false)).RelationshipGuards));
        var changed = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(CreateRelationshipFixture(
                relatedTarget: false,
                targetIdentityCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase,
                referenceCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase))
                .RelationshipGuards));
        var changedSource = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(CreateRelationshipFixture(
                relatedTarget: false,
                sourceIdentityCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase))
                .RelationshipGuards));

        Assert.NotEqual(baseline.Relationship.Materialization, changed.Relationship.Materialization);
        Assert.NotEqual(baseline.Relationship.Materialization, changedSource.Relationship.Materialization);
    }

    [Fact]
    public void Relationship_materialization_identity_rotates_on_semantic_path_and_index_drift()
    {
        var baseline = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(
                CreateRelationshipFixture(relatedTarget: false)).RelationshipGuards));
        var changedSourcePath = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(CreateRelationshipFixture(
                relatedTarget: false,
                sourceReferencePath: "authorizationIdV2")).RelationshipGuards));
        var changedSourceIndex = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(CreateRelationshipFixture(
                relatedTarget: false,
                sourceReferenceIndexIdentity: "token-by-authorization-id-v2")).RelationshipGuards));
        var changedTargetIndex = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(CreateRelationshipFixture(
                relatedTarget: false,
                targetEqualityIndexIdentity: "authorization-by-id-v2")).RelationshipGuards));

        Assert.NotEqual(baseline.Relationship.Materialization, changedSourcePath.Relationship.Materialization);
        Assert.NotEqual(baseline.Relationship.Materialization, changedSourceIndex.Relationship.Materialization);
        Assert.NotEqual(baseline.Relationship.Materialization, changedTargetIndex.Relationship.Materialization);
    }

    [Fact]
    public void Relationship_materialization_identity_binds_scope_and_both_identity_algorithms()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);
        var baseline = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(fixture.RouteSet).Plans);
        var global = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(
                relatedTarget: false,
                authorizationTenancy: TenancyPolicy.Global,
                tokenTenancy: TenancyPolicy.Global).RouteSet).Plans);
        var expectedRoot = ExpectedRelationshipMaterializationRoot(fixture.Manifest, baseline);

        Assert.NotEqual(baseline.Materialization, global.Materialization);
        Assert.Equal(
            $"relationship-reference:{expectedRoot}",
            baseline.Materialization.ReferenceStorageIdentity);
        Assert.Equal(
            $"relationship-reference-by-source:{expectedRoot}",
            baseline.Materialization.ReferenceBySourceIndexIdentity);
        Assert.Equal(
            $"relationship-reference-by-target:{expectedRoot}",
            baseline.Materialization.ReferenceByTargetIndexIdentity);
        Assert.Equal(
            $"relationship-fence:{expectedRoot}",
            baseline.Materialization.FenceStorageIdentity);
        Assert.Equal(
            $"relationship-fence-by-target:{expectedRoot}",
            baseline.Materialization.FenceByTargetIndexIdentity);
    }

    [Fact]
    public void Relationship_guard_canonicalization_is_not_ambiguous_when_values_contain_legacy_delimiters()
    {
        var plan = CompileRelationshipMutationPlan(CreateRelationshipFixture(relatedTarget: true));
        var original = Assert.IsType<PhysicalRequireRelatedTargetNotEqualMutationGuard>(
            Assert.Single(plan.RelationshipGuards));
        var firstField = original.TargetPredicateField with { Path = "a\u001fb", Identifier = "c" };
        var secondField = original.TargetPredicateField with { Path = "a", Identifier = "b\u001fc" };
        var firstGuard = new PhysicalRequireRelatedTargetNotEqualMutationGuard(
            original.Relationship,
            firstField,
            original.TargetPredicateIndex,
            original.DisallowedTargetValue);
        var secondGuard = new PhysicalRequireRelatedTargetNotEqualMutationGuard(
            original.Relationship,
            secondField,
            original.TargetPredicateIndex,
            original.DisallowedTargetValue);

        Assert.NotEqual(firstGuard.CanonicalIdentity, secondGuard.CanonicalIdentity);
        Assert.NotEqual(
            (plan with { RelationshipGuards = [firstGuard] }).Fingerprint,
            (plan with { RelationshipGuards = [secondGuard] }).Fingerprint);
    }

    [Fact]
    public void Relationship_guard_fails_closed_without_provider_execution_certification()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);
        var plan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)).Plans);

        var exception = Assert.Throws<PhysicalRelationshipProviderNotSupportedException>(
            () => new PhysicalMutationHandlerCertification(plan));
        Assert.Equal(["token-authorization"], exception.RelationshipIdentities);
    }

    [Fact]
    public void Relationship_guard_rejects_a_missing_manifest_owned_relationship()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);
        var withoutRelationship = fixture.Manifest with { Relationships = [] };

        Assert.False(fixture.Manifest.HasSameDefinitionAs(withoutRelationship));

        var result = PhysicalMutationPlanCompiler.Compile(
            CompileRelationshipRouteSet(withoutRelationship),
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-MUTATION-016");
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

    private sealed class CapturingDocumentStore : IDocumentStore
    {
        public DocumentStoreAccess Access => DocumentStoreAccess.Global;
        public TransactionBoundary TransactionBoundary => TransactionBoundary.PerOperation;
#pragma warning disable GW0004
        public PortableDocumentQuery? LastQuery { get; private set; }
#pragma warning restore GW0004

        public Task<DocumentStoreWriteResult> SaveAsync(
            SaveDocumentRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DocumentEnvelope?> LoadAsync(
            string documentKind,
            string id,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DocumentStoreWriteResult> DeleteAsync(
            DeleteDocumentRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

#pragma warning disable GW0004
        public Task<IReadOnlyList<DocumentEnvelope>> QueryAsync(
            DocumentStoreQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DocumentQueryResult> QueryAsync(
            PortableDocumentQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(DocumentQueryResult.Empty);
        }

        public Task<DocumentEnvelope?> FirstOrDefaultAsync(
            PortableDocumentQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> AnyAsync(
            PortableDocumentQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
#pragma warning restore GW0004

        public Task<IDocumentUnitOfWork> BeginAsync(
            DocumentCommitScope scope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
