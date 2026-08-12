using System.Collections.Concurrent;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Testcontainers.MongoDb;
using Xunit;
using Xunit.Abstractions;

namespace Groundwork.MongoDb.Tests;

/// <summary>
/// Pins how MongoDB pins an index whose partial filter omits documents that have no value.
/// </summary>
/// <remarks>
/// <para>
/// valence-works/Groundwork#166. <see cref="MissingValueBehavior.Excluded"/> is emitted as
/// <c>{field: {$exists: true}}</c>, and MongoDB — unlike SQL Server, which refuses the plan with error
/// 8622 — accepts a hint the query does not imply, answers from the smaller index, and returns a short
/// result set with no error at all. The cells below vary one ingredient at a time so a future change
/// cannot quietly re-break the class: filtered-ness is the discriminating variable, not the shape of
/// the predicate, which is what
/// <see cref="Not_contains_on_an_unfiltered_index_keeps_its_pin_and_every_document"/> is the control for.
/// </para>
/// <para>
/// Every cell seeds a document whose indexed field is absent and asserts three things about it: what
/// the schema emitted, what the rendered command decided, and what execution returned. That absent
/// document is what the whole class of defect hinges on — a cell without it passes while testing
/// nothing, because every other document is in the index either way.
/// </para>
/// </remarks>
public sealed class MongoDbNullExcludedIndexHintTests(ITestOutputHelper output, MongoDbReplicaSetTestContainer fixture)
    : IClassFixture<MongoDbReplicaSetTestContainer>
{
    private readonly MongoDbContainer container = fixture.Container;

    /// <summary>
    /// The control. An index that keeps every document carries no partial filter, so the equality is
    /// pinned exactly as before and states nothing about presence.
    /// </summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public async Task Equality_on_an_unfiltered_index_is_pinned_without_a_presence_conjunct(
        PhysicalStorageForm form)
    {
        var cell = await PrepareAsync(form, MissingValueBehavior.IncludedAsNull, [PortableQueryOperation.Equal]);
        Assert.False(await cell.IsPartialAsync());

        var result = await cell.QueryAsync(DocumentQueryComparison.Equal("status", "ready"));

        Assert.Equal("present", Assert.Single(result.Documents).Id);
        cell.AssertPinned();
        Assert.All(cell.Commands, command => Assert.False(StatesPresence(command, cell.StatusField)));
    }

    /// <summary>
    /// The equality can only match documents that have the field, so the partial index still serves it:
    /// the query keeps its pin and gains the conjunct that states the implication the filter assumes.
    /// </summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public async Task Equality_on_a_null_excluding_index_keeps_its_pin_via_a_presence_conjunct(
        PhysicalStorageForm form)
    {
        var cell = await PrepareAsync(form, MissingValueBehavior.Excluded, [PortableQueryOperation.Equal]);
        Assert.True(await cell.IsPartialAsync());

        var result = await cell.QueryAsync(DocumentQueryComparison.Equal("status", "ready"));

        Assert.Equal("present", Assert.Single(result.Documents).Id);
        cell.AssertPinned();
        Assert.All(cell.Commands, command => Assert.True(StatesPresence(command, cell.StatusField)));
    }

    /// <summary>
    /// The other control, and the one that rules out "NotContains is simply unpinnable" as the story:
    /// over an unfiltered index the same predicate keeps its pin, and the document with no field comes
    /// back, which is what NotContains is documented to do.
    /// </summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public async Task Not_contains_on_an_unfiltered_index_keeps_its_pin_and_every_document(
        PhysicalStorageForm form)
    {
        var cell = await PrepareAsync(
            form,
            MissingValueBehavior.IncludedAsNull,
            [PortableQueryOperation.Equal, PortableQueryOperation.NotContains],
            BoundedQueryExecutionClass.Ordinary);
        Assert.False(await cell.IsPartialAsync());

        var result = await cell.QueryAsync(DocumentQueryComparison.NotContains("status", "ready"));

        Assert.Equal("absent", Assert.Single(result.Documents).Id);
        cell.AssertPinned();
    }

    /// <summary>
    /// The regression. NotContains matches a document whose field is absent, which is precisely what the
    /// partial index omits, so the pin has to go rather than the document. An ordinary query is entitled
    /// to fall back to whatever index MongoDB chooses; a scale-bearing one is refused instead, below.
    /// </summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public async Task Not_contains_on_a_null_excluding_index_drops_its_pin_rather_than_documents(
        PhysicalStorageForm form)
    {
        var cell = await PrepareAsync(
            form,
            MissingValueBehavior.Excluded,
            [PortableQueryOperation.Equal, PortableQueryOperation.NotContains],
            BoundedQueryExecutionClass.Ordinary);
        Assert.True(await cell.IsPartialAsync());

        var result = await cell.QueryAsync(DocumentQueryComparison.NotContains("status", "ready"));

        // The load-bearing assertion of the whole file: before the fix this returned nothing at all,
        // because the pinned partial index does not hold the one document that matches.
        Assert.Equal("absent", Assert.Single(result.Documents).Id);
        Assert.Equal(1, result.TotalCount);
        cell.AssertUnpinned();
        Assert.All(cell.Commands, command => Assert.False(StatesPresence(command, cell.StatusField)));
    }

    /// <summary>
    /// A null equality asks for the documents with no field as well, so no conjunct can rescue the pin.
    /// A scale-bearing route is refused by name rather than degraded to a scan or, as MongoDB would
    /// otherwise have it, answered with fewer documents than the predicate matches. Explain has to
    /// refuse identically: evidence that describes a command execution would not issue is worthless.
    /// </summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public async Task Null_equality_on_a_null_excluding_index_is_refused(PhysicalStorageForm form)
    {
        var cell = await PrepareAsync(form, MissingValueBehavior.Excluded, [PortableQueryOperation.Equal]);
        Assert.True(await cell.IsPartialAsync());
        var query = Query(DocumentQueryComparison.Equal("status", null));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => cell.Store.QueryAsync(query));

        output.WriteLine($"[{form}] {failure.Message}");
        Assert.Contains("list-by-status", failure.Message, StringComparison.Ordinal);
        Assert.Contains(cell.IndexName, failure.Message, StringComparison.Ordinal);
        Assert.Contains(cell.StatusField, failure.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cell.Store.ExplainAsync(query));
    }

    /// <summary>
    /// An empty membership set matches nothing whichever index serves it, so it keeps its pin rather
    /// than being refused: a predicate that returns no documents cannot omit any. The relational side
    /// regressed here once by reading the contradiction as "proves nothing".
    /// </summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public async Task Empty_membership_on_a_null_excluding_index_matches_nothing_without_refusal(
        PhysicalStorageForm form)
    {
        var cell = await PrepareAsync(
            form,
            MissingValueBehavior.Excluded,
            [PortableQueryOperation.Equal, PortableQueryOperation.In]);
        Assert.True(await cell.IsPartialAsync());

        var result = await cell.QueryAsync(DocumentQueryComparison.In("status", []));

        Assert.Empty(result.Documents);
        cell.AssertPinned();
    }

    private static DocumentQuery Query(DocumentQueryComparison comparison) =>
        new("workItem", "list-by-status", [DocumentQueryClause.Of(comparison)]);

    private async Task<Cell> PrepareAsync(
        PhysicalStorageForm form,
        MissingValueBehavior missingValues,
        PortableQueryOperation[] operations,
        BoundedQueryExecutionClass executionClass = BoundedQueryExecutionClass.ScaleBearing)
    {
        var captured = new ConcurrentQueue<BsonDocument>();
        var settings = MongoClientSettings.FromConnectionString(container.GetConnectionString());
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(started =>
            captured.Enqueue(started.Command.DeepClone().AsBsonDocument));
        var database = new MongoClient(settings).GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = MongoDbPhysicalStorageConformanceTests.Model(
            form,
            operations.ToHashSet(),
            executionClass,
            isNullable: true,
            missingValueBehavior: missingValues);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new MongoDbPhysicalSchemaExecutor(database));
        var store = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-a")));
        await store.SaveAsync(new SaveDocumentRequest("workItem", "present", "1", """{"status":"ready"}"""));
        await store.SaveAsync(new SaveDocumentRequest("workItem", "absent", "1", "{}"));

        var route = Assert.Single(model.Routes);
        var index = Assert.Single(route.Indexes);
        var lookup = index.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        return new Cell(
            store,
            index.Name.Identifier,
            // Read off the route rather than taken as the index's last column: a non-unique index
            // appends an identity tie-break, which is not the column the filter is about.
            route.ProjectedColumns
                .Single(column => column.Target == index.Target && column.Definition.Path == "status")
                .Column.Identifier,
            database.GetCollection<BsonDocument>(lookup),
            captured,
            output);
    }

    /// <summary>
    /// Whether the rendered command states anywhere that <paramref name="field"/> has to be present.
    /// Searched rather than matched positionally, because where the driver puts the conjunct depends on
    /// which other conjuncts name the same field.
    /// </summary>
    private static bool StatesPresence(BsonValue rendered, string field) => rendered switch
    {
        BsonDocument document => document.Elements.Any(element =>
            (element.Name == field &&
             element.Value is BsonDocument condition &&
             condition.TryGetValue("$exists", out var exists) &&
             exists == BsonBoolean.True) ||
            StatesPresence(element.Value, field)),
        BsonArray array => array.Any(item => StatesPresence(item, field)),
        _ => false
    };

    /// <summary>One prepared route, and the commands the last query issued against its lookup object.</summary>
    private sealed record Cell(
        MongoDbPhysicalDocumentStore Store,
        string IndexName,
        string StatusField,
        IMongoCollection<BsonDocument> Lookup,
        ConcurrentQueue<BsonDocument> Captured,
        ITestOutputHelper Output)
    {
        public async Task<DocumentQueryResult> QueryAsync(DocumentQueryComparison comparison)
        {
            Captured.Clear();
            var result = await Store.QueryAsync(Query(comparison));
            foreach (var command in Commands)
                Output.WriteLine(command.ToJson());
            return result;
        }

        /// <summary>
        /// The lookup-object reads of the last query. Primary hydration is deliberately excluded: it is
        /// an identity lookup that no index pin applies to, so it would only add noise.
        /// </summary>
        public IReadOnlyList<BsonDocument> Commands =>
            Captured
                .Select(command => command.Contains("explain") ? command["explain"].AsBsonDocument : command)
                .Where(command =>
                    (command.TryGetValue("find", out var find) && find == Lookup.CollectionNamespace.CollectionName) ||
                    (command.TryGetValue("aggregate", out var aggregate) &&
                     aggregate == Lookup.CollectionNamespace.CollectionName))
                .ToArray();

        public void AssertPinned()
        {
            Assert.NotEmpty(Commands);
            Assert.All(Commands, command => Assert.Equal(IndexName, command["hint"].AsString));
        }

        public void AssertUnpinned()
        {
            Assert.NotEmpty(Commands);
            Assert.All(Commands, command => Assert.False(command.Contains("hint")));
        }

        public async Task<bool> IsPartialAsync() =>
            (await (await Lookup.Indexes.ListAsync()).ToListAsync())
            .Single(document => document["name"].AsString == IndexName)
            .Contains("partialFilterExpression");
    }
}
