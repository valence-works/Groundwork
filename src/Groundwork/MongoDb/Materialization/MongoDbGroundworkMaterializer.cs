using Groundwork.Core.SchemaEvolution;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Materialization;

/// <summary>
/// Applies a compiled <see cref="MongoDbPhysicalStorageModel"/> through the physical schema
/// executor, after verifying the deployment's transaction topology supports the model's routes.
/// </summary>
public sealed class MongoDbGroundworkMaterializer(IMongoDatabase database)
{
    public Task<PhysicalSchemaApplicationResult> MaterializeAsync(
        MongoDbPhysicalStorageModel model,
        CancellationToken cancellationToken = default) =>
        MaterializeAsync(model, MongoDbTransactionCapability.ForDatabase(database), cancellationToken);

    internal async Task<PhysicalSchemaApplicationResult> MaterializeAsync(
        MongoDbPhysicalStorageModel model,
        MongoDbTransactionCapability transactionCapability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(transactionCapability);
        await transactionCapability.EnsureSupportedAsync(
            model.Routes.Select(route => route.StorageUnit.Value).ToArray(),
            "physical schema application",
            cancellationToken);
        var executor = new MongoDbPhysicalSchemaExecutor(database, model.Target);
        var result = await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            executor,
            cancellationToken: cancellationToken);
        return result;
    }
}
