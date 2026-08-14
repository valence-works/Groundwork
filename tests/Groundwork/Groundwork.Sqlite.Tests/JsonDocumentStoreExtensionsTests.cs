using System.Text.Json;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

/// <summary>
/// Exercises the typed JSON convenience surface against a live physical store: an admitted SQLite
/// route with the shared metadata manifest's declared bounded queries ("find-by-key" and
/// "list-by-category").
/// </summary>
public sealed class JsonDocumentStoreExtensionsTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private IDocumentStore store = null!;
    private IBoundedDocumentStore queries = null!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        var manifest = SqliteTestManifests.MetadataManifest();
        var target = PhysicalSchemaTargetCompiler.Compile(
            manifest,
            SqliteTestManifests.Provider,
            SqliteGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var physical = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        store = physical;
        queries = SqlitePhysicalQueryRuntime.Create(
            physical,
            manifest,
            target.Routes.Single(),
            SqliteTestManifests.Provider);
    }

    public Task DisposeAsync() => connection.DisposeAsync().AsTask();

    [Fact]
    public async Task SaveJsonAndLoadJsonRoundTripATypedDocumentThroughThePhysicalStore()
    {
        var payload = new ConfigurationPayload("round-trip", "tools");

        var saved = await store.SaveJsonAsync(
            "configurationDocument", "round-trip", "1", payload, WebJson, expectedVersion: 0);

        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        Assert.Equal(1, saved.Document!.Version);
        Assert.Equal("""{"key":"round-trip","category":"tools"}""", saved.Document.ContentJson);
        Assert.Equal(payload, await store.LoadJsonAsync<ConfigurationPayload>(
            "configurationDocument", "round-trip", WebJson));
        Assert.Null(await store.LoadJsonAsync<ConfigurationPayload>(
            "configurationDocument", "absent", WebJson));
    }

    [Fact]
    public async Task QueryJsonAndFirstOrDefaultJsonExecuteDeclaredBoundedQueriesAsTypedDocuments()
    {
        await store.SaveJsonAsync(
            "configurationDocument", "a", "1", new ConfigurationPayload("key-a", "tools"), WebJson, 0);
        await store.SaveJsonAsync(
            "configurationDocument", "b", "1", new ConfigurationPayload("key-b", "tools"), WebJson, 0);

        var page = await queries.QueryJsonAsync<ConfigurationPayload>(
            new DocumentQuery(
                "configurationDocument",
                "list-by-category",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))],
                [new DocumentQueryOrder("category")]),
            WebJson);
        var first = await queries.FirstOrDefaultJsonAsync<ConfigurationPayload>(
            FindByKey("key-a").Select(BoundedQueryResultOperation.First), WebJson);
        var missing = await queries.FirstOrDefaultJsonAsync<ConfigurationPayload>(
            FindByKey("missing").Select(BoundedQueryResultOperation.First), WebJson);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(["key-a", "key-b"], page.Documents.Select(document => document.Key).Order());
        Assert.Equal(new ConfigurationPayload("key-a", "tools"), first);
        Assert.Null(missing);
    }

    [Fact]
    public void DeserializeJsonFailsClosedNamingIdAndKindWhenContentDeserializesToNull()
    {
        var envelope = new DocumentEnvelope(
            "configurationDocument", "null-content", "1", 1, "null",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            envelope.DeserializeJson<ConfigurationPayload>(WebJson));

        Assert.Contains("'null-content'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'configurationDocument'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ConfigurationPayload), exception.Message, StringComparison.Ordinal);
    }

    private static DocumentQuery FindByKey(string value) => new(
        "configurationDocument",
        "find-by-key",
        [DocumentQueryClause.Of(DocumentQueryComparison.Equal("key", value))]);

    private sealed record ConfigurationPayload(string Key, string Category);
}
