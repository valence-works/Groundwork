using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.PhysicalStorage;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Documents;

/// <summary>
/// Durable MongoDB rollout fence that serializes every route write with additive collection
/// activation and rejects stores compiled against a collection shape that can no longer maintain
/// the complete owner/element aggregate.
/// </summary>
internal static class MongoDbCollectionRolloutFence
{
    internal const string CollectionName = "groundwork_collection_rollout_fences";
    private const string RequiredSignature = "required_signature";
    private const string Activity = "activity";
    private const string Epoch = "epoch";

    internal static async Task EnsureCollectionAsync(
        IMongoDatabase database,
        CancellationToken cancellationToken)
    {
        try
        {
            await database.CreateCollectionAsync(
                CollectionName,
                new CreateCollectionOptions { Collation = Collation.Simple },
                cancellationToken);
        }
        catch (MongoCommandException exception) when (exception.Code == 48)
        {
            // A materializer or concurrent first writer created the provider-owned namespace.
        }
    }

    internal static async Task AssertWriterCompatibleAsync(
        IMongoDatabase database,
        IClientSessionHandle session,
        ExecutableStorageRoute route,
        Func<CancellationToken, ValueTask> missingBeforeInsert,
        Func<CancellationToken, ValueTask> existingBeforeTouch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(missingBeforeInsert);
        ArgumentNullException.ThrowIfNull(existingBeforeTouch);
        var fences = database.GetCollection<BsonDocument>(CollectionName);
        var identity = Identity(route);
        var expected = Signature(route);
        var current = await fences.Find(session, Builders<BsonDocument>.Filter.Eq("_id", identity))
            .SingleOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            await missingBeforeInsert(cancellationToken);
            try
            {
                await fences.InsertOneAsync(
                    session,
                    new BsonDocument
                    {
                        ["_id"] = identity,
                        [RequiredSignature] = expected,
                        [Activity] = 1L,
                        [Epoch] = 0L
                    },
                    cancellationToken: cancellationToken);
                return;
            }
            catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                // A concurrent first writer or activation won the fence insert after this
                // transaction's snapshot. The transaction cannot observe that winner safely;
                // retry the whole transaction and then either touch the matching fence or
                // fail closed against its activated route.
                throw new MongoDbCollectionRolloutFenceRetryException(exception);
            }
        }

        var actual = current.GetValue(RequiredSignature, "").AsString;
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw UpgradeRequired(route, expected, actual);
        await existingBeforeTouch(cancellationToken);
        var touched = await fences.UpdateOneAsync(
            session,
            Builders<BsonDocument>.Filter.Eq("_id", identity) &
            Builders<BsonDocument>.Filter.Eq(RequiredSignature, expected),
            Builders<BsonDocument>.Update.Inc(Activity, 1L),
            cancellationToken: cancellationToken);
        if (touched.MatchedCount != 1)
            throw UpgradeRequired(route, expected, "<changed-during-write>");
    }

    internal static async Task ActivateAsync(
        IMongoDatabase database,
        ExecutableStorageRoute route,
        CancellationToken cancellationToken)
    {
        var fences = database.GetCollection<BsonDocument>(CollectionName);
        var identity = Identity(route);
        var signature = Signature(route);
        for (var attempt = 1; ; attempt++)
        {
            using var session = await database.Client.StartSessionAsync(cancellationToken: cancellationToken);
            session.StartTransaction(new TransactionOptions(
                ReadConcern.Snapshot,
                writeConcern: WriteConcern.WMajority));
            try
            {
                await fences.UpdateOneAsync(
                    session,
                    Builders<BsonDocument>.Filter.Eq("_id", identity),
                    Builders<BsonDocument>.Update
                        .Set(RequiredSignature, signature)
                        .Inc(Epoch, 1L)
                        .SetOnInsert(Activity, 0L),
                    new UpdateOptions { IsUpsert = true },
                    cancellationToken);
                await session.CommitTransactionAsync(cancellationToken);
                return;
            }
            catch (MongoException exception) when (
                attempt < 10 &&
                (MongoDbPhysicalDocumentStore.IsTransientTransactionConflict(exception) ||
                 MongoDbPhysicalDocumentStore.IsDuplicateKey(exception)) &&
                !cancellationToken.IsCancellationRequested)
            {
                // A first writer may insert the missing route fence in the same interval that
                // this activation attempts its upsert. Both transactions cannot own that insert;
                // re-reading in a fresh transaction is sufficient because activation overwrites
                // the required signature atomically.
                await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
            }
            catch
            {
                await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
                throw;
            }
        }
    }

    internal static async Task ValidateActivatedAsync(
        IMongoDatabase database,
        ExecutableStorageRoute route,
        CancellationToken cancellationToken)
    {
        var identity = Identity(route);
        var expected = Signature(route);
        var current = await database.GetCollection<BsonDocument>(CollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", identity))
            .SingleOrDefaultAsync(cancellationToken);
        if (current is null)
            return; // Legacy/pre-fence applied targets are adopted by the first route write.
        var active = current.GetValue(RequiredSignature, "").AsString;
        if (!string.Equals(active, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"MongoDB collection rollout fence for storage unit '{route.StorageUnit.Value}' " +
                "does not match the resolved physical route.");
        }
    }

    internal static async Task ValidateActiveIfPresentAsync(
        IMongoDatabase database,
        ExecutableStorageRoute route,
        CancellationToken cancellationToken)
    {
        var names = await (await database.ListCollectionNamesAsync(cancellationToken: cancellationToken))
            .ToListAsync(cancellationToken);
        if (!names.Contains(CollectionName, StringComparer.Ordinal))
            return;
        var exists = await database.GetCollection<BsonDocument>(CollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", Identity(route)))
            .Limit(1)
            .AnyAsync(cancellationToken);
        if (exists)
            await ValidateActivatedAsync(database, route, cancellationToken);
    }

    internal static BsonDocument Identity(ExecutableStorageRoute route) =>
        new()
        {
            ["primary"] = route.PrimaryStorage.Name.Identifier,
            ["kind"] = route.Discriminator.Value
        };

    internal static string Signature(ExecutableStorageRoute route)
    {
        var value = string.Join(
            "\u001e",
            route.CollectionElementStorages
                .OrderBy(storage => storage.Projection.Definition.Path, StringComparer.Ordinal)
                .Select(storage => string.Join(
                    "\u001f",
                    new object?[]
                    {
                        storage.Projection.Definition.LogicalName,
                        storage.Projection.Definition.Path,
                        storage.Projection.Definition.Type,
                        storage.Projection.Definition.Length,
                        storage.Projection.Definition.Precision,
                        storage.Projection.Definition.Scale,
                        storage.Projection.Definition.IsNullable,
                        storage.Projection.Definition.DefaultValue,
                        storage.Projection.Definition.Collation,
                        storage.Projection.Definition.MaxCollectionElements,
                        storage.Storage.Name.Identifier,
                        storage.DocumentKind.Column.Identifier,
                        storage.StorageScope.Column.Identifier,
                        storage.IdComparisonKey.Column.Identifier,
                        storage.IdLookupKey.Column.Identifier,
                        storage.Ordinal.Column.Identifier,
                        storage.Value.Column.Identifier,
                        storage.OwnerOrdinalKey.Name.Identifier,
                        storage.MembershipKey.Name.Identifier
                    }.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""))));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static MongoDbCollectionRouteUpgradeRequiredException UpgradeRequired(
        ExecutableStorageRoute route,
        string expected,
        string actual,
        Exception? innerException = null) =>
        new(route.StorageUnit.Value, expected, actual, innerException);
}

/// <summary>
/// Internal retry signal for a first-writer fence insertion that lost its snapshot race. It is
/// deliberately distinct from a document duplicate-key conflict: retrying re-reads the durable
/// route signature before any primary or collection mutation can proceed.
/// </summary>
internal sealed class MongoDbCollectionRolloutFenceRetryException(Exception innerException) :
    Exception("MongoDB collection rollout fence changed while starting a writer transaction.", innerException);
