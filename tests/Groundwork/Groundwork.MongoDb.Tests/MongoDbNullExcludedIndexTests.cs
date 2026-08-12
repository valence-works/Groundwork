using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using Groundwork.TestInfrastructure;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Xunit;
using Xunit.Abstractions;

namespace Groundwork.MongoDb.Tests;

/// <summary>
/// MongoDB's arm of the cross-provider row-visibility contract (valence-works/Groundwork#178).
/// </summary>
/// <remarks>
/// <c>MongoDbNullExcludedIndexHintTests</c> already pins how this provider decides the hint, down to the
/// emitted index and the rendered command. What this adds is the cross-provider half the issue asked for:
/// the same manifest, documents, and predicates that SQLite, SQL Server, and PostgreSQL run, asserted
/// against the row set alone so the four answers can be compared rather than each described in its own
/// terms.
/// </remarks>
public sealed class MongoDbNullExcludedIndexTests(ITestOutputHelper output, MongoDbReplicaSetTestContainer fixture)
    : IClassFixture<MongoDbReplicaSetTestContainer>
{
    private readonly MongoDbContainer container = fixture.Container;

    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    public async Task Null_excluding_index_never_answers_with_fewer_rows_than_the_predicate_matches(
        PhysicalStorageForm form)
    {
        var cell = await CreateAsync(form);

        await NullExcludedIndexConformance.VerifyQueriesAsync(cell.Documents, cell.Route, output.WriteLine);
    }

    /// <summary>
    /// The mutation lane does not refuse, and must not: it is pinned to the provider-owned mirror index,
    /// which carries no partial filter and so excludes nothing. The relational lane does refuse, because
    /// its selection predicate pins the route's own filtered index — the same split the query lane shows
    /// between the providers that pin and the one that does not.
    /// </summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public async Task Null_excluding_index_never_leaves_a_matched_row_unmutated(PhysicalStorageForm form)
    {
        var cell = await CreateAsync(form, includeDelete: true);

        await NullExcludedIndexConformance.VerifyBoundedDeleteAsync(
            cell.Documents,
            MongoDbPhysicalMutationRuntime.Create(
                cell.Documents,
                cell.Model.Manifest,
                cell.Route,
                cell.Model.Provider),
            cell.Route,
            output.WriteLine);
    }

    /// <summary>
    /// The control that keeps the refusals above attributable to row exclusion alone: the same manifest
    /// declaring an index that keeps every row answers every predicate.
    /// </summary>
    [Fact]
    public async Task An_index_that_keeps_every_row_answers_every_predicate()
    {
        var cell = await CreateAsync(
            PhysicalStorageForm.PhysicalEntityTable,
            missingValues: MissingValueBehavior.IncludedAsNull);

        await NullExcludedIndexConformance.VerifyQueriesAsync(cell.Documents, cell.Route, output.WriteLine);
    }

    /// <summary>
    /// The third branch, and the only one where the answer rather than the refusal is the assertion. An
    /// ordinary query has no scale guarantee to abandon, so the hint is given up instead of refused — and
    /// the presence conjuncts have to go with it, since either alone would still drop the document with
    /// no category.
    /// </summary>
    [Fact]
    public async Task An_ordinary_query_gives_up_the_hint_rather_than_dropping_documents()
    {
        var cell = await CreateAsync(
            PhysicalStorageForm.PhysicalEntityTable,
            executionClass: BoundedQueryExecutionClass.Ordinary);

        await NullExcludedIndexConformance.VerifyQueriesAsync(cell.Documents, cell.Route, output.WriteLine);
    }

    private async Task<Cell> CreateAsync(
        PhysicalStorageForm form,
        MissingValueBehavior missingValues = MissingValueBehavior.Excluded,
        bool includeDelete = false,
        BoundedQueryExecutionClass executionClass = BoundedQueryExecutionClass.ScaleBearing)
    {
        var database = new MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var instance = Guid.NewGuid().ToString("N")[..8];
        var model = MongoDbPhysicalStorageModel.Compile(
            NullExcludedIndexConformance.CreateManifest(
                instance, form, missingValues, includeDelete, executionClass));
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var documents = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Global);
        await NullExcludedIndexConformance.SeedAsync(documents);
        return new Cell(model, model.Routes.Single(), documents);
    }

    private sealed record Cell(
        MongoDbPhysicalStorageModel Model,
        ExecutableStorageRoute Route,
        MongoDbPhysicalDocumentStore Documents);
}
