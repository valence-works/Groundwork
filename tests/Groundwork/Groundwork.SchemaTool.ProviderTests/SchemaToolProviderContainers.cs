using Testcontainers.MongoDb;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Groundwork.SchemaTool.ProviderTests;

/// <summary>
/// One container per provider, shared across every test in the parity collection instead of one
/// container per test. Tests stay isolated through unique database names on the shared server.
/// </summary>
public sealed class SchemaToolMsSqlContainer : IAsyncLifetime
{
    public MsSqlContainer Container { get; } =
        new MsSqlBuilder(Groundwork.TestInfrastructure.TestContainerImages.SqlServer).Build();

    public string ConnectionString => Container.GetConnectionString();

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

public sealed class SchemaToolPostgreSqlContainer : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } =
        new PostgreSqlBuilder(Groundwork.TestInfrastructure.TestContainerImages.PostgreSql)
            .WithDatabase("groundwork")
            .WithUsername("groundwork")
            .WithPassword("groundwork")
            .Build();

    public string ConnectionString => Container.GetConnectionString();

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

public sealed class SchemaToolMongoDbReplicaSetContainer : IAsyncLifetime
{
    public MongoDbContainer Container { get; } =
        new MongoDbBuilder(Groundwork.TestInfrastructure.TestContainerImages.MongoDb)
            .WithReplicaSet("groundwork-rs")
            .Build();

    public string ConnectionString => Container.GetConnectionString();

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

public sealed class SchemaToolMongoDbStandaloneContainer : IAsyncLifetime
{
    public MongoDbContainer Container { get; } =
        new MongoDbBuilder(Groundwork.TestInfrastructure.TestContainerImages.MongoDb).Build();

    public string ConnectionString => Container.GetConnectionString();

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}
