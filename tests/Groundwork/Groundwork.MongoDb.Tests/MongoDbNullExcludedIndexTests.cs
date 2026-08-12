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
/// This is the provider the issue was filed about. MongoDB pins the partial index unconditionally and
/// appended <c>$exists</c> conjuncts to the caller's predicate to match it, so a scale-bearing query the
/// relational lane refused outright came back here short by exactly the documents the index omits — and
/// a bounded mutation left them unmutated. It now takes the same three-way decision as SQL Server and
/// SQLite, which for a pinned partial index means refusing.
/// </remarks>
public sealed class MongoDbNullExcludedIndexTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly MongoDbContainer container = new MongoDbBuilder("mongo:7.0.24")
        .WithReplicaSet("groundwork-rs")
        .Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

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
    /// which carries no partial filter and so excludes nothing. Before the fix the membership conjuncts
    /// skipped the document with no category anyway, reporting a lower affected count and leaving it
    /// behind.
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

        foreach (var unproven in NullExcludedIndexConformance.UnprovenCases)
        {
            var result = await cell.Documents.QueryAsync(
                NullExcludedIndexConformance.Query(unproven.Comparison));
            Assert.Equal(
                unproven.ExpectedIds.Order(),
                result.Documents.Select(document => document.Id).Order());
        }
    }

    private async Task<Cell> CreateAsync(
        PhysicalStorageForm form,
        MissingValueBehavior missingValues = MissingValueBehavior.Excluded,
        bool includeDelete = false)
    {
        var database = new MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var instance = Guid.NewGuid().ToString("N")[..8];
        var model = MongoDbPhysicalStorageModel.Compile(
            NullExcludedIndexConformance.CreateManifest(instance, form, missingValues, includeDelete));
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
