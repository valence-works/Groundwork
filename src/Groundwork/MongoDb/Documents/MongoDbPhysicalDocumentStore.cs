using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.Scoping;
using Groundwork.Core.Transactions;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.MongoDb.Materialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Documents;

/// <summary>Route-driven MongoDB document store for all three physical storage forms.</summary>
public sealed class MongoDbPhysicalDocumentStore :
    IDocumentStore,
    IBoundedDocumentStore,
    IPhysicalDocumentQueryExplainer
{
    private const string ContentField = MongoDbPhysicalStorageFields.NativeContent;
    private const string CreatedField = MongoDbPhysicalStorageFields.CreatedAt;
    private const string UpdatedField = MongoDbPhysicalStorageFields.UpdatedAt;
    private readonly MongoDbPhysicalDocumentStoreRuntime runtime;
    private readonly IStorageScopeObserver scopeObserver;
    private readonly IReadOnlyDictionary<string, PhysicalQueryDocumentStore> queryStores;
    private readonly SemaphoreSlim rolloutFencePreparation = new(1, 1);
    private volatile bool rolloutFencePrepared;
    private IMongoDatabase database => runtime.Database;
    private MongoDbPhysicalStorageModel model => runtime.Model;
    private MongoDbPhysicalDocumentStoreOptions options => runtime.Options;
    private TimeProvider timeProvider => runtime.TimeProvider;
    private MongoDbPhysicalDocumentStoreExecutionHooks hooks => runtime.Hooks;
    private Func<CancellationToken, Task<IClientSessionHandle>> startSessionAsync => runtime.StartSessionAsync;
    private MongoDbTransactionCapability transactionCapability => runtime.TransactionCapability;

    internal MongoDbPhysicalDocumentStore(
        IMongoDatabase database,
        MongoDbPhysicalStorageModel model,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null,
        MongoDbPhysicalDocumentStoreOptions? options = null)
        : this(database, model, access, scopeObserver, options, TimeProvider.System, null)
    {
    }

    internal MongoDbPhysicalDocumentStore(
        IMongoDatabase database,
        MongoDbPhysicalStorageModel model,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver,
        MongoDbPhysicalDocumentStoreOptions? options,
        TimeProvider timeProvider,
        MongoDbPhysicalDocumentStoreExecutionHooks? hooks,
        Func<CancellationToken, Task<IClientSessionHandle>>? startSessionAsync = null,
        MongoDbTransactionCapability? transactionCapability = null)
        : this(
            new MongoDbPhysicalDocumentStoreRuntime(
                database,
                model,
                options,
                timeProvider,
                hooks,
                startSessionAsync,
                transactionCapability),
            access,
            scopeObserver)
    {
    }

    private MongoDbPhysicalDocumentStore(
        MongoDbPhysicalDocumentStoreRuntime runtime,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Access = access ?? throw new ArgumentNullException(nameof(access));
        this.scopeObserver = scopeObserver ?? NullStorageScopeObserver.Instance;
        DocumentStoreScopeResolver.ObserveAcquisition(access, this.scopeObserver);
        queryStores = model.Routes.ToFrozenDictionary(
            route => route.StorageUnit.Value,
            route => CreateQueryStore(route),
            StringComparer.Ordinal);
    }

    public DocumentStoreAccess Access { get; }

    public TransactionBoundary TransactionBoundary => transactionCapability.IsKnownSupported
        ? TransactionBoundary.CrossUnitAtomic
        : TransactionBoundary.PerOperation;

    public async Task<DocumentStoreWriteResult> SaveAsync(
        SaveDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        var (route, scope) = ResolveOperation(request.DocumentKind, StorageScopeOperation.Save);
        return await ExecuteAtomicAsync(
            [request.DocumentKind],
            session => SaveCoreAsync(request, route, scope, session, cancellationToken),
            () => ClassifyDuplicateIdentityAsync(route, request.Id, scope.StorageKey!, cancellationToken),
            cancellationToken);
    }

    public async Task<DocumentEnvelope?> LoadAsync(
        string documentKind,
        string id,
        CancellationToken cancellationToken = default)
    {
        var (route, scope) = ResolveOperation(documentKind, StorageScopeOperation.Load);
        await transactionCapability.EnsureSupportedAsync(
            [documentKind],
            "physical storage",
            cancellationToken);
        return await LoadCoreAsync(route, id, scope, session: null, cancellationToken);
    }

    public async Task<DocumentStoreWriteResult> DeleteAsync(
        DeleteDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        var (route, scope) = ResolveOperation(request.DocumentKind, StorageScopeOperation.Delete);
        return await ExecuteAtomicAsync(
            [request.DocumentKind],
            session => DeleteCoreAsync(request, route, scope, session, cancellationToken),
            duplicateKeyResult: null,
            cancellationToken);
    }

    public async Task<IDocumentUnitOfWork> BeginAsync(
        DocumentCommitScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var units = scope.Kinds.Select(Unit).ToArray();
        if (units.Select(unit => unit.Tenancy.Kind).Distinct().Count() != 1)
            throw DocumentStoreScopeResolver.RejectMixedUnitOfWork(scopeObserver, ScopePolicy(units[0]));
        foreach (var unit in units)
            ResolveScope(unit, StorageScopeOperation.BeginUnitOfWork);
        await transactionCapability.EnsureSupportedAsync(scope.Kinds, "physical storage", cancellationToken);

        var session = await startSessionAsync(cancellationToken);
        try
        {
            session.StartTransaction(new TransactionOptions(
                ReadConcern.Snapshot,
                writeConcern: WriteConcern.WMajority));
            await EnsureRolloutFenceAsync(cancellationToken);
            return new UnitOfWork(this, session, scope);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public Task<DocumentQueryResult> QueryAsync(DocumentQuery query, CancellationToken cancellationToken = default) =>
        QueryStore(query.DocumentKind).QueryAsync(query, cancellationToken);

    public Task<long> CountAsync(DocumentQuery query, CancellationToken cancellationToken = default) =>
        QueryStore(query.DocumentKind).CountAsync(query, cancellationToken);

    public Task<DocumentEnvelope?> FirstOrDefaultAsync(DocumentQuery query, CancellationToken cancellationToken = default) =>
        QueryStore(query.DocumentKind).FirstOrDefaultAsync(query, cancellationToken);

    public Task<bool> AnyAsync(DocumentQuery query, CancellationToken cancellationToken = default) =>
        QueryStore(query.DocumentKind).AnyAsync(query, cancellationToken);

    /// <summary>
    /// Returns ordered native MongoDB evidence. Linked queries can execute the bounded selector to
    /// derive exact hydration identities; this sensitive diagnostic operation can therefore be costly.
    /// </summary>
    public Task<PhysicalDocumentQueryExplanation> ExplainAsync(
        DocumentQuery query,
        CancellationToken cancellationToken = default) =>
        QueryStore(query.DocumentKind).ExplainAsync(query, cancellationToken);

    public PhysicalQueryPlan ResolvePlan(
        DocumentQuery query,
        BoundedQueryResultOperation operation = BoundedQueryResultOperation.Documents) =>
        QueryStore(query.DocumentKind).ResolvePlan(query, operation);

    private PhysicalQueryDocumentStore CreateQueryStore(ExecutableStorageRoute route)
    {
        var storage = model.StorageByStorageUnit[route.StorageUnit.Value];
        var capabilities = Capabilities(route, storage);
        var plans = PhysicalQueryPlanCompiler.Compile(route, storage, capabilities);
        if (!plans.IsValid)
            throw new InvalidOperationException(string.Join(Environment.NewLine, plans.Diagnostics.Select(x => $"{x.Code}: {x.Message}")));

        ValidateScaleBearingOperations(storage);
        ValidateTypedPaths(route, storage);
        var linkedPlans = plans.Plans.Where(plan => plan.AccessKind == PhysicalQueryAccessKind.LinkedIndexThenPrimary).ToArray();
        var collectionPlans = plans.Plans.Where(plan => plan.AccessKind == PhysicalQueryAccessKind.CollectionElementsThenPrimary).ToArray();
        var nativePlans = plans.Plans.Where(plan => plan.AccessKind is not (
            PhysicalQueryAccessKind.LinkedIndexThenPrimary or PhysicalQueryAccessKind.CollectionElementsThenPrimary)).ToArray();
        var handlers = new IPhysicalDocumentQueryHandler[]
        {
            new MongoDbPhysicalQueryHandler(
                MongoDbPhysicalQueryHandler.CollectionIdentity,
                PhysicalQuerySourceKind.CollectionElements,
                database,
                route,
                () => ResolveScope(Unit(route.StorageUnit.Value), StorageScopeOperation.Query, allowAcrossScopes: true),
                collectionPlans.Select(Certification).ToArray(),
                capabilities.NativeFieldIdentifiers,
                options,
                timeProvider,
                hooks,
                transactionCapability),
            new MongoDbPhysicalQueryHandler(
                MongoDbPhysicalQueryHandler.LinkedIdentity,
                PhysicalQuerySourceKind.LinkedIndex,
                database,
                route,
                () => ResolveScope(Unit(route.StorageUnit.Value), StorageScopeOperation.Query, allowAcrossScopes: true),
                linkedPlans.Select(Certification).ToArray(),
                capabilities.NativeFieldIdentifiers,
                options,
                timeProvider,
                hooks,
                transactionCapability),
            new MongoDbPhysicalQueryHandler(
                MongoDbPhysicalQueryHandler.NativeIdentity,
                PhysicalQuerySourceKind.NativeDocumentFields,
                database,
                route,
                () => ResolveScope(Unit(route.StorageUnit.Value), StorageScopeOperation.Query, allowAcrossScopes: true),
                nativePlans.Select(Certification).ToArray(),
                capabilities.NativeFieldIdentifiers,
                options,
                timeProvider,
                hooks,
                transactionCapability)
        };
        return PhysicalQueryDocumentStore.FromCompiledPlans(plans.Plans, capabilities, handlers);
    }

    private static void ValidateTypedPaths(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage)
    {
        foreach (var index in storage.LogicalIndexes)
        {
            foreach (var field in index.Fields.Where(field => !PhysicalDocumentFieldPaths.IsEnvelope(field.Path)))
            {
                var valueKind = index.GetValueKind(field);
                var projection = route.ProjectedColumns.SingleOrDefault(candidate =>
                    candidate.Definition.Path == field.Path);
                if (projection is null)
                {
                    if (valueKind is IndexValueKind.Number or IndexValueKind.DateTime)
                    {
                        throw new InvalidOperationException(
                            $"MongoDB cannot certify exact '{valueKind}' query semantics for path " +
                            $"'{field.Path}' without a typed projected column.");
                    }
                    continue;
                }
                if (!PortableQueryOperationCompatibility.Supports(valueKind, projection.Definition.Type))
                {
                    throw new InvalidOperationException(
                        $"MongoDB projected path '{field.Path}' type '{projection.Definition.Type}' cannot " +
                        $"preserve logical value kind '{valueKind}'.");
                }
            }

            foreach (var query in storage.BoundedQueries.Where(query => query.IndexIdentity == index.Identity))
            {
                var predicates = query.PredicateBindingMode == BoundedQueryPredicateBindingMode.ImplicitFirstLogicalIndexField
                    ? index.Fields.Take(1).Select(field =>
                        new BoundedQueryPredicateField(field.Path, query.Operations)).ToArray()
                    : query.PredicateFields;
                foreach (var predicate in predicates)
                {
                    var projection = route.ProjectedColumns.SingleOrDefault(candidate =>
                        candidate.Definition.Path == predicate.Path);
                    if (projection is not null && predicate.Operations.Any(operation =>
                            !PortableQueryOperationCompatibility.Supports(projection.Definition.Type, operation)))
                    {
                        throw new InvalidOperationException(
                            $"MongoDB projected path '{predicate.Path}' type '{projection.Definition.Type}' cannot " +
                            "execute every declared query operation without changing semantics.");
                    }
                }
            }
        }
    }

    private static void ValidateScaleBearingOperations(StorageUnitPhysicalStorage storage)
    {
        foreach (var query in storage.BoundedQueries.Where(query =>
                     query.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing))
        {
            var unsupported = MongoDbScaleBearingOperationValidation.UnsupportedQueryOperations(storage, query);
            if (unsupported.Length == 0)
                continue;

            throw new InvalidOperationException(
                $"MongoDB cannot certify scale-bearing query '{query.Identity}' operations " +
                $"{string.Join(", ", unsupported)} as indexed: Groundwork case-insensitive regular-expression semantics " +
                "cannot be served by the declared ordinary MongoDB B-tree index.");
        }
    }

    private static PhysicalQueryHandlerCertification Certification(PhysicalQueryPlan plan) =>
        new(
            plan.Provider,
            plan.StorageUnit,
            plan.QueryIdentity,
            plan.LogicalIndexIdentity,
            plan.LogicalIndexPaths,
            plan.AccessKind,
            plan.Scope.Field.Target,
            plan.LookupObject,
            plan.PrimaryObject,
            plan.IndexName,
            plan.RequiredFields
                .GroupBy(field => field.Path, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Identifier, StringComparer.Ordinal),
            plan.RouteFingerprint);

    private PhysicalQueryPlannerCapabilities Capabilities(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage) =>
        MongoDbPhysicalMutationCapabilities.Create(
            route,
            storage,
            model.Provider,
            MongoDbPhysicalQueryHandler.Operations);

    private async Task EnsureRolloutFenceAsync(CancellationToken cancellationToken)
    {
        if (rolloutFencePrepared)
            return;
        await rolloutFencePreparation.WaitAsync(cancellationToken);
        try
        {
            if (rolloutFencePrepared)
                return;
            await MongoDbCollectionRolloutFence.EnsureCollectionAsync(database, cancellationToken);
            rolloutFencePrepared = true;
        }
        finally
        {
            rolloutFencePreparation.Release();
        }
    }

    private async Task<DocumentStoreWriteResult> ExecuteAtomicAsync(
        IReadOnlyList<string> documentKinds,
        Func<IClientSessionHandle, Task<DocumentStoreWriteResult>> action,
        Func<Task<DocumentStoreWriteResult>>? duplicateKeyResult,
        CancellationToken cancellationToken)
    {
        await transactionCapability.EnsureSupportedAsync(documentKinds, "physical storage", cancellationToken);
        await EnsureRolloutFenceAsync(cancellationToken);
        return await ExecuteInTransactionAsync(
            documentKinds,
            (session, _) => action(session),
            duplicateKeyOutcome: () => duplicateKeyResult is null
                ? Task.FromResult(DocumentStoreWriteResult.ConcurrencyConflict)
                : duplicateKeyResult(),
            beforeCommit: null,
            afterCommitBeforeAcknowledgement: null,
            cancellationToken);
    }

    /// <summary>
    /// Runs one body inside a snapshot/majority transaction with the shared retry skeleton: rollout
    /// fence conflicts and transient transaction conflicts retry within the attempt/timeout budget,
    /// commit-acknowledgement uncertainty and other failures abort and rethrow. Duplicate-key policy is
    /// the caller's: a non-null <paramref name="duplicateKeyOutcome"/> makes duplicate keys terminal by
    /// returning its result after the abort; a null one retries them like transient conflicts.
    /// </summary>
    private async Task<T> ExecuteInTransactionAsync<T>(
        IReadOnlyList<string> documentKinds,
        Func<IClientSessionHandle, CancellationToken, Task<T>> body,
        Func<Task<T>>? duplicateKeyOutcome,
        Func<CancellationToken, ValueTask>? beforeCommit,
        Func<CancellationToken, ValueTask>? afterCommitBeforeAcknowledgement,
        CancellationToken cancellationToken)
    {
        var retryStarted = timeProvider.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var session = await startSessionAsync(cancellationToken);
            session.StartTransaction(new TransactionOptions(
                ReadConcern.Snapshot,
                writeConcern: WriteConcern.WMajority));
            try
            {
                await hooks.TransactionBodyStarting(session, attempt, cancellationToken);
                var result = await body(session, cancellationToken);
                if (beforeCommit is not null)
                    await beforeCommit(cancellationToken);
                await CommitWithRetryAsync(session, documentKinds, cancellationToken);
                if (afterCommitBeforeAcknowledgement is not null)
                    await afterCommitBeforeAcknowledgement(cancellationToken);
                return result;
            }
            catch (MongoDbCollectionRolloutFenceRetryException)
            {
                await AbortTransactionIgnoringFailureAsync(session);
                if (!await TryWaitForTransactionRetryAsync(attempt, retryStarted, cancellationToken))
                    throw;
            }
            catch (MongoException exception) when (
                !cancellationToken.IsCancellationRequested &&
                duplicateKeyOutcome is not null &&
                IsDuplicateKey(exception))
            {
                await AbortTransactionIgnoringFailureAsync(session);
                return await duplicateKeyOutcome();
            }
            catch (MongoException exception) when (
                !cancellationToken.IsCancellationRequested &&
                (IsTransientTransactionConflict(exception) ||
                 (duplicateKeyOutcome is null && IsDuplicateKey(exception))))
            {
                await AbortTransactionIgnoringFailureAsync(session);
                if (!await TryWaitForTransactionRetryAsync(attempt, retryStarted, cancellationToken))
                    throw;
            }
            catch (DocumentCommitAcknowledgementUncertainException)
            {
                await AbortTransactionIgnoringFailureAsync(session);
                throw;
            }
            catch
            {
                await AbortTransactionIgnoringFailureAsync(session);
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }
    }

    /// <summary>
    /// Decides whether the failed transaction attempt may retry: false (rethrow the current failure)
    /// when the attempt/timeout budget is exhausted, true after the backoff delay ran otherwise.
    /// </summary>
    private async Task<bool> TryWaitForTransactionRetryAsync(int attempt, long retryStarted, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanRetry(
                attempt,
                retryStarted,
                options.MaximumTransactionAttempts,
                options.TransactionRetryTimeout))
        {
            return false;
        }
        await DelayBeforeRetryAsync(attempt, cancellationToken);
        return timeProvider.GetElapsedTime(retryStarted) < options.TransactionRetryTimeout;
    }

    private async Task CommitWithRetryAsync(
        IClientSessionHandle session,
        IReadOnlyList<string> documentKinds,
        CancellationToken cancellationToken)
    {
        var retryStarted = timeProvider.GetTimestamp();
        MongoException? unknownCommitResult = null;
        var commitWasInvoked = false;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await hooks.CommitStarting(session, attempt, cancellationToken);
                    commitWasInvoked = true;
                    var commit = session.CommitTransactionAsync(cancellationToken);
                    await hooks.CommitInvoked(session, attempt, cancellationToken);
                    await commit;
                    return;
                }
                catch (MongoException exception)
                {
                    if (!exception.HasErrorLabel("UnknownTransactionCommitResult"))
                    {
                        if (unknownCommitResult is not null)
                            throw new DocumentCommitAcknowledgementUncertainException(documentKinds, exception);
                        cancellationToken.ThrowIfCancellationRequested();
                        throw;
                    }

                    unknownCommitResult = exception;
                    await hooks.CommitResultUnknown(session, attempt, exception, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!CanRetry(
                            attempt,
                            retryStarted,
                            options.MaximumCommitAttempts,
                            options.CommitRetryTimeout))
                    {
                        throw new DocumentCommitAcknowledgementUncertainException(documentKinds, exception);
                    }
                    await hooks.CommitRetryDelayStarting(session, attempt, cancellationToken);
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    await hooks.CommitRetryDelayCompleted(session, attempt, cancellationToken);
                    if (timeProvider.GetElapsedTime(retryStarted) >= options.CommitRetryTimeout)
                        throw new DocumentCommitAcknowledgementUncertainException(documentKinds, exception);
                }
            }
        }
        catch (OperationCanceledException exception) when (commitWasInvoked)
        {
            throw new DocumentCommitAcknowledgementUncertainException(documentKinds, exception);
        }
        catch (TimeoutException exception) when (commitWasInvoked)
        {
            throw new DocumentCommitAcknowledgementUncertainException(documentKinds, exception);
        }
    }

    private bool CanRetry(int attempts, long started, int maximumAttempts, TimeSpan timeout) =>
        attempts < maximumAttempts && timeProvider.GetElapsedTime(started) < timeout;

    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var maximumDelay = Math.Min(100, 2 << Math.Min(attempt, 5));
        return Task.Delay(Random.Shared.Next(1, maximumDelay + 1), cancellationToken);
    }

    internal static bool IsTransientTransactionConflict(MongoException exception) =>
        exception.HasErrorLabel("TransientTransactionError") ||
        exception switch
        {
            MongoCommandException command => command.Code is 112 or 244 or 251,
            MongoWriteException write =>
                IsTransientTransactionConflictCode(write.WriteError?.Code) ||
                IsTransientTransactionConflictCode(write.WriteConcernError?.Code),
            MongoBulkWriteException bulk =>
                bulk.WriteErrors.Any(error => IsTransientTransactionConflictCode(error.Code)) ||
                IsTransientTransactionConflictCode(bulk.WriteConcernError?.Code),
            _ => false
        };

    internal static bool IsDuplicateKey(MongoException exception) =>
        exception switch
        {
            MongoCommandException command => command.Code == 11000,
            MongoWriteException write => write.WriteError?.Code == 11000 || write.WriteConcernError?.Code == 11000,
            MongoBulkWriteException bulk =>
                bulk.WriteErrors.Any(error => error.Code == 11000) ||
                bulk.WriteConcernError?.Code == 11000,
            _ => false
        };

    private static bool IsTransientTransactionConflictCode(int? code) =>
        code is 112 or 244 or 251;

    internal static async Task AbortTransactionIgnoringFailureAsync(IClientSessionHandle session)
    {
        if (!session.IsInTransaction)
            return;
        try
        {
            await session.AbortTransactionAsync(CancellationToken.None);
        }
        catch (MongoException)
        {
            // The operation error or structured conflict is authoritative.
        }
    }

    private async Task<DocumentStoreWriteResult> SaveCoreAsync(
        SaveDocumentRequest request,
        ExecutableStorageRoute route,
        DocumentScopeSelection scope,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        await MongoDbCollectionRolloutFence.AssertWriterCompatibleAsync(
            database,
            session,
            route,
            hooks.RolloutFenceMissingBeforeInsert,
            hooks.RolloutFenceExistingBeforeTouch,
            cancellationToken);
        var current = await LoadDocumentAsync(route, request.Id, scope.StorageKey!, session, cancellationToken);
        if (current is not null)
        {
            var authoritativeId = current[route.Envelope.Identity.OriginalId.Identifier].AsString;
            if (!string.Equals(authoritativeId, request.Id, StringComparison.Ordinal))
                return DocumentStoreWriteResult.IdentityConflict(authoritativeId);
        }
        if (current is not null && request.ExpectedVersion is not null && current[route.Envelope.Version.Identifier].ToInt64() != request.ExpectedVersion)
            return DocumentStoreWriteResult.ConcurrencyConflict;
        if (current is null && request.ExpectedVersion is { } expected && expected != 0)
            return DocumentStoreWriteResult.NotFound;

        var now = DateTimeOffset.UtcNow;
        var created = current is null ? now : new DateTimeOffset(current[CreatedField].ToUniversalTime());
        var version = current is null ? 1 : current[route.Envelope.Version.Identifier].ToInt64() + 1;
        var incarnation = current?.GetValue(MongoDbPhysicalStorageFields.Incarnation).AsString ?? Guid.NewGuid().ToString("N");
        var content = MongoDbCanonicalJson.Parse(request.ContentJson);
        var projectedValues = MongoDbPhysicalProjectionValues.ResolveAll(request.ContentJson, route.ProjectedColumns);
        var document = CreatePrimary(
            route,
            GetMutationBindings(route.StorageUnit.Value),
            request,
            scope.StorageKey!,
            version,
            incarnation,
            created,
            now,
            content,
            projectedValues);
        var primary = database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier);
        if (current is null)
        {
            await primary.InsertOneAsync(session, document, cancellationToken: cancellationToken);
        }
        else
        {
            var filter = MongoDbPhysicalDocumentIdentity.PrimaryExactFilter(route, request.Id, scope.StorageKey!);
            if (request.ExpectedVersion is not null)
                filter &= Builders<BsonDocument>.Filter.Eq(route.Envelope.Version.Identifier, request.ExpectedVersion.Value);
            var result = await primary.ReplaceOneAsync(session, filter, document, cancellationToken: cancellationToken);
            if (result.MatchedCount == 0)
                return DocumentStoreWriteResult.ConcurrencyConflict;
        }
        await MaintainLinkedAsync(
            route,
            GetMutationBindings(route.StorageUnit.Value),
            document,
            projectedValues,
            session,
            cancellationToken);
        await hooks.CollectionMaintenanceStarting(session, cancellationToken);
        await MaintainCollectionsAsync(route, request, scope.StorageKey!, session, cancellationToken);
        return DocumentStoreWriteResult.Saved(ReadEnvelope(route, document));
    }

    private async Task<DocumentStoreWriteResult> DeleteCoreAsync(
        DeleteDocumentRequest request,
        ExecutableStorageRoute route,
        DocumentScopeSelection scope,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        await MongoDbCollectionRolloutFence.AssertWriterCompatibleAsync(
            database,
            session,
            route,
            hooks.RolloutFenceMissingBeforeInsert,
            hooks.RolloutFenceExistingBeforeTouch,
            cancellationToken);
        var filter = MongoDbPhysicalDocumentIdentity.PrimaryExactFilter(route, request.Id, scope.StorageKey!);
        if (request.ExpectedVersion is not null)
            filter &= Builders<BsonDocument>.Filter.Eq(route.Envelope.Version.Identifier, request.ExpectedVersion.Value);
        var deleted = await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .FindOneAndDeleteAsync(session, filter, cancellationToken: cancellationToken);
        if (deleted is null)
        {
            var exists = await LoadDocumentAsync(route, request.Id, scope.StorageKey!, session, cancellationToken);
            return exists is null ? DocumentStoreWriteResult.NotFound : DocumentStoreWriteResult.ConcurrencyConflict;
        }
        if (route.LinkedIndexStorage is not null)
        {
            var linkedFilter = MongoDbPhysicalDocumentIdentity.LinkedExactFilter(
                route,
                request.Id,
                scope.StorageKey!);
            await database.GetCollection<BsonDocument>(route.LinkedIndexStorage.Name.Identifier)
                .DeleteOneAsync(session, linkedFilter, cancellationToken: cancellationToken);
        }
        await DeleteCollectionsAsync(route, request.Id, scope.StorageKey!, session, cancellationToken);
        return DocumentStoreWriteResult.Deleted(deleted[route.Envelope.Id.Identifier].AsString);
    }

    private async Task<DocumentEnvelope?> LoadCoreAsync(
        ExecutableStorageRoute route,
        string id,
        DocumentScopeSelection scope,
        IClientSessionHandle? session,
        CancellationToken cancellationToken)
    {
        var document = await LoadDocumentAsync(route, id, scope.StorageKey!, session, cancellationToken);
        return document is null ? null : ReadEnvelope(route, document);
    }

    private async Task<BsonDocument?> LoadDocumentAsync(
        ExecutableStorageRoute route,
        string id,
        string scope,
        IClientSessionHandle? session,
        CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier);
        var identity = route.Envelope.Identity.Project(id);
        var filter = MongoDbPhysicalDocumentIdentity.PrimaryExactFilter(route, identity, scope);
        var exact = session is null
            ? await collection.Find(filter).SingleOrDefaultAsync(cancellationToken)
            : await collection.Find(session, filter).SingleOrDefaultAsync(cancellationToken);
        if (exact is not null)
            return exact;

        var lookupFilter = MongoDbPhysicalDocumentIdentity.PrimaryLookupFilter(
            route,
            scope,
            identity.LookupKey);
        var retained = session is null
            ? await collection.Find(lookupFilter).SingleOrDefaultAsync(cancellationToken)
            : await collection.Find(session, lookupFilter).SingleOrDefaultAsync(cancellationToken);
        if (retained is null)
            return null;
        MongoDbPhysicalDocumentIdentity.ThrowIfCollision(route, identity, retained);

        // A matching identity may be inserted after the exact read reports no document and before
        // the collision-evidence fallback runs. The retained comparison key is the authoritative
        // exact evidence in that race; a different comparison key still fails closed above.
        return retained;
    }

    private async Task<DocumentStoreWriteResult> ClassifyDuplicateIdentityAsync(
        ExecutableStorageRoute route,
        string requestedId,
        string scope,
        CancellationToken cancellationToken)
    {
        var requested = route.Envelope.Identity.Project(requestedId);
        var retained = await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .Find(MongoDbPhysicalDocumentIdentity.PrimaryLookupFilter(route, scope, requested.LookupKey))
            .SingleOrDefaultAsync(cancellationToken);
        if (retained is null)
            return DocumentStoreWriteResult.ConcurrencyConflict;

        MongoDbPhysicalDocumentIdentity.ThrowIfCollision(route, requested, retained);
        var authoritativeId = retained[route.Envelope.Identity.OriginalId.Identifier].AsString;
        return string.Equals(authoritativeId, requestedId, StringComparison.Ordinal)
            ? DocumentStoreWriteResult.ConcurrencyConflict
            : DocumentStoreWriteResult.IdentityConflict(authoritativeId);
    }

    private static BsonDocument CreatePrimary(
        ExecutableStorageRoute route,
        IReadOnlyList<MongoDbPhysicalMutationBinding> mutationBindings,
        SaveDocumentRequest request,
        string scope,
        long version,
        string incarnation,
        DateTimeOffset created,
        DateTimeOffset updated,
        BsonDocument content,
        IReadOnlyDictionary<ExecutableProjectedColumnRoute, MongoDbPhysicalProjectionValue> projectedValues)
    {
        var document = new BsonDocument
        {
            [route.Envelope.DocumentKind.Identifier] = route.Discriminator.Value,
            [route.Envelope.StorageScope.Identifier] = scope,
            [route.Envelope.Version.Identifier] = version,
            [route.Envelope.SchemaVersion.Identifier] = request.SchemaVersion,
            [route.Envelope.CanonicalJson.Identifier] = content.DeepClone(),
            [MongoDbPhysicalStorageFields.Incarnation] = incarnation,
            [ContentField] = content,
            [CreatedField] = created.UtcDateTime,
            [UpdatedField] = updated.UtcDateTime
        };
        MongoDbPhysicalDocumentIdentity.WritePrimary(document, route, request.Id);
        foreach (var projection in route.ProjectedColumns.Where(column =>
                     column.Target == ExecutableStorageObjectRole.PrimaryStorage &&
                     column.Definition.Cardinality == ProjectionCardinality.Scalar))
        {
            var value = projectedValues[projection];
            if (value.IsPresent)
                document[projection.Column.Identifier] = value.Value;
        }
        MongoDbPhysicalMutationStorage.ApplyMirrors(
            document,
            document,
            content,
            route,
            mutationBindings,
            ExecutableStorageObjectRole.PrimaryStorage,
            projectedValues);
        document[MongoDbPhysicalStorageFields.Id] = MongoDbPhysicalSchemaExecutor.KeyDocument(route.PrimaryKey, document);
        return document;
    }

    private async Task MaintainLinkedAsync(
        ExecutableStorageRoute route,
        IReadOnlyList<MongoDbPhysicalMutationBinding> mutationBindings,
        BsonDocument primary,
        IReadOnlyDictionary<ExecutableProjectedColumnRoute, MongoDbPhysicalProjectionValue> projectedValues,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (route.LinkedIndexStorage is null)
            return;
        var linked = MongoDbLinkedDocumentStorage.Create(route, primary, projectedValues);
        MongoDbPhysicalMutationStorage.ApplyMirrors(
            linked.Document,
            primary,
            primary[ContentField].AsBsonDocument,
            route,
            mutationBindings,
            ExecutableStorageObjectRole.LinkedIndexStorage,
            projectedValues);
        await database.GetCollection<BsonDocument>(route.LinkedIndexStorage.Name.Identifier).ReplaceOneAsync(
            session,
            Builders<BsonDocument>.Filter.Eq(MongoDbPhysicalStorageFields.Id, linked.Identity),
            linked.Document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    private async Task MaintainCollectionsAsync(
        ExecutableStorageRoute route,
        SaveDocumentRequest request,
        string scope,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        await DeleteCollectionsAsync(route, request.Id, scope, session, cancellationToken);
        var identity = route.Envelope.Identity.Project(request.Id);
        foreach (var storage in route.CollectionElementStorages)
        {
            var documents = MongoDbPhysicalProjectionValues.ResolveCollection(request.ContentJson, storage.Projection)
                .Select(element => new BsonDocument
                {
                    [storage.DocumentKind.Column.Identifier] = route.Discriminator.Value,
                    [storage.StorageScope.Column.Identifier] = scope,
                    [storage.IdComparisonKey.Column.Identifier] = identity.ComparisonKey,
                    [storage.IdLookupKey.Column.Identifier] = identity.LookupKey,
                    [storage.Ordinal.Column.Identifier] = element.Ordinal,
                    [storage.Value.Column.Identifier] = element.Value
                })
                .ToArray();
            if (documents.Length != 0)
            {
                await database.GetCollection<BsonDocument>(storage.Storage.Name.Identifier)
                    .InsertManyAsync(session, documents, cancellationToken: cancellationToken);
            }
        }
    }

    private async Task DeleteCollectionsAsync(
        ExecutableStorageRoute route,
        string id,
        string scope,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var identity = route.Envelope.Identity.Project(id);
        foreach (var storage in route.CollectionElementStorages)
        {
            var filter = Builders<BsonDocument>.Filter.Eq(storage.DocumentKind.Column.Identifier, route.Discriminator.Value) &
                         Builders<BsonDocument>.Filter.Eq(storage.StorageScope.Column.Identifier, scope) &
                         Builders<BsonDocument>.Filter.Eq(storage.IdLookupKey.Column.Identifier, identity.LookupKey) &
                         Builders<BsonDocument>.Filter.Eq(storage.IdComparisonKey.Column.Identifier, identity.ComparisonKey);
            await database.GetCollection<BsonDocument>(storage.Storage.Name.Identifier)
                .DeleteManyAsync(session, filter, new DeleteOptions(), cancellationToken);
        }
    }

    internal static DocumentEnvelope ReadEnvelope(ExecutableStorageRoute route, BsonDocument document) =>
        new(
            route.StorageUnit.Value,
            document[route.Envelope.Id.Identifier].AsString,
            document[route.Envelope.SchemaVersion.Identifier].AsString,
            document[route.Envelope.Version.Identifier].ToInt64(),
            MongoDbCanonicalJson.Serialize(document[route.Envelope.CanonicalJson.Identifier]),
            new DateTimeOffset(document[CreatedField].ToUniversalTime()),
            new DateTimeOffset(document[UpdatedField].ToUniversalTime()))
        {
            Scope = DocumentStoreScopeResolver.ReadScope(document[route.Envelope.StorageScope.Identifier].AsString)
        };

    private PhysicalQueryDocumentStore QueryStore(string kind) =>
        queryStores.TryGetValue(kind, out var store) ? store : throw Unknown(kind);

    private ExecutableStorageRoute Route(string kind) =>
        model.RoutesByStorageUnit.TryGetValue(kind, out var route) ? route : throw Unknown(kind);

    private StorageUnit Unit(string kind) =>
        model.Manifest.StorageUnits.SingleOrDefault(unit => unit.Identity.Value == kind) ?? throw Unknown(kind);

    private static InvalidOperationException Unknown(string kind) => new($"Document kind '{kind}' is not declared by the compiled MongoDB physical model.");

    private DocumentScopeSelection ResolveScope(StorageUnit unit, StorageScopeOperation operation, bool allowAcrossScopes = false) =>
        DocumentStoreScopeResolver.Resolve(unit, Access, operation, scopeObserver, allowAcrossScopes);

    private (ExecutableStorageRoute Route, DocumentScopeSelection Scope) ResolveOperation(
        string documentKind,
        StorageScopeOperation operation) =>
        (Route(documentKind), ResolveScope(Unit(documentKind), operation));

    internal IMongoDatabase Database => database;

    internal MongoDbPhysicalDocumentStore WithAccess(
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver) =>
        new(runtime, access, scopeObserver);

    internal string ManifestIdentity => model.Manifest.Identity.Value;

    internal StorageManifest Manifest => model.Manifest;

    internal ProviderIdentity Provider => model.Provider;

    internal ExecutableStorageRoute GetRoute(string documentKind) => Route(documentKind);

    internal DocumentScopeSelection ResolveMutationScope(string documentKind) =>
        ResolveScope(Unit(documentKind), StorageScopeOperation.Mutate);

    internal PhysicalQueryPlannerCapabilities GetMutationCapabilities(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage) =>
        MongoDbPhysicalMutationCapabilities.Create(route, storage, model.Provider);

    internal IReadOnlyList<MongoDbPhysicalMutationBinding> GetMutationBindings(string documentKind) =>
        model.MutationBindingsByStorageUnit.TryGetValue(documentKind, out var bindings)
            ? bindings
            : [];

    internal Task EnsureMutationSupportedAsync(string documentKind, CancellationToken cancellationToken) =>
        transactionCapability.EnsureSupportedAsync(
            [documentKind],
            "physical bounded mutation explain",
            cancellationToken);

    internal async Task<T> ExecutePhysicalMutationAsync<T>(
        string documentKind,
        Func<IClientSessionHandle, CancellationToken, Task<T>> action,
        Func<CancellationToken, ValueTask>? beforeCommit,
        Func<CancellationToken, ValueTask>? afterCommitBeforeAcknowledgement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await transactionCapability.EnsureSupportedAsync(
            [documentKind],
            "physical bounded mutation",
            cancellationToken);
        await EnsureRolloutFenceAsync(cancellationToken);
        var route = Route(documentKind);
        return await ExecuteInTransactionAsync(
            [documentKind],
            async (session, ct) =>
            {
                await MongoDbCollectionRolloutFence.AssertWriterCompatibleAsync(
                    database,
                    session,
                    route,
                    hooks.RolloutFenceMissingBeforeInsert,
                    hooks.RolloutFenceExistingBeforeTouch,
                    ct);
                return await action(session, ct);
            },
            duplicateKeyOutcome: null,
            beforeCommit,
            afterCommitBeforeAcknowledgement,
            cancellationToken);
    }

    private static StorageScopePolicy ScopePolicy(StorageUnit unit) =>
        unit.Tenancy.Kind == TenancyKind.Scoped ? StorageScopePolicy.Scoped : StorageScopePolicy.Global;

    private sealed class UnitOfWork(
        MongoDbPhysicalDocumentStore store,
        IClientSessionHandle session,
        DocumentCommitScope scope) : MongoDbDocumentUnitOfWorkBase(session)
    {
        protected override string AlreadyCompletedMessage => "The document transaction has completed.";

        protected override Task CommitTransactionAsync(CancellationToken cancellationToken) =>
            store.CommitWithRetryAsync(Session, scope.Kinds, cancellationToken);

        protected override void BeforeRethrowStagedWriteFailure(CancellationToken cancellationToken) =>
            cancellationToken.ThrowIfCancellationRequested();

        public override async Task<DocumentStoreWriteResult> SaveAsync(SaveDocumentRequest request, CancellationToken cancellationToken = default)
        {
            EnsureActive();
            scope.EnsureIncludes(request.DocumentKind);
            var (route, selection) = store.ResolveOperation(request.DocumentKind, StorageScopeOperation.Save);

            Func<CancellationToken, Task<DocumentStoreWriteResult>>? ConvertFailure(Exception exception)
            {
                if (exception is MongoDbCollectionRolloutFenceRetryException)
                    return _ => Task.FromResult(DocumentStoreWriteResult.ConcurrencyConflict);
                if (exception is MongoException mongoException &&
                    !cancellationToken.IsCancellationRequested &&
                    (IsDuplicateKey(mongoException) || IsTransientTransactionConflict(mongoException)))
                {
                    return ct => IsDuplicateKey(mongoException)
                        ? store.ClassifyDuplicateIdentityAsync(route, request.Id, selection.StorageKey!, ct)
                        : Task.FromResult(DocumentStoreWriteResult.ConcurrencyConflict);
                }
                return null;
            }

            return await StageWriteAsync(
                ct => store.SaveCoreAsync(request, route, selection, Session, ct),
                DocumentStoreWriteStatus.Saved,
                cancellationToken,
                ConvertFailure);
        }

        public override async Task<DocumentStoreWriteResult> DeleteAsync(DeleteDocumentRequest request, CancellationToken cancellationToken = default)
        {
            EnsureActive();
            scope.EnsureIncludes(request.DocumentKind);

            Func<CancellationToken, Task<DocumentStoreWriteResult>>? ConvertFailure(Exception exception)
            {
                if (exception is MongoDbCollectionRolloutFenceRetryException ||
                    (exception is MongoException mongoException &&
                     !cancellationToken.IsCancellationRequested &&
                     IsTransientTransactionConflict(mongoException)))
                {
                    return _ => Task.FromResult(DocumentStoreWriteResult.ConcurrencyConflict);
                }
                return null;
            }

            return await StageWriteAsync(
                ct =>
                {
                    var (route, selection) = store.ResolveOperation(request.DocumentKind, StorageScopeOperation.Delete);
                    return store.DeleteCoreAsync(request, route, selection, Session, ct);
                },
                DocumentStoreWriteStatus.Deleted,
                cancellationToken,
                ConvertFailure);
        }

        public override Task<DocumentEnvelope?> LoadAsync(string documentKind, string id, CancellationToken cancellationToken = default)
        {
            EnsureActive();
            scope.EnsureIncludes(documentKind);
            var (route, selection) = store.ResolveOperation(documentKind, StorageScopeOperation.Load);
            return store.LoadCoreAsync(route, id, selection, Session, cancellationToken);
        }
    }
}

internal sealed class MongoDbPhysicalDocumentStoreRuntime
{
    public MongoDbPhysicalDocumentStoreRuntime(
        IMongoDatabase database,
        MongoDbPhysicalStorageModel model,
        MongoDbPhysicalDocumentStoreOptions? options,
        TimeProvider timeProvider,
        MongoDbPhysicalDocumentStoreExecutionHooks? hooks,
        Func<CancellationToken, Task<IClientSessionHandle>>? startSessionAsync,
        MongoDbTransactionCapability? transactionCapability)
    {
        ArgumentNullException.ThrowIfNull(database);
        Database = database
            .WithReadConcern(ReadConcern.Majority)
            .WithWriteConcern(WriteConcern.WMajority);
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Options = options ?? new MongoDbPhysicalDocumentStoreOptions();
        Options.Validate();
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Hooks = hooks ?? MongoDbPhysicalDocumentStoreExecutionHooks.None;
        StartSessionAsync = startSessionAsync ??
            (ct => Database.Client.StartSessionAsync(cancellationToken: ct));
        TransactionCapability = transactionCapability ?? MongoDbTransactionCapability.ForDatabase(Database);
    }

    public IMongoDatabase Database { get; }
    public MongoDbPhysicalStorageModel Model { get; }
    public MongoDbPhysicalDocumentStoreOptions Options { get; }
    public TimeProvider TimeProvider { get; }
    public MongoDbPhysicalDocumentStoreExecutionHooks Hooks { get; }
    public Func<CancellationToken, Task<IClientSessionHandle>> StartSessionAsync { get; }
    public MongoDbTransactionCapability TransactionCapability { get; }
}

internal sealed record MongoDbPhysicalDocumentStoreExecutionHooks(
    Func<IClientSessionHandle, int, CancellationToken, ValueTask> TransactionBodyStarting,
    Func<IClientSessionHandle, int, CancellationToken, ValueTask> CommitStarting,
    Func<IClientSessionHandle, int, MongoException, CancellationToken, ValueTask> CommitResultUnknown,
    Func<IClientSessionHandle, int, CancellationToken, ValueTask> CommitRetryDelayStarting,
    Func<IClientSessionHandle, int, CancellationToken, ValueTask> CommitRetryDelayCompleted)
{
    public Func<IClientSessionHandle, int, CancellationToken, ValueTask> CommitInvoked { get; init; } =
        static (_, _, _) => ValueTask.CompletedTask;

    public Func<IClientSessionHandle, int, CancellationToken, ValueTask> QueryPageRead { get; init; } =
        static (_, _, _) => ValueTask.CompletedTask;

    public Func<IClientSessionHandle, int, CancellationToken, ValueTask> QueryAttemptStarting { get; init; } =
        static (_, _, _) => ValueTask.CompletedTask;

    public Func<IClientSessionHandle, int, CancellationToken, ValueTask> QueryCountRead { get; init; } =
        static (_, _, _) => ValueTask.CompletedTask;

    public Func<IClientSessionHandle, int, CancellationToken, ValueTask> QueryPrimaryHydrationStarting { get; init; } =
        static (_, _, _) => ValueTask.CompletedTask;

    public Func<IClientSessionHandle, int, CancellationToken, ValueTask> QueryRetryDelayStarting { get; init; } =
        static (_, _, _) => ValueTask.CompletedTask;

    public Func<IClientSessionHandle, int, CancellationToken, ValueTask> QueryRetryDelayCompleted { get; init; } =
        static (_, _, _) => ValueTask.CompletedTask;

    public Func<IClientSessionHandle, CancellationToken, ValueTask> CollectionMaintenanceStarting { get; init; } =
        static (_, _) => ValueTask.CompletedTask;

    public Func<CancellationToken, ValueTask> RolloutFenceMissingBeforeInsert { get; init; } =
        static _ => ValueTask.CompletedTask;

    public Func<CancellationToken, ValueTask> RolloutFenceExistingBeforeTouch { get; init; } =
        static _ => ValueTask.CompletedTask;

    public static MongoDbPhysicalDocumentStoreExecutionHooks None { get; } = new(
        static (_, _, _) => ValueTask.CompletedTask,
        static (_, _, _) => ValueTask.CompletedTask,
        static (_, _, _, _) => ValueTask.CompletedTask,
        static (_, _, _) => ValueTask.CompletedTask,
        static (_, _, _) => ValueTask.CompletedTask);
}

/// <summary>
/// One bounded query's selector, the fields it reads, and the index pin it is entitled to.
/// </summary>
/// <remarks>
/// The pin travels with the selector because it is decided from it. Keeping the two together is what
/// stops execution and explain from reaching different conclusions about the same query.
/// </remarks>
internal sealed record MongoDbPhysicalQueryPredicate(
    FilterDefinition<BsonDocument> Filter,
    IReadOnlyList<string> FieldIdentifiers,
    BsonString? IndexHint);

internal sealed class MongoDbPhysicalQueryHandler : IPhysicalDocumentQueryHandler
{
    internal const string LinkedIdentity = "Groundwork.MongoDb.LinkedIndex.v1";
    internal const string CollectionIdentity = "Groundwork.MongoDb.CollectionElements.v1";
    internal const string NativeIdentity = "Groundwork.MongoDb.NativeDocumentFields.v1";
    /// <summary>The field no document carries, so that a selector naming it matches nothing.</summary>
    private const string MatchNoneField = "_groundwork_match_none";
    internal static IReadOnlySet<PortableQueryOperation> Operations { get; } =
        Enum.GetValues<PortableQueryOperation>().ToFrozenSet();
    private readonly IMongoDatabase database;
    private readonly ExecutableStorageRoute route;
    private readonly Func<DocumentScopeSelection> scope;
    private readonly MongoDbPhysicalDocumentStoreOptions options;
    private readonly TimeProvider timeProvider;
    private readonly MongoDbPhysicalDocumentStoreExecutionHooks hooks;
    private readonly MongoDbTransactionCapability transactionCapability;
    private readonly MongoDbPhysicalQueryExplainer explainer;

    public MongoDbPhysicalQueryHandler(
        string identity,
        PhysicalQuerySourceKind source,
        IMongoDatabase database,
        ExecutableStorageRoute route,
        Func<DocumentScopeSelection> scope,
        IReadOnlyList<PhysicalQueryHandlerCertification> certifications,
        IReadOnlyDictionary<string, string> nativeFieldIdentifiers,
        MongoDbPhysicalDocumentStoreOptions options,
        TimeProvider timeProvider,
        MongoDbPhysicalDocumentStoreExecutionHooks hooks,
        MongoDbTransactionCapability transactionCapability)
    {
        Identity = identity;
        Source = source;
        this.database = database;
        this.route = route;
        this.scope = scope;
        this.options = options;
        this.timeProvider = timeProvider;
        this.hooks = hooks;
        this.transactionCapability = transactionCapability;
        explainer = new MongoDbPhysicalQueryExplainer(
            database,
            route,
            scope,
            transactionCapability);
        Certifications = Array.AsReadOnly(certifications.ToArray());
        NativeFieldIdentifiers = source == PhysicalQuerySourceKind.NativeDocumentFields
            ? nativeFieldIdentifiers.ToFrozenDictionary(StringComparer.Ordinal)
            : FrozenDictionary<string, string>.Empty;
    }

    public string Identity { get; }
    public PhysicalQuerySourceKind Source { get; }
    public IReadOnlySet<PortableQueryOperation> SupportedOperations => Operations;
    public IReadOnlyDictionary<string, string> NativeFieldIdentifiers { get; }
    public IReadOnlyList<PhysicalQueryHandlerCertification> Certifications { get; }
    public bool SupportsCompoundPredicates => true;
    public bool SupportsDisjunction => true;
    public bool SupportsOffsetPaging => true;
    public bool SupportsKeysetPaging => true;
    public bool SupportsCount => true;
    public bool SupportsAny => true;
    public bool SupportsFirst => true;
    public bool SupportsLatestPerKey => true;

    public async Task<DocumentQueryResult> QueryAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
    {
        if (plan.AccessKind == PhysicalQueryAccessKind.CollectionElementsThenPrimary)
            return await QueryCollectionMembershipAsync(query, plan, cancellationToken);
        var collection = database.GetCollection<BsonDocument>(plan.LookupObject.Identifier);
        var resolvedScope = scope();
        DocumentQueryContinuationCodec.ValidateScope(plan, resolvedScope);
        var basePredicate = BuildPredicate(query, plan, resolvedScope, route);
        var pagePredicate = BuildPagePredicate(query, plan, resolvedScope, basePredicate);
        var sort = BuildSort(query, plan);
        var indexHint = basePredicate.IndexHint;
        await transactionCapability.EnsureSupportedAsync(
            [route.StorageUnit.Value],
            "physical snapshot query",
            cancellationToken);
        var started = timeProvider.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var session = await database.Client.StartSessionAsync(cancellationToken: cancellationToken);
            session.StartTransaction(new TransactionOptions(ReadConcern.Snapshot));
            try
            {
                await hooks.QueryAttemptStarting(session, attempt, cancellationToken);
                if (query.LatestPerKeyPath is not null)
                {
                    var renderedFilter = RenderFilter(collection, basePredicate.Filter);
                    var latestTotal = await CountLatestPerKeyAsync(
                        collection,
                        session,
                        renderedFilter,
                        query,
                        plan,
                        indexHint,
                        cancellationToken);
                    await hooks.QueryCountRead(session, attempt, cancellationToken);
                    if (latestTotal == 0 || query.Take == 0)
                    {
                        await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
                        return new DocumentQueryResult([], latestTotal);
                    }

                    var latestFound = await collection.Aggregate<BsonDocument>(
                            session,
                            LatestPerKeyPagePipeline(renderedFilter, query, plan).ToArray(),
                            new AggregateOptions { Hint = indexHint })
                        .ToListAsync(cancellationToken);
                    await hooks.QueryPageRead(session, attempt, cancellationToken);
                    var latestDocuments = plan.RequiresPrimaryLookup
                        ? await LoadPrimaryAsync(session, latestFound, attempt, cancellationToken)
                        : latestFound.Select(document => MongoDbPhysicalDocumentStore.ReadEnvelope(route, document)).ToArray();
                    await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
                    return new DocumentQueryResult(latestDocuments, latestTotal);
                }

                var total = await collection.CountDocumentsAsync(
                    session,
                    basePredicate.Filter,
                    new CountOptions { Hint = indexHint },
                    cancellationToken: cancellationToken);
                await hooks.QueryCountRead(session, attempt, cancellationToken);
                if (total == 0 || query.Take == 0)
                {
                    await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
                    return new DocumentQueryResult([], total);
                }
                var find = collection.Find(
                        session,
                        pagePredicate.Filter,
                        new FindOptions { Hint = indexHint })
                    .Sort(sort)
                    .Skip(plan.PagingSupport == QueryPagingSupport.Cursor ? 0 : query.Skip ?? 0);
                find = find.Limit(PageReadLimit(query, plan));
                var found = (await find.ToListAsync(cancellationToken)).ToList();
                await hooks.QueryPageRead(session, attempt, cancellationToken);
                var hasMore = query.Take is { } take &&
                              take < int.MaxValue &&
                              found.Count > take;
                if (hasMore)
                    found.RemoveAt(found.Count - 1);
                var documents = plan.RequiresPrimaryLookup
                    ? await LoadPrimaryAsync(session, found, attempt, cancellationToken)
                    : found.Select(document => MongoDbPhysicalDocumentStore.ReadEnvelope(route, document)).ToArray();
                var next = hasMore && found.Count != 0
                    ? DocumentQueryContinuationCodec.Encode(
                        query,
                        plan,
                        resolvedScope,
                        ReadContinuationValues(found[^1], query, plan))
                    : null;
                await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
                return new DocumentQueryResult(documents, total, next);
            }
            catch (MongoException exception) when (
                MongoDbPhysicalDocumentStore.IsTransientTransactionConflict(exception) &&
                !cancellationToken.IsCancellationRequested)
            {
                await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
                if (attempt >= options.MaximumTransactionAttempts ||
                    timeProvider.GetElapsedTime(started) >= options.TransactionRetryTimeout)
                {
                    throw;
                }
                await hooks.QueryRetryDelayStarting(session, attempt, cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, 2 << Math.Min(attempt, 5))), cancellationToken);
                await hooks.QueryRetryDelayCompleted(session, attempt, cancellationToken);
                if (timeProvider.GetElapsedTime(started) >= options.TransactionRetryTimeout)
                    throw;
            }
            catch
            {
                await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
                throw;
            }
        }
    }

    private async Task<DocumentQueryResult> QueryCollectionMembershipAsync(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        CancellationToken cancellationToken)
    {
        var elementStorage = route.CollectionElementStorages.Single(storage =>
            storage.Storage.Name == plan.LookupObject);
        var resolvedScope = scope();
        var pipeline = CollectionMembershipPipeline(query, plan, route, resolvedScope);

        await transactionCapability.EnsureSupportedAsync(
            [route.StorageUnit.Value],
            "physical collection membership query",
            cancellationToken);
        using var session = await database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        session.StartTransaction(new TransactionOptions(ReadConcern.Snapshot));
        try
        {
            var result = await database.GetCollection<BsonDocument>(elementStorage.Storage.Name.Identifier)
                .Aggregate<BsonDocument>(session, pipeline.ToArray())
                .SingleAsync(cancellationToken);
            await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
            var total = result["metadata"].AsBsonArray.Count == 0
                ? 0
                : result["metadata"].AsBsonArray[0]["total"].ToInt64();
            var documents = result["data"].AsBsonArray
                .Select(item => MongoDbPhysicalDocumentStore.ReadEnvelope(route, item.AsBsonDocument))
                .ToArray();
            return new DocumentQueryResult(documents, total);
        }
        catch
        {
            await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
            throw;
        }
    }

    internal static IReadOnlyList<BsonDocument> CollectionMembershipPipeline(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        DocumentScopeSelection resolvedScope)
    {
        if (query.Continuation is not null || query.LatestPerKeyPath is not null)
            throw new NotSupportedException("Collection membership queries do not support cursor or latest-per-key execution.");
        DocumentQueryContinuationCodec.ValidateScope(plan, resolvedScope);
        var elementStorage = route.CollectionElementStorages.Single(storage =>
            storage.Storage.Name == plan.LookupObject);
        var allValues = query.Clauses
            .SelectMany(clause => clause.Comparisons)
            .SelectMany(comparison => comparison.Values)
            .Select(value => value ?? throw new InvalidOperationException("Collection membership values cannot be null."))
            .Select(value => MongoDbPhysicalProjectionValues.ParseQueryValue(elementStorage.Value, value))
            .Distinct()
            .ToArray();
        if (allValues.Length == 0)
            throw new InvalidOperationException("Collection membership execution requires at least one requested value.");

        var match = new BsonDocument
        {
            [elementStorage.DocumentKind.Column.Identifier] = route.Discriminator.Value,
            [elementStorage.Value.Column.Identifier] = new BsonDocument("$in", new BsonArray(allValues))
        };
        if (!resolvedScope.AcrossScopes)
            match[elementStorage.StorageScope.Column.Identifier] = resolvedScope.StorageKey;

        BsonDocument ComparisonExpression(DocumentQueryComparison comparison)
        {
            var requested = comparison.Values
                .Select(value => value ?? throw new InvalidOperationException("Collection membership values cannot be null."))
                .Select(value => MongoDbPhysicalProjectionValues.ParseQueryValue(elementStorage.Value, value))
                .Distinct()
                .ToArray();
            return comparison.Operator switch
            {
                QueryComparisonOperator.CollectionContains => new BsonDocument(
                    "$in",
                    new BsonArray { requested.Single(), "$matchedValues" }),
                QueryComparisonOperator.CollectionContainsAll => new BsonDocument(
                    "$setIsSubset",
                    new BsonArray { new BsonArray(requested), "$matchedValues" }),
                _ => throw new InvalidOperationException(
                    $"Collection membership plans do not support '{comparison.Operator}'.")
            };
        }

        var ownerId = new BsonDocument
        {
            ["kind"] = $"${elementStorage.DocumentKind.Column.Identifier}",
            ["scope"] = $"${elementStorage.StorageScope.Column.Identifier}",
            ["lookup"] = $"${elementStorage.IdLookupKey.Column.Identifier}",
            ["comparison"] = $"${elementStorage.IdComparisonKey.Column.Identifier}"
        };
        var pipeline = new List<BsonDocument>
        {
            new("$match", match),
            new("$group", new BsonDocument
            {
                ["_id"] = ownerId,
                ["matchedValues"] = new BsonDocument("$addToSet", $"${elementStorage.Value.Column.Identifier}")
            })
        };
        var clauseExpressions = query.Clauses.Select(clause =>
            clause.Comparisons.Count == 0
                ? new BsonDocument("$eq", new BsonArray { 1, 0 })
                : new BsonDocument("$or", new BsonArray(clause.Comparisons.Select(ComparisonExpression)))).ToArray();
        if (clauseExpressions.Length != 0)
            pipeline.Add(new BsonDocument("$match", new BsonDocument(
                "$expr",
                new BsonDocument("$and", new BsonArray(clauseExpressions)))));
        pipeline.Add(new BsonDocument("$lookup", new BsonDocument
        {
            ["from"] = route.PrimaryStorage.Name.Identifier,
            ["let"] = new BsonDocument
            {
                ["kind"] = "$_id.kind",
                ["scope"] = "$_id.scope",
                ["lookup"] = "$_id.lookup",
                ["comparison"] = "$_id.comparison"
            },
            ["pipeline"] = new BsonArray
            {
                new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument("$eq", new BsonArray { $"${route.Discriminator.Column.Identifier}", "$$kind" }),
                    new BsonDocument("$eq", new BsonArray { $"${route.ScopeKey.Column.Identifier}", "$$scope" }),
                    new BsonDocument("$eq", new BsonArray { $"${route.Envelope.Identity.LookupKey.Identifier}", "$$lookup" }),
                    new BsonDocument("$eq", new BsonArray { $"${route.Envelope.Identity.ComparisonKey.Identifier}", "$$comparison" })
                })))
            },
            ["as"] = "document"
        }));
        pipeline.Add(new BsonDocument("$unwind", "$document"));
        pipeline.Add(new BsonDocument("$replaceWith", "$document"));

        var dataPipeline = new BsonArray();
        var order = DocumentQueryOrderResolver.Resolve(query, plan);
        if (order.Count != 0)
        {
            dataPipeline.Add(new BsonDocument("$sort", new BsonDocument(order.Select(item =>
                new BsonElement(item.Field.Identifier, item.Direction == PhysicalSortDirection.Ascending ? 1 : -1)))));
        }
        if (query.Skip is > 0)
            dataPipeline.Add(new BsonDocument("$skip", query.Skip.Value));

        switch (query.ResultOperation)
        {
            case BoundedQueryResultOperation.Documents:
                if (query.Take is { } take)
                    dataPipeline.Add(new BsonDocument("$limit", take));
                pipeline.Add(new BsonDocument("$facet", new BsonDocument
                {
                    ["metadata"] = new BsonArray { new BsonDocument("$count", "total") },
                    ["data"] = dataPipeline
                }));
                break;
            case BoundedQueryResultOperation.Count:
                pipeline.Add(new BsonDocument("$count", "total"));
                break;
            case BoundedQueryResultOperation.Any:
                pipeline.Add(new BsonDocument("$limit", 1));
                break;
            case BoundedQueryResultOperation.First:
                foreach (var stage in dataPipeline)
                    pipeline.Add(stage.AsBsonDocument);
                pipeline.Add(new BsonDocument("$limit", 1));
                break;
            default:
                throw new NotSupportedException(
                    $"Collection membership execution does not support result operation '{query.ResultOperation}'.");
        }
        return pipeline;
    }

    public async Task<long> CountAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
    {
        if (plan.AccessKind == PhysicalQueryAccessKind.CollectionElementsThenPrimary)
        {
            await transactionCapability.EnsureSupportedAsync(
                [route.StorageUnit.Value],
                "physical collection membership count",
                cancellationToken);
            var elementStorage = route.CollectionElementStorages.Single(storage =>
                storage.Storage.Name == plan.LookupObject);
            var result = await database.GetCollection<BsonDocument>(elementStorage.Storage.Name.Identifier)
                .Aggregate<BsonDocument>(CollectionMembershipPipeline(query, plan, route, scope()).ToArray())
                .SingleOrDefaultAsync(cancellationToken);
            return result?.GetValue("total", 0).ToInt64() ?? 0;
        }
        var collection = database.GetCollection<BsonDocument>(plan.LookupObject.Identifier);
        var predicate = BuildPredicate(query, plan, scope(), route);
        var filter = predicate.Filter;
        var indexHint = predicate.IndexHint;
        await transactionCapability.EnsureSupportedAsync(
            [route.StorageUnit.Value],
            "physical count query",
            cancellationToken);
        return query.LatestPerKeyPath is null
            ? await collection.CountDocumentsAsync(
                filter,
                new CountOptions { Hint = indexHint },
                cancellationToken)
            : await CountLatestPerKeyAsync(
                collection,
                session: null,
                RenderFilter(collection, filter),
                query,
                plan,
                indexHint,
                cancellationToken);
    }

    public async Task<DocumentEnvelope?> FirstOrDefaultAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
    {
        if (plan.AccessKind == PhysicalQueryAccessKind.CollectionElementsThenPrimary)
        {
            await transactionCapability.EnsureSupportedAsync(
                [route.StorageUnit.Value],
                "physical collection membership first",
                cancellationToken);
            var elementStorage = route.CollectionElementStorages.Single(storage =>
                storage.Storage.Name == plan.LookupObject);
            var document = await database.GetCollection<BsonDocument>(elementStorage.Storage.Name.Identifier)
                .Aggregate<BsonDocument>(CollectionMembershipPipeline(query, plan, route, scope()).ToArray())
                .SingleOrDefaultAsync(cancellationToken);
            return document is null
                ? null
                : MongoDbPhysicalDocumentStore.ReadEnvelope(route, document);
        }
        var result = await QueryAsync(new DocumentQuery(
            query.DocumentKind, query.QueryIdentity, query.Clauses, query.Order, query.Skip, 1,
            query.Continuation, query.LatestPerKeyPath), plan, cancellationToken);
        return result.Documents.FirstOrDefault();
    }

    public async Task<bool> AnyAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
    {
        if (plan.AccessKind == PhysicalQueryAccessKind.CollectionElementsThenPrimary)
        {
            await transactionCapability.EnsureSupportedAsync(
                [route.StorageUnit.Value],
                "physical collection membership existence",
                cancellationToken);
            var elementStorage = route.CollectionElementStorages.Single(storage =>
                storage.Storage.Name == plan.LookupObject);
            return await database.GetCollection<BsonDocument>(elementStorage.Storage.Name.Identifier)
                .Aggregate<BsonDocument>(CollectionMembershipPipeline(query, plan, route, scope()).ToArray())
                .AnyAsync(cancellationToken);
        }
        var predicate = BuildPredicate(query, plan, scope(), route);
        await transactionCapability.EnsureSupportedAsync(
            [route.StorageUnit.Value],
            "physical existence query",
            cancellationToken);
        return await database.GetCollection<BsonDocument>(plan.LookupObject.Identifier)
            .Find(predicate.Filter, new FindOptions { Hint = predicate.IndexHint })
            .Limit(1)
            .AnyAsync(cancellationToken);
    }

    public Task<PhysicalDocumentQueryExplanation> ExplainAsync(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        CancellationToken cancellationToken) =>
        explainer.ExplainAsync(query, plan, cancellationToken);

    internal static FilterDefinition<BsonDocument> BuildFilter(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        DocumentScopeSelection scope,
        ExecutableStorageRoute route) =>
        BuildPredicate(query, plan, scope, route).Filter;

    /// <summary>
    /// Builds the selector for one bounded query together with the index pin it may carry.
    /// </summary>
    /// <remarks>
    /// The pin is decided from the predicate rather than from the plan alone, because an index that
    /// declares <see cref="MissingValueBehavior.Excluded"/> is emitted as a partial index and therefore
    /// holds fewer documents than the collection. MongoDB accepts a hint the predicate does not imply,
    /// answers from the smaller index, and returns the short result set without complaint — unlike SQL
    /// Server, which refuses the plan outright. Deciding here is what makes that silence impossible.
    /// </remarks>
    internal static MongoDbPhysicalQueryPredicate BuildPredicate(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        DocumentScopeSelection scope,
        ExecutableStorageRoute route)
    {
        var fieldIdentifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            plan.Discriminator.Identifier
        };
        var filters = new List<FilterDefinition<BsonDocument>>
        {
            Builders<BsonDocument>.Filter.Eq(plan.Discriminator.Identifier, plan.StorageUnit.Value)
        };
        if (scope.StorageKey is not null)
        {
            filters.Add(Builders<BsonDocument>.Filter.Eq(plan.Scope.Field.Identifier, scope.StorageKey));
            fieldIdentifiers.Add(plan.Scope.Field.Identifier);
        }
        var provenPresentFields = new HashSet<string>(StringComparer.Ordinal);
        var matchesNoDocuments = false;
        foreach (var clause in query.Clauses)
        {
            if (clause.Comparisons.Count == 0)
            {
                // A clause admitting no document makes the whole conjunction one, and a predicate that
                // returns nothing cannot omit anything — so it keeps the pin whatever the index excludes.
                return new MongoDbPhysicalQueryPredicate(
                    Builders<BsonDocument>.Filter.Eq(MatchNoneField, true),
                    [MatchNoneField],
                    PlanIndexHint(query, plan, route, provenPresentFields, matchesNoDocuments: true).Hint);
            }
            var alternatives = new List<FilterDefinition<BsonDocument>>();
            HashSet<string>? clausePresent = null;
            foreach (var comparison in clause.Comparisons)
            {
                var field = plan.Predicates.Single(predicate => predicate.Path == comparison.Path).Field;
                fieldIdentifiers.Add(field.Identifier);
                alternatives.Add(Comparison(comparison, plan, field, route));
                // An alternative that matches nothing contributes no documents to the disjunction, so it
                // neither widens what the clause can return nor constrains it. Folding it into the
                // intersection below would wrongly read it as "proves nothing".
                if (MatchesNoDocuments(comparison))
                    continue;
                var alternativePresent = new HashSet<string>(StringComparer.Ordinal);
                // The identifier alone is the right key, without first proving it names a projected
                // column: an index only ever excludes documents for its own projected columns, so a
                // native path such as "native_content.name" simply never appears in that set. Asking the
                // plan instead would be wrong here — MongoDB reads projected columns through its native
                // field source, which reports the field as a native path while still naming the column.
                if (RejectsMissingValues(comparison))
                    alternativePresent.Add(field.Identifier);
                // The clause is a disjunction, so it only proves a field present when every alternative
                // does. One alternative that can match a document lacking the field is enough to make
                // the whole clause able to.
                if (clausePresent is null)
                    clausePresent = alternativePresent;
                else
                    clausePresent.IntersectWith(alternativePresent);
            }
            filters.Add(Builders<BsonDocument>.Filter.Or(alternatives));
            if (clausePresent is null)
                matchesNoDocuments = true;
            else
                provenPresentFields.UnionWith(clausePresent);
        }
        var (indexHint, presentFields) =
            PlanIndexHint(query, plan, route, provenPresentFields, matchesNoDocuments);
        foreach (var field in presentFields)
        {
            filters.Add(Builders<BsonDocument>.Filter.Exists(field, true));
            fieldIdentifiers.Add(field);
        }
        return new MongoDbPhysicalQueryPredicate(
            Builders<BsonDocument>.Filter.And(filters),
            fieldIdentifiers.Order(StringComparer.Ordinal).ToArray(),
            indexHint);
    }

    internal static MongoDbPhysicalQueryPredicate BuildPagePredicate(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        DocumentScopeSelection scope,
        MongoDbPhysicalQueryPredicate basePredicate)
    {
        if (query.Continuation is null)
            return basePredicate;
        var values = DocumentQueryContinuationCodec.Decode(query.Continuation, query, plan, scope);
        var order = DocumentQueryOrderResolver.Resolve(query, plan);
        // The continuation only narrows the page, so it can never reach a document the base predicate
        // already excluded. The pin decided for that predicate therefore still holds.
        return new MongoDbPhysicalQueryPredicate(
            Builders<BsonDocument>.Filter.And(
                basePredicate.Filter,
                ContinuationFilter(order, values)),
            basePredicate.FieldIdentifiers
                .Concat(order.Select(item => item.Field.Identifier))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            basePredicate.IndexHint);
    }

    internal static SortDefinition<BsonDocument> BuildSort(DocumentQuery query, PhysicalQueryPlan plan)
    {
        var requested = DocumentQueryOrderResolver.Resolve(query, plan);
        return Builders<BsonDocument>.Sort.Combine(requested.Select(order =>
            order.Direction == PhysicalSortDirection.Ascending
                ? Builders<BsonDocument>.Sort.Ascending(order.Field.Identifier)
                : Builders<BsonDocument>.Sort.Descending(order.Field.Identifier)));
    }

    /// <summary>
    /// Decides whether the planned index may be pinned for the predicate just built, and returns the
    /// fields whose presence the selector has to state alongside it.
    /// </summary>
    /// <remarks>
    /// A pinned partial index can only serve a predicate that provably matches none of the documents it
    /// omits. Where that holds, the presence conjuncts are redundant by construction and exist so the
    /// selector spells out the implication the index filter assumes. Where it does not, pinning would
    /// drop documents the predicate can match, so a scale-bearing query is refused by name rather than
    /// silently under-served, and any other query falls back to letting MongoDB choose an index.
    /// </remarks>
    private static (BsonString? Hint, IReadOnlyList<string> PresentFields) PlanIndexHint(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        IReadOnlySet<string> provenPresentFields,
        bool matchesNoDocuments)
    {
        var index = PlanIndex(plan, route);
        if (index is null)
            return (null, []);
        var hint = new BsonString(index.Name.Identifier);
        if (matchesNoDocuments)
            return (hint, []);
        // The one definition of what the index omits, shared with the schema side that emits the partial
        // filter and the validation that compares them. A second copy here is how they start to disagree.
        var excluded = PhysicalIndexNullExclusion.Columns(route, index);
        if (excluded.Length == 0)
            return (hint, []);
        var unproven = excluded.Where(column => !provenPresentFields.Contains(column)).ToArray();
        if (unproven.Length == 0)
            return (hint, excluded);
        if (plan.IsScaleBearing)
        {
            throw new InvalidOperationException(
                $"Document query '{query.QueryIdentity}' is scale-bearing on physical index " +
                $"'{index.Name.Identifier}', which declares MissingValueBehavior.Excluded and so omits " +
                $"documents whose {string.Join(" or ", unproven.Select(column => $"'{column}'"))} is " +
                "absent. Its predicate can match those documents, so the index cannot serve the query " +
                "without dropping them — and MongoDB drops them silently rather than refusing the hint. " +
                "Declare the index MissingValueBehavior.IncludedAsNull so it keeps every document — an " +
                "already-applied index is rebuilt under it, which is additive. A unique index that keys a " +
                "nullable column cannot take that route, because null-distinct uniqueness is not portable " +
                "(GW-ROUTE-007); bind the query to an index that keys no nullable column, make the column " +
                "non-nullable, or declare a new index identity for the shape the query needs.");
        }
        return (null, []);
    }

    /// <summary>The route index the plan pins, or <see langword="null"/> when the plan pins none.</summary>
    private static ExecutablePhysicalIndexRoute? PlanIndex(PhysicalQueryPlan plan, ExecutableStorageRoute route)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(route);

        if (plan.AccessKind == PhysicalQueryAccessKind.CollectionElementsThenPrimary || plan.IndexName is null)
            return null;

        var routeIndex = route.Indexes.SingleOrDefault(index =>
            string.Equals(index.Identity, plan.LogicalIndexIdentity, StringComparison.Ordinal));
        if (routeIndex is null || routeIndex.Name != plan.IndexName)
        {
            throw new InvalidOperationException(
                $"MongoDB physical query '{plan.QueryIdentity}' cannot resolve declared index " +
                $"'{plan.IndexName.Identifier}' for logical index '{plan.LogicalIndexIdentity}'.");
        }

        return routeIndex;
    }

    /// <summary>
    /// Whether the comparison, as rendered by <see cref="Comparison"/>, can only match documents that
    /// carry the field.
    /// </summary>
    /// <remarks>
    /// Presence, not non-nullness: a document may hold an explicit null and the partial filter keeps it,
    /// so what has to be proved is only that the field is there. The reading is MongoDB's, and it parts
    /// company with SQL on <c>NotEqual</c>: <c>{$ne: v}</c> also matches a document that has no such
    /// field, so it proves nothing, where relational <c>&lt;&gt; @p</c> is unknown for null and therefore
    /// does. Only <c>{$ne: null}</c> excludes the absent case, which is why the value decides
    /// <c>NotEqual</c> in the opposite direction from every other operator. <c>NotContains</c> proves
    /// nothing either way — it is defined to match a null or absent field — and <c>Equal</c> to null
    /// renders <c>{field: null}</c>, which matches the absent documents together with the null ones.
    /// </remarks>
    private static bool RejectsMissingValues(DocumentQueryComparison comparison) => comparison.Operator switch
    {
        QueryComparisonOperator.NotEqual => comparison.Values.Count == 0 || comparison.Values[0] is null,
        QueryComparisonOperator.NotContains => false,
        QueryComparisonOperator.In =>
            comparison.Values.Count > 0 && comparison.Values.All(value => value is not null),
        QueryComparisonOperator.Equal or
            QueryComparisonOperator.GreaterThan or QueryComparisonOperator.GreaterThanOrEqual or
            QueryComparisonOperator.LessThan or QueryComparisonOperator.LessThanOrEqual or
            QueryComparisonOperator.Contains or QueryComparisonOperator.StartsWith =>
            comparison.Values[0] is not null,
        _ => false
    };

    /// <summary>
    /// Whether the comparison renders the match-nothing sentinel. An empty membership set is the only
    /// shape that does, on the projected and the identity path alike.
    /// </summary>
    private static bool MatchesNoDocuments(DocumentQueryComparison comparison) =>
        comparison.Operator == QueryComparisonOperator.In && comparison.Values.Count == 0;

    internal static IReadOnlyList<BsonDocument> LatestPerKeyPagePipeline(
        BsonDocument renderedFilter,
        DocumentQuery query,
        PhysicalQueryPlan plan)
    {
        var pipeline = LatestPerKeySelectionPipeline(renderedFilter, query, plan).ToList();
        if (query.Skip is { } skip && skip != 0)
            pipeline.Add(new BsonDocument("$skip", skip));
        pipeline.Add(new BsonDocument("$limit", PageReadLimit(query, plan)));
        return pipeline;
    }

    internal static IReadOnlyList<BsonDocument> LatestPerKeyCountPipeline(
        BsonDocument renderedFilter,
        DocumentQuery query,
        PhysicalQueryPlan plan)
    {
        var group = LatestPerKeyField(query, plan);
        return
        [
            new BsonDocument("$match", renderedFilter),
            new BsonDocument("$group", new BsonDocument("_id", $"${group.Identifier}")),
            new BsonDocument("$count", "value")
        ];
    }

    private static IReadOnlyList<BsonDocument> LatestPerKeySelectionPipeline(
        BsonDocument renderedFilter,
        DocumentQuery query,
        PhysicalQueryPlan plan)
    {
        var group = LatestPerKeyField(query, plan);
        var sort = SortDocument(query, plan);
        return
        [
            new BsonDocument("$match", renderedFilter),
            new BsonDocument("$sort", sort),
            new BsonDocument("$group", new BsonDocument
            {
                ["_id"] = $"${group.Identifier}",
                ["document"] = new BsonDocument("$first", "$$ROOT")
            }),
            new BsonDocument("$replaceWith", "$document"),
            new BsonDocument("$sort", sort)
        ];
    }

    internal static BsonDocument SortDocument(DocumentQuery query, PhysicalQueryPlan plan)
    {
        var sort = new BsonDocument();
        foreach (var order in DocumentQueryOrderResolver.Resolve(query, plan))
            sort[order.Field.Identifier] = order.Direction == PhysicalSortDirection.Ascending ? 1 : -1;
        return sort;
    }

    private static PhysicalQueryField LatestPerKeyField(DocumentQuery query, PhysicalQueryPlan plan)
    {
        var path = query.LatestPerKeyPath
                   ?? throw new InvalidOperationException("Latest-per-key execution requires a grouping path.");
        return plan.Order.Single(order => !order.IsIdentityTieBreak && order.Path == path).Field;
    }

    private static BsonDocument RenderFilter(
        IMongoCollection<BsonDocument> collection,
        FilterDefinition<BsonDocument> filter) =>
        filter.Render(new RenderArgs<BsonDocument>(
            collection.DocumentSerializer,
            BsonSerializer.SerializerRegistry));

    private static async Task<long> CountLatestPerKeyAsync(
        IMongoCollection<BsonDocument> collection,
        IClientSessionHandle? session,
        BsonDocument renderedFilter,
        DocumentQuery query,
        PhysicalQueryPlan plan,
        BsonValue? indexHint,
        CancellationToken cancellationToken)
    {
        var pipeline = LatestPerKeyCountPipeline(renderedFilter, query, plan);
        var options = new AggregateOptions { Hint = indexHint };
        var documents = session is null
            ? await collection.Aggregate<BsonDocument>(pipeline.ToArray(), options).ToListAsync(cancellationToken)
            : await collection.Aggregate<BsonDocument>(session, pipeline.ToArray(), options).ToListAsync(cancellationToken);
        return documents.FirstOrDefault()?["value"].ToInt64() ?? 0L;
    }

    private static FilterDefinition<BsonDocument> ContinuationFilter(
        IReadOnlyList<PhysicalQueryOrder> order,
        IReadOnlyList<DocumentQueryContinuationValue> values)
    {
        var alternatives = new List<FilterDefinition<BsonDocument>>();
        for (var boundaryIndex = 0; boundaryIndex < order.Count; boundaryIndex++)
        {
            var conjunction = new List<FilterDefinition<BsonDocument>>();
            for (var prefixIndex = 0; prefixIndex < boundaryIndex; prefixIndex++)
            {
                conjunction.Add(Builders<BsonDocument>.Filter.Eq(
                    order[prefixIndex].Field.Identifier,
                    ToBsonValue(values[prefixIndex])));
            }

            conjunction.Add(ContinuationAfter(order[boundaryIndex], values[boundaryIndex]));
            alternatives.Add(Builders<BsonDocument>.Filter.And(conjunction));
        }
        return Builders<BsonDocument>.Filter.Or(alternatives);
    }

    private static FilterDefinition<BsonDocument> ContinuationAfter(
        PhysicalQueryOrder order,
        DocumentQueryContinuationValue value)
    {
        var field = order.Field.Identifier;
        if (value.ScalarKind == DocumentQueryContinuationScalarKind.Null)
        {
            return order.Direction == PhysicalSortDirection.Ascending
                ? Builders<BsonDocument>.Filter.Ne(field, BsonNull.Value)
                : Builders<BsonDocument>.Filter.Eq(MatchNoneField, true);
        }

        var boundary = ToBsonValue(value);
        return order.Direction == PhysicalSortDirection.Ascending
            ? Builders<BsonDocument>.Filter.Gt(field, boundary)
            : Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Lt(field, boundary),
                Builders<BsonDocument>.Filter.Eq(field, BsonNull.Value));
    }

    internal static int PageReadLimit(DocumentQuery query, PhysicalQueryPlan plan) =>
        plan.PagingSupport == QueryPagingSupport.Cursor &&
        query.Take is { } take &&
        take < int.MaxValue
            ? take + 1
            : query.Take ?? int.MaxValue;

    private static IReadOnlyList<DocumentQueryContinuationValue> ReadContinuationValues(
        BsonDocument document,
        DocumentQuery query,
        PhysicalQueryPlan plan) =>
        DocumentQueryOrderResolver.Resolve(query, plan)
            .Select(order =>
                TryReadDotted(document, order.Field.Identifier, out var value)
                    ? FromBsonValue(order.Field.ValueKind, value)
                    : new DocumentQueryContinuationValue(
                        order.Field.ValueKind,
                        DocumentQueryContinuationScalarKind.Null,
                        null))
            .ToArray();

    private static DocumentQueryContinuationValue FromBsonValue(IndexValueKind kind, BsonValue value)
    {
        if (value.IsBsonNull)
            return new(kind, DocumentQueryContinuationScalarKind.Null, null);
        return value.BsonType switch
        {
            BsonType.String => new(kind, DocumentQueryContinuationScalarKind.String, value.AsString),
            BsonType.Int32 => new(
                kind,
                DocumentQueryContinuationScalarKind.Int64,
                value.AsInt32.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            BsonType.Int64 => new(
                kind,
                DocumentQueryContinuationScalarKind.Int64,
                value.AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            BsonType.Double => new(
                kind,
                DocumentQueryContinuationScalarKind.Double,
                value.AsDouble.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
            BsonType.Decimal128 => new(
                kind,
                DocumentQueryContinuationScalarKind.Decimal,
                value.AsDecimal128.ToString()),
            BsonType.Boolean => new(
                kind,
                DocumentQueryContinuationScalarKind.Boolean,
                value.AsBoolean.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            BsonType.DateTime => new(
                kind,
                DocumentQueryContinuationScalarKind.DateTimeOffset,
                new DateTimeOffset(value.ToUniversalTime()).ToString(
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture)),
            BsonType.Binary => new(
                kind,
                DocumentQueryContinuationScalarKind.Binary,
                Convert.ToBase64String(value.AsBsonBinaryData.Bytes)),
            _ => throw new InvalidOperationException(
                $"MongoDB physical query order returned unsupported BSON type '{value.BsonType}'.")
        };
    }

    private static BsonValue ToBsonValue(DocumentQueryContinuationValue value) =>
        value.ScalarKind switch
        {
            DocumentQueryContinuationScalarKind.Null => BsonNull.Value,
            DocumentQueryContinuationScalarKind.String => new BsonString(value.Value!),
            DocumentQueryContinuationScalarKind.Int64 => new BsonInt64(long.Parse(
                value.Value!,
                System.Globalization.CultureInfo.InvariantCulture)),
            DocumentQueryContinuationScalarKind.Decimal => new BsonDecimal128(Decimal128.Parse(value.Value!)),
            DocumentQueryContinuationScalarKind.Double => new BsonDouble(double.Parse(
                value.Value!,
                System.Globalization.CultureInfo.InvariantCulture)),
            DocumentQueryContinuationScalarKind.Boolean => new BsonBoolean(bool.Parse(value.Value!)),
            DocumentQueryContinuationScalarKind.DateTimeOffset => new BsonDateTime(DateTimeOffset.Parse(
                    value.Value!,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind)
                .UtcDateTime),
            DocumentQueryContinuationScalarKind.Binary => new BsonBinaryData(Convert.FromBase64String(value.Value!)),
            _ => throw new InvalidDocumentQueryContinuationException(
                "The document-query continuation contains an unsupported MongoDB physical value.")
        };

    private static bool TryReadDotted(BsonDocument document, string path, out BsonValue value)
    {
        value = document;
        foreach (var segment in path.Split('.'))
        {
            if (value is not BsonDocument current || !current.TryGetValue(segment, out value!))
            {
                value = BsonNull.Value;
                return false;
            }
        }
        return true;
    }

    private async Task<IReadOnlyList<DocumentEnvelope>> LoadPrimaryAsync(
        IClientSessionHandle session,
        IReadOnlyList<BsonDocument> linked,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (linked.Count == 0) return [];
        var rel = route.LinkedRelationship!;
        var filters = linked.Select(document =>
            MongoDbPhysicalDocumentIdentity.PrimaryExactFilter(route, document));
        await hooks.QueryPrimaryHydrationStarting(session, attempt, cancellationToken);
        var primary = await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .Find(session, Builders<BsonDocument>.Filter.Or(filters))
            .Limit(linked.Count)
            .ToListAsync(cancellationToken);
        var byKey = primary.ToDictionary(document => Key(
            document,
            route.Envelope.Identity,
            route.Envelope.StorageScope));
        return linked.Select(document => byKey[Key(document, rel.Identity, rel.StorageScope)])
            .Select(document => MongoDbPhysicalDocumentStore.ReadEnvelope(route, document)).ToArray();
    }

    private static DocumentIdentity Key(
        BsonDocument document,
        ExecutableDocumentIdentityRoute identity,
        ExecutableColumnRoute scope) =>
        new(
            document[scope.Identifier].AsString,
            document[identity.LookupKey.Identifier].AsString,
            document[identity.ComparisonKey.Identifier].AsString);

    private readonly record struct DocumentIdentity(string Scope, string LookupKey, string ComparisonKey);

    private static FilterDefinition<BsonDocument> Comparison(
        DocumentQueryComparison comparison,
        PhysicalQueryPlan plan,
        PhysicalQueryField queryField,
        ExecutableStorageRoute route)
    {
        if (comparison.Path == PhysicalDocumentFieldPaths.Id)
            return MongoDbPhysicalIdentityQuery.Build(comparison, plan);

        var field = queryField.Identifier;
        var projection = route.ProjectedColumns.SingleOrDefault(candidate =>
            candidate.Target == queryField.Target &&
            candidate.Column.Identifier == queryField.Identifier);
        BsonValue ToValue(string? value) => projection is null
            ? ToLogicalValue(queryField.ValueKind, value)
            : MongoDbPhysicalProjectionValues.ParseQueryValue(projection, value);
        var value = comparison.Values.Count == 0 ? BsonNull.Value : ToValue(comparison.Values[0]);
        return comparison.Operator switch
        {
            QueryComparisonOperator.Equal => Builders<BsonDocument>.Filter.Eq(field, value),
            QueryComparisonOperator.NotEqual => Builders<BsonDocument>.Filter.Ne(field, value),
            QueryComparisonOperator.In => comparison.Values.Count == 0
                ? Builders<BsonDocument>.Filter.Eq(MatchNoneField, true)
                : Builders<BsonDocument>.Filter.In(field, comparison.Values.Select(ToValue).ToArray()),
            QueryComparisonOperator.Contains => Builders<BsonDocument>.Filter.Regex(field, new BsonRegularExpression(Regex.Escape(comparison.Values[0]!), "i")),
            QueryComparisonOperator.NotContains => Builders<BsonDocument>.Filter.Not(
                Builders<BsonDocument>.Filter.Regex(field, new BsonRegularExpression(Regex.Escape(comparison.Values[0]!), "i"))),
            QueryComparisonOperator.StartsWith => Builders<BsonDocument>.Filter.Regex(field, new BsonRegularExpression("^" + Regex.Escape(comparison.Values[0]!), "i")),
            QueryComparisonOperator.GreaterThan => Builders<BsonDocument>.Filter.Gt(field, value),
            QueryComparisonOperator.GreaterThanOrEqual => Builders<BsonDocument>.Filter.Gte(field, value),
            QueryComparisonOperator.LessThan => Builders<BsonDocument>.Filter.Lt(field, value),
            QueryComparisonOperator.LessThanOrEqual => Builders<BsonDocument>.Filter.Lte(field, value),
            _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison.Operator, null)
        };
    }

    private static BsonValue ToLogicalValue(IndexValueKind kind, string? value)
    {
        if (value is null) return BsonNull.Value;
        try
        {
            return kind switch
            {
                IndexValueKind.Number => new BsonDecimal128(Decimal128.Parse(value)),
                IndexValueKind.Boolean => bool.Parse(value),
                IndexValueKind.DateTime => throw new InvalidOperationException(
                    "MongoDB exact DateTime queries require a typed projected field."),
                _ => value
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new InvalidDataException(
                $"MongoDB query value '{value}' cannot be converted to logical value kind '{kind}'.",
                exception);
        }
    }
}
