using Testcontainers.MongoDb;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Groundwork.Differential.Tests;

public sealed class G2DifferentialProviderContainers : IAsyncLifetime
{
    public PostgreSqlContainer PostgreSql { get; } = new PostgreSqlBuilder(
            Groundwork.TestInfrastructure.TestContainerImages.PostgreSql)
        .WithDatabase("groundwork")
        .WithUsername("groundwork")
        .WithPassword("groundwork")
        .Build();

    public MsSqlContainer SqlServer { get; } = new MsSqlBuilder(
            Groundwork.TestInfrastructure.TestContainerImages.SqlServer)
        .Build();

    public MongoDbContainer MongoDb { get; } = new MongoDbBuilder(
            Groundwork.TestInfrastructure.TestContainerImages.MongoDb)
        .WithReplicaSet("groundwork-rs")
        .Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(PostgreSql.StartAsync(), SqlServer.StartAsync(), MongoDb.StartAsync());
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(
            PostgreSql.DisposeAsync().AsTask(),
            SqlServer.DisposeAsync().AsTask(),
            MongoDb.DisposeAsync().AsTask());
    }
}
