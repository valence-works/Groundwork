using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Text;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.MongoDb;
using Groundwork.MongoDb.Documents;
using Groundwork.PostgreSql;
using Groundwork.PostgreSql.Documents;
using Groundwork.PostgreSql.PhysicalStorage;
using Groundwork.SqlServer;
using Groundwork.SqlServer.Documents;
using Groundwork.SqlServer.PhysicalStorage;
using Groundwork.Sqlite;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Microsoft.Data.Sqlite;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.Differential.Tests;

public sealed class G2DifferentialAcceptanceTests
{
    [Fact]
    public void Corpus_has_the_issue_230_shape_and_decision_inventory()
    {
        Assert.Equal(G2DifferentialCorpus.ExpectedRowCount, G2DifferentialCorpus.Rows.Count);
        Assert.Equal(40, G2DifferentialCorpus.AcceptedRows.Count);
        Assert.Equal(2, G2DifferentialCorpus.RejectedRows.Count);
        Assert.Equal(G2DifferentialCorpus.ExpectedShapeCount, G2DifferentialCorpus.Shapes.Count);
        var shapeKeys = G2DifferentialCorpus.Shapes.Select(shape =>
                $"{shape.Kind}|{shape.QueryIdentity}|{shape.Skip}|{shape.Take}|" +
                string.Join(";", shape.Clauses.Select(clause => string.Join(",", clause.Comparisons.Select(comparison =>
                    $"{comparison.Path}:{comparison.Operator}:{comparison.Values.Count}:" +
                    string.Join("/", comparison.Values.Select(value => value is null ? "<NULL>" : $"<{value}>")))))) + "|" +
                string.Join(",", shape.Order.Select(order => $"{order.Path}:{order.Direction}"))).ToArray();
        var duplicateKeys = shapeKeys.GroupBy(key => key, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        Assert.True(duplicateKeys.Length == 0, $"duplicates={duplicateKeys.Length}{Environment.NewLine}{string.Join(Environment.NewLine, duplicateKeys)}");
        Assert.NotEmpty(G2DifferentialCorpus.Shapes.Where(shape => shape.Decision == G2SemanticDecision.Refuse));
        var comparisons = G2DifferentialCorpus.Shapes
            .SelectMany(shape => shape.Clauses)
            .SelectMany(clause => clause.Comparisons)
            .ToArray();
        Assert.Contains(comparisons, comparison =>
            comparison.Operator == QueryComparisonOperator.In && comparison.Values.Count == 0);
        Assert.Contains(comparisons, comparison =>
            comparison.Operator == QueryComparisonOperator.In &&
            comparison.Values.Any(value => value is null) &&
            comparison.Values.Any(value => value is not null));
        Assert.All(G2DifferentialCorpus.Shapes, shape =>
        {
            Assert.False(string.IsNullOrWhiteSpace(shape.DecisionId));
            Assert.False(string.IsNullOrWhiteSpace(shape.Description));
        });
    }

    [Fact]
    public void Oracle_is_independent_and_pins_every_refusal_family()
    {
        var refused = G2DifferentialCorpus.Shapes
            .Where(shape => shape.Decision == G2SemanticDecision.Refuse)
            .Select(shape => shape.DecisionId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("binary-order-refused", refused);
        Assert.Contains("binary-range-prefix-order-refused", refused);
        Assert.Contains("cross-provider-index-certification-refused", refused);
        Assert.Contains("explicit-null-ordering", G2DifferentialCorpus.Shapes.Select(shape => shape.DecisionId));
        Assert.Contains("typed-decimal-18-4", G2DifferentialCorpus.Shapes.Select(shape => shape.DecisionId));
        Assert.Contains("utc-ticks", G2DifferentialCorpus.Shapes.Select(shape => shape.DecisionId));
        Assert.Contains("rfc4122-network-guid-key", G2DifferentialCorpus.Shapes.Select(shape => shape.DecisionId));
    }
}

public sealed class G2DifferentialRejectedInputTests
{
    [Fact]
    public void Overlength_projection_is_rejected_before_provider_write()
    {
        var definition = new ProjectedColumnDefinition(
            "textSearch",
            "textSearch",
            PortablePhysicalType.String,
            Length: G2DifferentialCorpus.StringMaximumCodeUnits);
        var exception = Assert.Throws<PhysicalProjectionValueValidationException>(() =>
            PhysicalProjectionValueValidation.ValidateStringLength(
                new string('x', G2DifferentialCorpus.StringMaximumCodeUnits + 1),
                definition));
        Assert.Equal("GW-PHYSICAL-037", exception.Diagnostic.Code);
    }

    [Fact]
    public void Malformed_utf16_is_refused_before_search_key_generation()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            PortableStringComparison.CreateSearchKey(
                "\uD800",
                PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase));
        Assert.Contains("well-formed UTF-16", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decimal_shape_and_untyped_mixed_values_fail_closed()
    {
        Assert.Throws<InvalidDataException>(() =>
            ExactNumericLiteral.Parse("1.23456").ToDecimal(
                G2DifferentialCorpus.DecimalPrecision,
                G2DifferentialCorpus.DecimalScale,
                "numberValue"));
        Assert.False(PortableQueryOperationCompatibility.Supports(
            IndexValueKind.Number,
            PortablePhysicalType.String));
        Assert.False(PortableQueryOperationCompatibility.Supports(
            IndexValueKind.Number,
            PortablePhysicalType.Json));
    }

    [Fact]
    public void Refused_shapes_have_an_explicit_pre_io_guard()
    {
        var refused = G2DifferentialCorpus.Shapes
            .Where(shape => shape.Decision == G2SemanticDecision.Refuse)
            .ToArray();
        Assert.NotEmpty(refused);
        Assert.All(refused, shape =>
        {
            Assert.NotEqual(G2SemanticDecision.Normalize, shape.Decision);
            Assert.False(string.IsNullOrWhiteSpace(shape.DecisionId));
            var exception = Assert.Throws<G2SemanticRefusalException>(shape.ToDocumentQuery);
            Assert.Contains(shape.DecisionId, exception.Message, StringComparison.Ordinal);
        });
        Assert.All(
            G2DifferentialCorpus.Shapes.Where(shape => shape.DecisionId == "binary-order-refused"),
            shape => Assert.Empty(shape.Clauses));
        Assert.All(
            G2DifferentialCorpus.Shapes.Where(shape => shape.DecisionId == "binary-range-prefix-order-refused"),
            shape => Assert.All(
                shape.Clauses.SelectMany(clause => clause.Comparisons),
                comparison => Assert.DoesNotContain(
                    comparison.Operator,
                    new[] { QueryComparisonOperator.Equal, QueryComparisonOperator.In })));
    }
}

public sealed class G2DifferentialProviderMatrixTests(G2DifferentialProviderContainers containers)
    : IClassFixture<G2DifferentialProviderContainers>
{
    [Fact]
    public async Task Normalized_shapes_are_bit_identical_across_sqlite_postgresql_sqlserver_and_mongodb()
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        await using var sqlite = await OpenSqliteAsync(instance);
        await using var postgresql = await OpenPostgreSqlAsync(containers.PostgreSql.GetConnectionString(), instance);
        await using var sqlServer = await OpenSqlServerAsync(containers.SqlServer.GetConnectionString(), instance);
        await using var mongo = await OpenMongoAsync(containers.MongoDb.GetConnectionString(), instance);
        var providers = new[] { sqlite, postgresql, sqlServer, mongo };

        foreach (var provider in providers)
            await SeedAsync(provider);

        foreach (var shape in G2DifferentialCorpus.Shapes)
        {
            if (shape.Decision == G2SemanticDecision.Refuse)
                continue;
            var expected = G2Oracle.Evaluate(G2DifferentialCorpus.AcceptedRows, shape);
            var observations = new List<(string Provider, IReadOnlyList<string> Ids)>();
            for (var providerIndex = 0; providerIndex < providers.Length; providerIndex++)
            {
                var provider = providers[providerIndex];
                var result = await provider.Queries.QueryAsync(shape.ToDocumentQuery());
                var ids = result.Documents.Select(document => document.Id).ToArray();
                observations.Add((providerIndex.ToString(), ids));
                Assert.True(
                    expected.SequenceEqual(ids, StringComparer.Ordinal),
                    $"shape={shape.Number} decision={shape.DecisionId} provider={providerIndex} description={shape.Description}{Environment.NewLine}" +
                    $"expected={string.Join(',', expected)}{Environment.NewLine}actual={string.Join(',', ids)}");
                var unpaged = shape with { Skip = null, Take = null };
                Assert.Equal(G2Oracle.Evaluate(G2DifferentialCorpus.AcceptedRows, unpaged).Count, result.TotalCount);
            }
            var baseline = observations[0].Ids;
            Assert.All(observations.Skip(1), observation => Assert.Equal(baseline, observation.Ids));
        }
    }

    private static async Task SeedAsync(G2ProviderHandle provider)
    {
        foreach (var row in G2DifferentialCorpus.AcceptedRows)
        {
            var result = await provider.Writer.SaveAsync(new SaveDocumentRequest(
                G2DifferentialCorpus.DocumentKind,
                row.Id,
                "1",
                G2DifferentialCorpus.Serialize(row)));
            Assert.Equal(DocumentStoreWriteStatus.Saved, result.Status);
        }
    }

    private static async Task<G2ProviderHandle> OpenSqliteAsync(string instance)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var model = G2DifferentialModel.CompileRelational(
            instance,
            SqliteGroundworkCapabilities.Provider,
            SqliteGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlitePhysicalSchemaExecutor(connection));
        var store = new SqlitePhysicalDocumentStore(
            connection,
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Global);
        return new G2ProviderHandle(
            model.Manifest,
            model.Target.Routes.Single(),
            store,
            SqlitePhysicalQueryRuntime.Create(store, model.Manifest, model.Target.Routes.Single(), model.Target.Provider),
            connection.DisposeAsync);
    }

    private static async Task<G2ProviderHandle> OpenPostgreSqlAsync(string connectionString, string instance)
    {
        var model = G2DifferentialModel.CompileRelational(
            instance,
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new PostgreSqlPhysicalSchemaExecutor(connectionString));
        var store = new PostgreSqlPhysicalDocumentStore(
            connectionString,
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Global);
        return new G2ProviderHandle(
            model.Manifest,
            model.Target.Routes.Single(),
            store,
            PostgreSqlPhysicalQueryRuntime.Create(store, model.Manifest, model.Target.Routes.Single(), model.Target.Provider),
            () => ValueTask.CompletedTask);
    }

    private static async Task<G2ProviderHandle> OpenSqlServerAsync(string connectionString, string instance)
    {
        var model = G2DifferentialModel.CompileRelational(
            instance,
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlServerPhysicalSchemaExecutor(connectionString));
        var store = new SqlServerPhysicalDocumentStore(
            connectionString,
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Global);
        return new G2ProviderHandle(
            model.Manifest,
            model.Target.Routes.Single(),
            store,
            SqlServerPhysicalQueryRuntime.Create(store, model.Manifest, model.Target.Routes.Single(), model.Target.Provider),
            () => ValueTask.CompletedTask);
    }

    private static async Task<G2ProviderHandle> OpenMongoAsync(string connectionString, string instance)
    {
        var manifest = G2DifferentialCorpus.CreateManifest(instance);
        var namePolicy = new DelegatePhysicalNamePolicy(context => $"gw_{instance}_{context.FeatureDefaultLogicalName}");
        var handle = await MongoDbDocumentStoreFactory.CreatePhysicalAsync(
            connectionString,
            "g2_" + instance,
            manifest,
            MongoDbGroundworkCapabilities.Provider,
            DocumentStoreAccess.Global,
            namePolicy);
        var store = handle.Store;
        return new G2ProviderHandle(
            handle.Model.Manifest,
            handle.Model.Routes.Single(),
            store,
            store,
            handle.DisposeAsync);
    }

    private sealed class G2ProviderHandle(
        StorageManifest manifest,
        ExecutableStorageRoute route,
        IDocumentStore writer,
        IBoundedDocumentStore queries,
        Func<ValueTask> dispose)
        : IAsyncDisposable
    {
        public StorageManifest Manifest { get; } = manifest;
        public ExecutableStorageRoute Route { get; } = route;
        public IDocumentStore Writer { get; } = writer;
        public IBoundedDocumentStore Queries { get; } = queries;
        public ValueTask DisposeAsync() => dispose();
    }
}
