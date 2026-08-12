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
}
