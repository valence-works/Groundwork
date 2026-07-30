using Groundwork.Core.PhysicalStorage;
using Groundwork.Sqlite;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkModelFactoryTests
{
    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments, true)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable, true)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable, false)]
    public void Model_compiles_the_scoped_mixed_direction_query_for_every_form(
        PhysicalStorageForm form,
        bool expectsLinkedStorage)
    {
        var model = BenchmarkModelFactory.CompileRelational(
            form,
            "fixed",
            SqliteGroundworkCapabilities.Provider,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.Equal(form, model.Route.Form);
        Assert.Equal(expectsLinkedStorage, model.Route.LinkedIndexStorage is not null);
        Assert.Equal(
            45,
            model.Route.ProjectedColumns.Single(column => column.Definition.Path == "status").Definition.Length);
        Assert.Collection(
            BenchmarkModelFactory.IndexFor(model.Route, ordered: false).Columns,
            scope => Assert.Equal("storage_scope", scope.Column.LogicalName),
            status => Assert.Equal(PhysicalSortDirection.Ascending, status.Direction),
            identity => Assert.EndsWith(
                expectsLinkedStorage ? "document_id_comparison_key" : "id_comparison_key",
                identity.Column.Identifier,
                StringComparison.Ordinal));
        Assert.Collection(
            BenchmarkModelFactory.IndexFor(model.Route, ordered: true).Columns,
            scope => Assert.Equal("storage_scope", scope.Column.LogicalName),
            status => Assert.Equal(PhysicalSortDirection.Ascending, status.Direction),
            rank => Assert.Equal(PhysicalSortDirection.Descending, rank.Direction),
            identity => Assert.EndsWith(
                expectsLinkedStorage ? "document_id_comparison_key" : "id_comparison_key",
                identity.Column.Identifier,
                StringComparison.Ordinal));
        Assert.Equal(
            [BenchmarkModelFactory.IndexedQueryIdentity, BenchmarkModelFactory.QueryIdentity],
            model.Manifest.StorageUnits.Single().PhysicalStorage!.BoundedQueries.Select(query => query.Identity));
        var fields = BenchmarkModelFactory.FieldBindingFor(model.Route);
        Assert.Equal(
            model.Route.LinkedRelationship?.StorageScope.Identifier ?? model.Route.ScopeKey.Column.Identifier,
            fields.StorageScope);
        Assert.Equal(
            model.Route.LinkedRelationship?.DocumentKind.Identifier ?? model.Route.Discriminator.Column.Identifier,
            fields.DocumentKind);
        Assert.EndsWith(
            expectsLinkedStorage ? "document_id_comparison_key" : "id_comparison_key",
            fields.IdentityComparison,
            StringComparison.Ordinal);
        var selection = new BenchmarkPlanRequest(
            BenchmarkWorkload.IndexedQuery,
            NativePlanOperation.Selection,
            Ordered: false,
            Skip: null,
            Take: 20);
        var count = selection with { Operation = NativePlanOperation.Count };
        Assert.Equal(
            [BenchmarkModelFactory.IndexedIndexIdentity],
            BenchmarkModelFactory.PlanIndexCandidatesFor(model.Route, selection)
                .Select(index => index.Identity));
        Assert.Equal(
            [BenchmarkModelFactory.IndexedIndexIdentity, BenchmarkModelFactory.CompoundIndexIdentity],
            BenchmarkModelFactory.PlanIndexCandidatesFor(model.Route, count)
                .Select(index => index.Identity));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public void Relational_model_uses_the_exact_production_factory_compilation(PhysicalStorageForm form)
    {
        const string instance = "factory-admission";
        var model = BenchmarkModelFactory.CompileRelational(
            form,
            instance,
            SqliteGroundworkCapabilities.Provider,
            SqliteGroundworkCapabilities.PhysicalNames);
        var factoryTarget = Groundwork.Core.SchemaEvolution.PhysicalSchemaTargetCompiler.Compile(
            model.Manifest,
            SqliteGroundworkCapabilities.Provider,
            SqliteGroundworkCapabilities.PhysicalNames,
            BenchmarkModelFactory.NamePolicy(instance));

        Assert.Equal(factoryTarget.Fingerprint, model.Target.Fingerprint);
        Assert.Equal(factoryTarget.Routes.Single().Indexes, model.Route.Indexes);
    }
}
