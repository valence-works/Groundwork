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
}
