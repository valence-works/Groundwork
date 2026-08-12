using Groundwork.Core.Capabilities;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Groundwork.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Groundwork.Sqlite.Tests;

/// <summary>
/// SQLite's arm of the cross-provider row-visibility contract (valence-works/Groundwork#178). It pins an
/// index with <c>INDEXED BY</c> and realises row exclusion as a partial index, so it is the arm that
/// refuses rather than the one that answers.
/// </summary>
public sealed class SqliteNullExcludedIndexTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public async Task Null_excluding_index_never_answers_with_fewer_rows_than_the_predicate_matches(
        PhysicalStorageForm form)
    {
        await using var cell = await CreateAsync(form);

        await NullExcludedIndexConformance.VerifyQueriesAsync(
            cell.Queries,
            cell.Route,
            output.WriteLine);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public async Task Null_excluding_index_never_leaves_a_matched_row_unmutated(PhysicalStorageForm form)
    {
        await using var cell = await CreateAsync(form, includeDelete: true);

        await NullExcludedIndexConformance.VerifyBoundedDeleteAsync(
            cell.Documents,
            SqlitePhysicalMutationRuntime.Create(
                cell.Documents,
                cell.Manifest,
                cell.Route,
                SqliteTestManifests.Provider),
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
        await using var cell = await CreateAsync(
            PhysicalStorageForm.PhysicalEntityTable,
            missingValues: MissingValueBehavior.IncludedAsNull);

        foreach (var unproven in NullExcludedIndexConformance.UnprovenCases)
        {
            var result = await cell.Queries.QueryAsync(
                NullExcludedIndexConformance.Query(unproven.Comparison));
            Assert.Equal(
                unproven.ExpectedIds.Order(),
                result.Documents.Select(document => document.Id).Order());
        }
    }

    private static async Task<Cell> CreateAsync(
        PhysicalStorageForm form,
        MissingValueBehavior missingValues = MissingValueBehavior.Excluded,
        bool includeDelete = false)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var manifest = NullExcludedIndexConformance.CreateManifest(instance, form, missingValues, includeDelete);
        var target = NullExcludedIndexConformance.CreateTarget(
            manifest,
            SqliteTestManifests.Provider,
            ProviderPhysicalNameNormalizer.Identity,
            instance);
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var documents = new SqlitePhysicalDocumentStore(
            connection, manifest, target.Routes, DocumentStoreAccess.Global);
        await NullExcludedIndexConformance.SeedAsync(documents);
        var route = target.Routes.Single();
        return new Cell(
            connection,
            manifest,
            route,
            documents,
            SqlitePhysicalQueryRuntime.Create(documents, manifest, route, target.Provider));
    }

    private sealed record Cell(
        SqliteConnection Connection,
        StorageManifest Manifest,
        ExecutableStorageRoute Route,
        SqlitePhysicalDocumentStore Documents,
        IBoundedDocumentStore Queries) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
