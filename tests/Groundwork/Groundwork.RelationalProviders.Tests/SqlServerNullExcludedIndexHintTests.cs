using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Provider.Relational;
using Groundwork.Relational.Documents;
using Groundwork.SqlServer;
using Groundwork.SqlServer.Documents;
using Groundwork.SqlServer.PhysicalStorage;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace Groundwork.RelationalProviders.Tests;

/// <summary>
/// Pins how the SQL Server dialect pins an index whose filter excludes null rows.
/// </summary>
/// <remarks>
/// A unique index over a nullable column carries a <c>WHERE column IS NOT NULL</c> filter, and SQL
/// Server refuses to plan (error 8622) when a pinned filtered index's filter is not implied by the
/// query. That is what broke every substring search route in valence-works/Groundwork#165. The cells
/// below vary one ingredient at a time so a future change cannot quietly re-break the class:
/// filtered-ness is the discriminating variable, not the shape of the predicate. In particular
/// <see cref="Contains_on_an_unfiltered_index_is_pinned_without_an_implication_conjunct"/> is the
/// control that rules out "<c>LOWER(...) LIKE</c> is non-sargable" as the cause, and
/// <see cref="Null_equality_on_a_null_excluding_index_is_refused"/> shows an equality failing the
/// same way, which no sargability story explains.
/// </remarks>
public sealed class SqlServerNullExcludedIndexHintTests(
    SqlServerNullExcludedIndexContainer fixture,
    ITestOutputHelper output)
    : IClassFixture<SqlServerNullExcludedIndexContainer>
{
    /// <summary>
    /// The control. An index declared <see cref="MissingValueBehavior.IncludedAsNull"/> carries no
    /// filter, so the substring predicate is pinned exactly as before and needs no implication conjunct.
    /// Proves the defect was never about sargability.
    /// </summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public Task Contains_on_an_unfiltered_index_is_pinned_without_an_implication_conjunct(
        PhysicalStorageForm form) =>
        AssertPinnedAsync(
            form, unique: false, nullable: true, Contains(), expectFiltered: false,
            missingValues: MissingValueBehavior.IncludedAsNull);

    /// <summary>
    /// Uniqueness no longer decides whether rows are excluded, so a unique index that keeps every row
    /// carries no filter either. This is the pairing that used to be impossible to express.
    /// </summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public Task Contains_on_a_unique_index_that_keeps_every_row_carries_no_filter(
        PhysicalStorageForm form) =>
        AssertPinnedAsync(
            form, unique: true, nullable: false, Contains(), expectFiltered: false,
            missingValues: MissingValueBehavior.IncludedAsNull);

    /// <summary>An index over a non-nullable column has nothing to exclude, whatever it declares.</summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public Task Contains_on_a_non_nullable_index_is_pinned_without_an_implication_conjunct(
        PhysicalStorageForm form) =>
        AssertPinnedAsync(form, unique: true, nullable: false, Contains(), expectFiltered: false);

    /// <summary>
    /// The regression. A substring predicate rejects nulls, so the null-excluding index still serves it —
    /// the query keeps its pin and gains the conjunct that lets SQL Server match the index filter.
    /// </summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public Task Contains_on_a_null_excluding_index_keeps_its_pin_via_an_implication_conjunct(
        PhysicalStorageForm form) =>
        AssertPinnedAsync(form, unique: true, nullable: true, Contains(), expectFiltered: true);

    /// <summary>Equality rejects nulls too, and takes the same path.</summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public Task Equality_on_a_null_excluding_index_keeps_its_pin_via_an_implication_conjunct(
        PhysicalStorageForm form) =>
        AssertPinnedAsync(form, unique: true, nullable: true, Equality(), expectFiltered: true);

    /// <summary>
    /// A null equality asks for exactly the rows the index filter removes, so no conjunct can rescue the
    /// pin. A scale-bearing route is refused by name rather than degraded to a scan or left to fail as
    /// an opaque provider error.
    /// </summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public async Task Null_equality_on_a_null_excluding_index_is_refused(PhysicalStorageForm form)
    {
        var cell = await PrepareAsync(form, unique: true, nullable: true, NullEquality());
        Assert.True(await ReadHasFilterAsync(cell));

        var failure = Assert.Throws<InvalidOperationException>(() => cell.Render(NullEquality()));
        output.WriteLine($"[{form}] {failure.Message}");
        Assert.Contains("list-by-category", failure.Message, StringComparison.Ordinal);
        Assert.Contains(cell.IndexName, failure.Message, StringComparison.Ordinal);
        Assert.Contains(cell.CategoryColumn, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty membership set renders a contradiction, so the query returns nothing no matter which
    /// index serves it. It must therefore keep its pin rather than be refused: a predicate that returns
    /// no rows cannot drop any. Regressed once by reading the contradiction as "proves nothing".
    /// </summary>
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public async Task Empty_membership_on_a_null_excluding_index_matches_nothing_without_refusal(
        PhysicalStorageForm form)
    {
        var comparison = DocumentQueryComparison.In("category", []);
        var cell = await PrepareAsync(form, unique: true, nullable: true, comparison);
        Assert.True(await ReadHasFilterAsync(cell));

        var rendered = cell.Render(comparison);
        output.WriteLine($"[{form}] {rendered.CommandText}");
        Assert.Contains($"WITH (INDEX([{cell.IndexName}]))", rendered.CommandText, StringComparison.Ordinal);

        var result = await cell.Runtime.QueryAsync(NullExcludedIndexScenario.Query(comparison));

        Assert.Empty(result.Documents);
    }

    private async Task AssertPinnedAsync(
        PhysicalStorageForm form,
        bool unique,
        bool nullable,
        DocumentQueryComparison comparison,
        bool expectFiltered,
        MissingValueBehavior missingValues = MissingValueBehavior.Excluded)
    {
        var cell = await PrepareAsync(form, unique, nullable, comparison, missingValues);

        // Read the filter off the catalog rather than assuming it. Together with the conjunct assertion
        // below this is the drift guard between the two sides that must agree: the schema executor
        // decides whether to emit an index filter, the document dialect decides whether the query must
        // qualify for it. If either side silently stops, one of these two assertions fails.
        Assert.Equal(expectFiltered, await ReadHasFilterAsync(cell));

        var rendered = cell.Render(comparison);
        output.WriteLine($"[{form}] unique={unique} nullable={nullable} op={comparison.Operator}");
        output.WriteLine($"[{form}] {rendered.CommandText}");

        Assert.Contains($"WITH (INDEX([{cell.IndexName}]))", rendered.CommandText, StringComparison.Ordinal);
        var conjunct = $"{cell.Alias}.[{cell.CategoryColumn}] IS NOT NULL";
        if (expectFiltered)
            Assert.Contains(conjunct, rendered.CommandText, StringComparison.Ordinal);
        else
            Assert.DoesNotContain(conjunct, rendered.CommandText, StringComparison.Ordinal);

        // The pin has to survive contact with the query processor, which is what 8622 denied.
        var result = await cell.Runtime.QueryAsync(NullExcludedIndexScenario.Query(comparison));
        Assert.Equal("present", Assert.Single(result.Documents).Id);
    }

    private async Task<Cell> PrepareAsync(
        PhysicalStorageForm form,
        bool unique,
        bool nullable,
        DocumentQueryComparison comparison,
        MissingValueBehavior missingValues = MissingValueBehavior.Excluded)
    {
        var model = NullExcludedIndexScenario.Create(
            form,
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            unique,
            nullable,
            comparison,
            missingValues);
        var connectionString = fixture.Container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlServerPhysicalSchemaExecutor(connectionString));
        var store = new SqlServerPhysicalDocumentStore(
            connectionString, model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "present", "1", """{"category":"alpha"}"""));

        var route = model.Target.Routes.Single();
        var index = NullExcludedIndexScenario.CategoryIndex(route);
        var linked = index.Target == ExecutableStorageObjectRole.LinkedIndexStorage;
        return new Cell(
            connectionString,
            index.Name.Identifier,
            linked ? "l" : "p",
            linked ? route.LinkedIndexStorage!.Name.Identifier : route.PrimaryStorage.Name.Identifier,
            NullExcludedIndexScenario.CategoryColumn(route),
            SqlServerPhysicalQueryRuntime.Create(store, model.Manifest, route, model.Target.Provider),
            given => RelationalPhysicalQueryRuntime.BuildQueryCommand(
                store,
                model.Manifest,
                route,
                model.Target.Provider,
                "sqlserver",
                NullExcludedIndexScenario.Query(given)));
    }

    private static async Task<bool> ReadHasFilterAsync(Cell cell)
    {
        await using var connection = new SqlConnection(cell.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT has_filter FROM sys.indexes WHERE object_id = OBJECT_ID(@table) AND name = @index;";
        command.Parameters.AddWithValue("@table", cell.IndexTable);
        command.Parameters.AddWithValue("@index", cell.IndexName);
        return Convert.ToBoolean(Assert.IsType<bool>(await command.ExecuteScalarAsync()));
    }

    private static DocumentQueryComparison Contains() => NullExcludedIndexScenario.Contains();
    private static DocumentQueryComparison Equality() => NullExcludedIndexScenario.Equality();
    private static DocumentQueryComparison NullEquality() => NullExcludedIndexScenario.NullEquality();

    private sealed record Cell(
        string ConnectionString,
        string IndexName,
        string Alias,
        string IndexTable,
        string CategoryColumn,
        IBoundedDocumentStore Runtime,
        Func<DocumentQueryComparison, RelationalPhysicalQueryCommand> Render);
}

public sealed class SqlServerNullExcludedIndexContainer : IAsyncLifetime
{
    public Testcontainers.MsSql.MsSqlContainer Container { get; } =
        new Testcontainers.MsSql.MsSqlBuilder(Groundwork.TestInfrastructure.TestContainerImages.SqlServer).Build();

    public Task InitializeAsync() => Container.StartAsync();
    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}
