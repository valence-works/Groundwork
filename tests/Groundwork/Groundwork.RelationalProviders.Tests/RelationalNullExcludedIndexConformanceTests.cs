using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.PostgreSql;
using Groundwork.PostgreSql.Documents;
using Groundwork.PostgreSql.PhysicalStorage;
using Groundwork.SqlServer;
using Groundwork.SqlServer.Documents;
using Groundwork.SqlServer.PhysicalStorage;
using Groundwork.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Groundwork.RelationalProviders.Tests;

/// <summary>
/// SQL Server's arm of the cross-provider row-visibility contract (valence-works/Groundwork#178). It pins
/// a filtered index, so it is one of the arms that refuses.
/// </summary>
public sealed class SqlServerNullExcludedIndexConformanceTests(
    SqlServerNullExcludedIndexContainer fixture,
    ITestOutputHelper output)
    : IClassFixture<SqlServerNullExcludedIndexContainer>
{
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public async Task Null_excluding_index_never_answers_with_fewer_rows_than_the_predicate_matches(
        PhysicalStorageForm form)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var manifest = NullExcludedIndexConformance.CreateManifest(instance, form);
        var target = NullExcludedIndexConformance.CreateTarget(
            manifest,
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            instance);
        var connectionString = fixture.Container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(
            target, new SqlServerPhysicalSchemaExecutor(connectionString));
        var documents = new SqlServerPhysicalDocumentStore(
            connectionString, manifest, target.Routes, DocumentStoreAccess.Global);
        await NullExcludedIndexConformance.SeedAsync(documents);
        var route = target.Routes.Single();

        await NullExcludedIndexConformance.VerifyQueriesAsync(
            SqlServerPhysicalQueryRuntime.Create(documents, manifest, route, target.Provider),
            route,
            output.WriteLine);
    }
}

/// <summary>
/// PostgreSQL's arm of the same contract, and the one that shows what the contract is really about. It
/// builds the partial index but pins nothing, so it answers every predicate here rather than refusing —
/// and still never returns a short result set, which is the property all four providers share.
/// </summary>
public sealed class PostgreSqlNullExcludedIndexConformanceTests(
    PostgreSqlPhysicalStorageContainer fixture,
    ITestOutputHelper output)
    : IClassFixture<PostgreSqlPhysicalStorageContainer>
{
    [Theory]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    public async Task Null_excluding_index_never_answers_with_fewer_rows_than_the_predicate_matches(
        PhysicalStorageForm form)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var manifest = NullExcludedIndexConformance.CreateManifest(instance, form);
        var target = NullExcludedIndexConformance.CreateTarget(
            manifest,
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            instance);
        var connectionString = fixture.Container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(
            target, new PostgreSqlPhysicalSchemaExecutor(connectionString));
        var documents = new PostgreSqlPhysicalDocumentStore(
            connectionString, manifest, target.Routes, DocumentStoreAccess.Global);
        await NullExcludedIndexConformance.SeedAsync(documents);
        var route = target.Routes.Single();

        await NullExcludedIndexConformance.VerifyQueriesAsync(
            PostgreSqlPhysicalQueryRuntime.Create(documents, manifest, route, target.Provider),
            route,
            output.WriteLine);
    }
}
