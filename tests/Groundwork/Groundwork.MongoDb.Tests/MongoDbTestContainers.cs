using Testcontainers.MongoDb;
using Xunit;

namespace Groundwork.MongoDb.Tests;

/// <summary>
/// Shares one MongoDB Testcontainer across all tests of a class (or collection) instead of one
/// container per test. Tests stay isolated by using a unique database name per test.
/// </summary>
public abstract class MongoDbTestContainer(MongoDbBuilder builder) : IAsyncLifetime
{
    public MongoDbContainer Container { get; } = builder.Build();

    public string ConnectionString => Container.GetConnectionString();

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

/// <summary>A single-node replica set, required by tests that use multi-document transactions.</summary>
public sealed class MongoDbReplicaSetTestContainer() : MongoDbTestContainer(
    new MongoDbBuilder(Groundwork.TestInfrastructure.TestContainerImages.MongoDb)
        .WithReplicaSet("groundwork-rs"));

/// <summary>A plain standalone server, for tests that assert standalone-topology rejection.</summary>
public sealed class MongoDbStandaloneTestContainer() : MongoDbTestContainer(
    new MongoDbBuilder(Groundwork.TestInfrastructure.TestContainerImages.MongoDb));

/// <summary>
/// A replica set with test commands enabled, for tests that configure server-global failpoints.
/// Classes using this must not share it with other classes: a failpoint affects the whole server.
/// </summary>
public sealed class MongoDbFailpointReplicaSetTestContainer() : MongoDbTestContainer(
    new MongoDbBuilder(Groundwork.TestInfrastructure.TestContainerImages.MongoDb)
        .WithReplicaSet("groundwork-rs")
        .WithCommand("--setParameter", "enableTestCommands=1"));
