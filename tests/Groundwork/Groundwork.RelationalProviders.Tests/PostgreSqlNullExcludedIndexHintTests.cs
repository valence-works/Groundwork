using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.PostgreSql;
using Groundwork.PostgreSql.Documents;
using Groundwork.PostgreSql.PhysicalStorage;
using Groundwork.Relational.Documents;
using Xunit;
using Xunit.Abstractions;

namespace Groundwork.RelationalProviders.Tests;

/// <summary>
/// The other half of the valence-works/Groundwork#165 story: PostgreSQL pins no index and builds no
/// partial index, so the same routes never hit the defect. These assertions exist to keep that
/// divergence deliberate rather than incidental — if PostgreSQL ever starts pinning an index, or
/// starts honouring <see cref="Groundwork.Core.Indexing.MissingValueBehavior.Excluded"/> with a
/// partial index, it inherits the whole class of problem and these tests are what will say so.
/// </summary>
public sealed class PostgreSqlNullExcludedIndexHintTests(
    PostgreSqlPhysicalStorageContainer fixture,
    ITestOutputHelper output)
    : IClassFixture<PostgreSqlPhysicalStorageContainer>
{
    /// <summary>
    /// No pin, and therefore no implication conjunct: there is no index filter to imply. The mirror of
    /// the SQL Server case, where the same declaration does carry both.
    /// </summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public async Task Contains_on_a_unique_nullable_index_is_neither_pinned_nor_qualified(
        PhysicalStorageForm form)
    {
        var comparison = NullExcludedIndexScenario.Contains();
        var (rendered, runtime, column) = await PrepareAsync(form, comparison);
        output.WriteLine($"[{form}] {rendered.CommandText}");

        Assert.DoesNotContain("INDEX(", rendered.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("INDEXED BY", rendered.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain($"[{column}] IS NOT NULL", rendered.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{column}\" IS NOT NULL", rendered.CommandText, StringComparison.Ordinal);

        var result = await runtime.QueryAsync(NullExcludedIndexScenario.Query(comparison));
        Assert.Equal("present", Assert.Single(result.Documents).Id);
    }

    /// <summary>
    /// PostgreSQL indexes every row, so a null equality is answerable here and is not refused. On SQL
    /// Server the same query is refused, because there the rows it asks for are not in the index.
    /// </summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public async Task Null_equality_on_a_unique_nullable_index_is_answered_rather_than_refused(
        PhysicalStorageForm form)
    {
        var comparison = NullExcludedIndexScenario.NullEquality();
        var (_, runtime, _) = await PrepareAsync(form, comparison);

        var result = await runtime.QueryAsync(NullExcludedIndexScenario.Query(comparison));

        Assert.Equal("absent", Assert.Single(result.Documents).Id);
    }

    /// <summary>
    /// An index excluding two columns emits a two-conjunct filter, and PostgreSQL re-renders it with
    /// every conjunct parenthesised. Schema validation compares the filter it emitted against the one
    /// the catalog reports, so a single-column filter is not enough to prove the round trip: applying
    /// this model is the assertion.
    /// </summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public async Task Compound_null_excluding_index_survives_the_catalog_round_trip(PhysicalStorageForm form)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            form,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            priorityNullable: true,
            instance: Guid.NewGuid().ToString("N")[..8],
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames,
            categoryNullable: true,
            categoryMissingValues: MissingValueBehavior.Excluded,
            compoundMissingValues: MissingValueBehavior.Excluded);
        var connectionString = fixture.Container.GetConnectionString();
        var executor = new PostgreSqlPhysicalSchemaExecutor(connectionString);

        await PhysicalSchemaApplication.ApplyAsync(model.Target, executor);

        // Re-applying validates every emitted index against the catalog rather than recreating it.
        await PhysicalSchemaApplication.ApplyAsync(model.Target, executor);
    }

    private async Task<(RelationalPhysicalQueryCommand Rendered, IBoundedDocumentStore Runtime, string Column)>
        PrepareAsync(PhysicalStorageForm form, DocumentQueryComparison comparison)
    {
        var model = NullExcludedIndexScenario.Create(
            form,
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            unique: true,
            nullable: true,
            comparison);
        var connectionString = fixture.Container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target, new PostgreSqlPhysicalSchemaExecutor(connectionString));
        var store = new PostgreSqlPhysicalDocumentStore(
            connectionString, model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "present", "1", """{"category":"alpha"}"""));
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "absent", "1", "{}"));

        var route = model.Target.Routes.Single();
        return (
            RelationalPhysicalQueryRuntime.BuildQueryCommand(
                store,
                model.Manifest,
                route,
                model.Target.Provider,
                "postgresql",
                NullExcludedIndexScenario.Query(comparison)),
            PostgreSqlPhysicalQueryRuntime.Create(store, model.Manifest, route, model.Target.Provider),
            NullExcludedIndexScenario.CategoryColumn(route));
    }
}
