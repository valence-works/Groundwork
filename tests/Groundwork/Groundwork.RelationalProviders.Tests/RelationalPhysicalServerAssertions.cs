using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;
using Groundwork.TestInfrastructure;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

internal static class RelationalPhysicalServerAssertions
{
    public static async Task LatestPerKeyFiltersAndPagesAsync(
        PhysicalStorageForm form,
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<
            Groundwork.Core.Manifests.StorageManifest,
            IReadOnlyList<ExecutableStorageRoute>,
            RelationalPhysicalDocumentStore> createStore,
        Func<
            RelationalPhysicalDocumentStore,
            Groundwork.Core.Manifests.StorageManifest,
            ExecutableStorageRoute,
            IBoundedDocumentStore> createRuntime)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var model = RelationalPhysicalStorageTestModels.Create(
            form,
            provider,
            includePriority: true,
            normalizer: normalizer,
            includeLatestPerCategory: true,
            instance: instance,
            namePolicy: context => context.FeatureDefaultLogicalName == "configuration_entities"
                ? "groundwork_latest_candidates"
                : $"gw_{instance}_{context.FeatureDefaultLogicalName}");
        await PhysicalSchemaApplication.ApplyAsync(model.Target, createExecutor());
        var store = createStore(model.Manifest, model.Target.Routes);
        await SaveAsync("alpha-hidden", "alpha", 0, false);
        await SaveAsync("alpha-low", "alpha", 1, true);
        await SaveAsync("alpha-high", "alpha", 3, true);
        await SaveAsync("beta-tie-b", "beta", 2, true);
        await SaveAsync("beta-tie-a", "beta", 2, true);
        await SaveAsync("gamma-hidden", "gamma", 1, false);
        var runtime = createRuntime(
            store,
            model.Manifest,
            model.Target.Routes.Single());
        var query = new DocumentQuery(
            "configurationDocument",
            "latest-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("visible", "true"))],
            [new DocumentQueryOrder("category"), new DocumentQueryOrder("priority")],
            skip: 1,
            take: 1,
            latestPerKeyPath: "category");

        var page = await runtime.QueryAsync(query);
        var count = await runtime.CountAsync(query.Select(BoundedQueryResultOperation.Count));
        var first = await runtime.FirstOrDefaultAsync(
            query.Page(0, 1).Select(BoundedQueryResultOperation.First));
        var explanation = await Assert.IsAssignableFrom<IPhysicalDocumentQueryExplainer>(runtime)
            .ExplainAsync(query);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal("beta-tie-a", Assert.Single(page.Documents).Id);
        Assert.Equal(2, count);
        Assert.Equal("alpha-low", first!.Id);
        Assert.Equal(
            form == PhysicalStorageForm.PhysicalEntityTable
                ?
                [
                    PhysicalDocumentQueryCommandIdentities.Count,
                    PhysicalDocumentQueryCommandIdentities.Page
                ]
                :
                [
                    PhysicalDocumentQueryCommandIdentities.LinkedIdentityCollisionCheck,
                    PhysicalDocumentQueryCommandIdentities.Count,
                    PhysicalDocumentQueryCommandIdentities.Page
                ],
            explanation.Commands.Select(command => command.Identity));
        Assert.All(explanation.Commands, command => Assert.False(string.IsNullOrWhiteSpace(command.NativePlan)));
        Assert.All(explanation.Commands, command => Assert.Equal(1, command.ProviderAppliedMaximumRows));
        var pageExplanation = explanation.Commands.Single(command =>
            command.Kind == PhysicalDocumentQueryCommandKind.Page);
        Assert.Equal(
            explanation.Plan.Order.Select(order => order.Field.Identifier),
            pageExplanation.ProviderAppliedOrder.Select(order => order.FieldIdentifier));
        Assert.All(
            pageExplanation.ProviderAppliedOrder,
            order => Assert.Equal(PhysicalSortDirection.Ascending, order.Direction));
        Assert.Equal(
            explanation.Plan.Order.Select(order => order.IsIdentityTieBreak),
            pageExplanation.ProviderAppliedOrder.Select(order => order.IsIdentityTieBreak));
        Assert.Contains(pageExplanation.ProviderAppliedOrder, order => order.IsIdentityTieBreak);

        async Task SaveAsync(string id, string category, int priority, bool visible)
        {
            var content = $$"""{"category":"{{category}}","priority":{{priority}},"visible":{{visible.ToString().ToLowerInvariant()}}}""";
            Assert.Equal(
                DocumentStoreWriteStatus.Saved,
                (await store.SaveAsync(new SaveDocumentRequest(
                    "configurationDocument",
                    id,
                    "1",
                    content))).Status);
        }
    }

    public static async Task LostOperationLockCannotPublishEvidenceAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<
            Func<PhysicalSchemaOperation, CancellationToken, Task>?,
            Func<PhysicalSchemaAppliedState, CancellationToken, Task>?,
            IPhysicalSchemaExecutor> createExecutor,
        Func<IPhysicalSchemaApplicationLock, long> readSessionId,
        Func<long, Task> terminateSession,
        Func<string, string, Task<long>> countOperationEvidence,
        Func<string, Task<bool>> tableExists)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = createExecutor(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        }, null);
        await using var staleLock = await executor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None);
        var history = await executor.ReadHistoryAsync(model.Target.Identity, staleLock, CancellationToken.None);
        var operation = PhysicalSchemaDiffPlanner.Plan(model.Target, history, DateTimeOffset.UtcNow).Operations
            .First(candidate => candidate is not RecordPhysicalSchemaAppliedStateOperation);
        var staleApplication = executor.ApplyOperationAsync(
            model.Target.Identity,
            operation,
            staleLock,
            CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await terminateSession(readSessionId(staleLock));
        release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => staleApplication);
        Assert.True(staleLock.OwnershipLost.IsCancellationRequested);
        Assert.Equal(0, await countOperationEvidence(operation.Identity, operation.Fingerprint));
        Assert.False(await tableExists(model.Target.Routes.Single().PrimaryStorage.Name.Identifier));

        var successor = createExecutor(null, null);
        await using (var successorLock = await successor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None))
        {
            var acknowledgement = await successor.ApplyOperationAsync(
                model.Target.Identity,
                operation,
                successorLock,
                CancellationToken.None);
            Assert.Equal(operation.Identity, acknowledgement.Identity);
        }
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(model.Target, createExecutor(null, null))).Outcome);
    }

    public static async Task LostStateLockCannotPublishAppliedStateAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<
            Func<PhysicalSchemaOperation, CancellationToken, Task>?,
            Func<PhysicalSchemaAppliedState, CancellationToken, Task>?,
            IPhysicalSchemaExecutor> createExecutor,
        Func<IPhysicalSchemaApplicationLock, long> readSessionId,
        Func<long, Task> terminateSession,
        Func<string, string, Task<long>> countAppliedState)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = createExecutor(null, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        await using var staleLock = await executor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None);
        var history = await executor.ReadHistoryAsync(model.Target.Identity, staleLock, CancellationToken.None);
        var plan = PhysicalSchemaDiffPlanner.Plan(model.Target, history, DateTimeOffset.UtcNow);
        var acknowledgements = new List<PhysicalSchemaOperationAcknowledgement>();
        foreach (var operation in plan.Operations.Where(candidate => candidate is not RecordPhysicalSchemaAppliedStateOperation))
        {
            acknowledgements.Add(await executor.ApplyOperationAsync(
                model.Target.Identity,
                operation,
                staleLock,
                CancellationToken.None));
        }
        var state = plan.Complete(acknowledgements, DateTimeOffset.UtcNow);
        var stalePublish = executor.RecordAppliedStateAsync(
            state,
            plan.ExpectedAppliedTargetFingerprint,
            staleLock,
            CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await terminateSession(readSessionId(staleLock));
        release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => stalePublish);
        Assert.True(staleLock.OwnershipLost.IsCancellationRequested);
        Assert.Equal(0, await countAppliedState(model.Target.ManifestIdentity.Value, model.Target.Provider.Name));

        await using (await createExecutor(null, null).AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None))
        {
        }
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(model.Target, createExecutor(null, null))).Outcome);
    }

    public static async Task NonProviderFailureAfterRealLockLossUsesStableErrorAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<Func<PhysicalSchemaAppliedState, CancellationToken, Task>?, IPhysicalSchemaExecutor> createExecutor,
        Func<IPhysicalSchemaApplicationLock, long> readSessionId,
        Func<long, Task> terminateSession,
        Func<string, string, Task<long>> countAppliedState)
    {
        const string injectedMessage = "simulated non-provider crash after relational lock loss";
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        long sessionId = 0;
        var executor = createExecutor(async (_, _) =>
        {
            await terminateSession(sessionId);
            // A killed session can surface as driver-internal exception types (SqlClient has thrown
            // NullReferenceException), so the injected failure deliberately uses a type outside any
            // provider-exception hierarchy: classification must not depend on the exception type.
            throw new NullReferenceException(injectedMessage);
        });
        await using var applicationLock = await executor.AcquireApplicationLockAsync(
            model.Target.Identity,
            CancellationToken.None);
        sessionId = readSessionId(applicationLock);
        var state = await PreparePendingStateAsync(model.Target, executor, applicationLock);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.RecordAppliedStateAsync(
            state,
            null,
            applicationLock,
            CancellationToken.None).AsTask());

        Assert.Contains("lock session", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(injectedMessage, exception.ToString(), StringComparison.Ordinal);
        Assert.True(applicationLock.OwnershipLost.IsCancellationRequested);
        Assert.Equal(0, await countAppliedState(model.Target.ManifestIdentity.Value, model.Target.Provider.Name));
    }

    public static async Task OrdinaryInvalidOperationPreservesOwnedLockAndOriginalErrorAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<Func<PhysicalSchemaAppliedState, CancellationToken, Task>?, IPhysicalSchemaExecutor> createExecutor)
    {
        const string injectedMessage = "ordinary schema validation failure";
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        var remainingFailures = 1;
        using var callerCancellation = new CancellationTokenSource();
        var executor = createExecutor((_, _) =>
        {
            if (Interlocked.Exchange(ref remainingFailures, 0) != 1)
                return Task.CompletedTask;
            callerCancellation.Cancel();
            return Task.FromException(new InvalidOperationException(injectedMessage));
        });
        await using var applicationLock = await executor.AcquireApplicationLockAsync(
            model.Target.Identity,
            CancellationToken.None);
        var state = await PreparePendingStateAsync(model.Target, executor, applicationLock);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.RecordAppliedStateAsync(
            state,
            null,
            applicationLock,
            callerCancellation.Token).AsTask());

        Assert.Equal(injectedMessage, exception.Message);
        Assert.False(applicationLock.OwnershipLost.IsCancellationRequested);
        await executor.RecordAppliedStateAsync(state, null, applicationLock, CancellationToken.None);
        Assert.False(applicationLock.OwnershipLost.IsCancellationRequested);
    }

    public static async Task LostBackfillLockCannotCommitDataOrEvidenceAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<
            Func<PhysicalSchemaOperation, CancellationToken, Task>?,
            Func<PhysicalSchemaAppliedState, CancellationToken, Task>?,
            IPhysicalSchemaExecutor> createExecutor,
        Func<Groundwork.Core.Manifests.StorageManifest, IReadOnlyList<ExecutableStorageRoute>, RelationalPhysicalDocumentStore> createStore,
        Func<IPhysicalSchemaApplicationLock, long> readSessionId,
        Func<long, Task> terminateSession,
        Func<string, string, Task<long>> countOperationEvidence,
        Func<string, string, Task<long>> countProjectedValues)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: false,
            instance: instance,
            normalizer: normalizer);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: true,
            instance: instance,
            normalizer: normalizer);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, createExecutor(null, null));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await createStore(initial.Manifest, initial.Target.Routes).SaveAsync(
            new SaveDocumentRequest(
                "configurationDocument",
                "backfill-lock-loss",
                "1",
                "{\"category\":\"tools\",\"priority\":42}",
                0))).Status);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = createExecutor(async (operation, _) =>
        {
            if (operation is not BackfillCanonicalJsonOperation)
                return;
            entered.TrySetResult();
            await release.Task;
        }, null);
        await using var staleLock = await executor.AcquireApplicationLockAsync(additive.Target.Identity, CancellationToken.None);
        var history = await executor.ReadHistoryAsync(additive.Target.Identity, staleLock, CancellationToken.None);
        var plan = PhysicalSchemaDiffPlanner.Plan(additive.Target, history, DateTimeOffset.UtcNow);
        Task<PhysicalSchemaOperationAcknowledgement>? staleBackfill = null;
        BackfillCanonicalJsonOperation? backfill = null;
        foreach (var operation in plan.Operations.Where(candidate => candidate is not RecordPhysicalSchemaAppliedStateOperation))
        {
            if (operation is BackfillCanonicalJsonOperation candidate)
            {
                backfill = candidate;
                staleBackfill = executor.ApplyOperationAsync(
                    additive.Target.Identity,
                    operation,
                    staleLock,
                    CancellationToken.None).AsTask();
                break;
            }
            await executor.ApplyOperationAsync(additive.Target.Identity, operation, staleLock, CancellationToken.None);
        }
        Assert.NotNull(backfill);
        Assert.NotNull(staleBackfill);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await terminateSession(readSessionId(staleLock));
        release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => staleBackfill);
        Assert.True(staleLock.OwnershipLost.IsCancellationRequested);
        Assert.Equal(0, await countOperationEvidence(backfill.Identity, backfill.Fingerprint));
        var route = additive.Target.Routes.Single();
        var priority = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "priority");
        var table = priority.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        Assert.Equal(0, await countProjectedValues(table, priority.Column.Identifier));

        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(additive.Target, createExecutor(null, null))).Outcome);
        Assert.Equal(1, await countProjectedValues(table, priority.Column.Identifier));
    }

    public static async Task TerminatedApplicationLockDisposalIsIdempotentAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<IPhysicalSchemaApplicationLock, long> readSessionId,
        Func<long, Task> terminateSession)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        var applicationLock = await createExecutor().AcquireApplicationLockAsync(
            model.Target.Identity,
            CancellationToken.None);

        await terminateSession(readSessionId(applicationLock));
        await Task.WhenAll(
            applicationLock.DisposeAsync().AsTask(),
            applicationLock.DisposeAsync().AsTask());
        Assert.True(applicationLock.OwnershipLost.IsCancellationRequested);

        await using var successor = await createExecutor().AcquireApplicationLockAsync(
            model.Target.Identity,
            CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    public static async Task FailedReleaseAloneDisposesQuietlyAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<RelationalLockFailureHarness> createHarness)
    {
        var model = LockFailureModel(provider, normalizer);
        var harness = createHarness();
        var executor = harness.CreateExecutor(false);
        var applicationLock = await executor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None);

        // The lock is session-scoped, so closing the connection ends the session and the server drops
        // the lock even though the explicit release failed. Nothing leaked, so nothing throws.
        harness.Switches.FailReleases = true;
        await applicationLock.DisposeAsync();

        Assert.True(applicationLock.OwnershipLost.IsCancellationRequested);
        await applicationLock.DisposeAsync();

        harness.Switches.FailReleases = false;
        await using var successor = await executor.AcquireApplicationLockAsync(
            model.Target.Identity,
            CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    public static async Task FailedReleaseAndSessionCloseReportThePossibleLeakAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<RelationalLockFailureHarness> createHarness)
    {
        var model = LockFailureModel(provider, normalizer);
        var harness = createHarness();
        var executor = harness.CreateExecutor(true);
        var applicationLock = await executor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None);

        harness.Switches.FailReleases = true;
        var exception = await Assert.ThrowsAsync<AggregateException>(() => applicationLock.DisposeAsync().AsTask());

        Assert.Contains(
            exception.InnerExceptions,
            inner => inner.Message == RelationalLockFailureSwitches.ReleaseFailureMessage);
        Assert.Contains(
            exception.InnerExceptions,
            inner => inner.Message == RelationalLockFailureSwitches.SessionCloseFailureMessage);
        Assert.True(applicationLock.OwnershipLost.IsCancellationRequested);
    }

    public static async Task DisposalReportCarriesTheHeartbeatProbeFailureAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<RelationalLockFailureHarness> createHarness)
    {
        var model = LockFailureModel(provider, normalizer);
        var harness = createHarness();
        var executor = harness.CreateExecutor(true);
        var applicationLock = await executor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None);
        // Nothing else may have forfeited the lease yet, or the wait below would prove nothing.
        Assert.False(applicationLock.OwnershipLost.IsCancellationRequested);

        // The heartbeat tolerates a bounded run of failed probes, so waiting for it to forfeit the
        // lease is what proves it recorded the probe failure this assertion is about.
        harness.Switches.FailVerification = true;
        await WaitForOwnershipLossAsync(applicationLock);
        harness.Switches.FailReleases = true;

        var exception = await Assert.ThrowsAsync<AggregateException>(() => applicationLock.DisposeAsync().AsTask());

        Assert.Contains(
            exception.InnerExceptions,
            inner => inner.Message == RelationalLockFailureSwitches.VerificationFailureMessage);
    }

    public static async Task ThrowingOwnershipSubscriberCannotBreakTeardownAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<RelationalLockFailureHarness> createHarness)
    {
        var model = LockFailureModel(provider, normalizer);
        var harness = createHarness();
        var executor = harness.CreateExecutor(false);
        var applicationLock = await executor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None);
        applicationLock.OwnershipLost.Register(
            () => throw new InvalidOperationException("Subscriber cancellation callback failure."));

        // Failing the release makes teardown forfeit the lease, which runs the callback above inline.
        // Cancel surfaces that as an AggregateException, which must not escape disposal.
        harness.Switches.FailReleases = true;
        await applicationLock.DisposeAsync();

        Assert.True(applicationLock.OwnershipLost.IsCancellationRequested);
        harness.Switches.FailReleases = false;
        await using var successor = await executor.AcquireApplicationLockAsync(
            model.Target.Identity,
            CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static (Groundwork.Core.Manifests.StorageManifest Manifest, PhysicalSchemaTarget Target) LockFailureModel(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer) =>
        RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: true,
            instance: Guid.NewGuid().ToString("N")[..8],
            normalizer: normalizer);

    private static async Task WaitForOwnershipLossAsync(IPhysicalSchemaApplicationLock applicationLock)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!applicationLock.OwnershipLost.IsCancellationRequested)
        {
            Assert.True(
                DateTimeOffset.UtcNow < deadline,
                "The heartbeat never forfeited the lease after its probes started failing.");
            await Task.Delay(25);
        }
    }

    public static async Task ConcurrentMaterializationAndAcknowledgementLossAreRestartSafeAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor)
    {
        var concurrent = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        var outcomes = await Task.WhenAll(
            PhysicalSchemaApplication.ApplyAsync(concurrent.Target, createExecutor()),
            PhysicalSchemaApplication.ApplyAsync(concurrent.Target, createExecutor()));
        Assert.Equal(1, outcomes.Count(result => result.Outcome == PhysicalSchemaApplicationOutcome.Applied));
        Assert.Equal(1, outcomes.Count(result => result.Outcome == PhysicalSchemaApplicationOutcome.NoChanges));

        var lost = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        await Assert.ThrowsAsync<SimulatedAcknowledgementLossException>(() =>
            PhysicalSchemaApplication.ApplyAsync(lost.Target, new AcknowledgementLosingExecutor(createExecutor())));
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(lost.Target, createExecutor())).Outcome);
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.NoChanges,
            (await PhysicalSchemaApplication.ApplyAsync(lost.Target, createExecutor())).Outcome);
    }

    public static async Task UnpublishedBackfillAcknowledgementLossReplaysInterleavedWritesAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<Groundwork.Core.Manifests.StorageManifest, IReadOnlyList<ExecutableStorageRoute>, RelationalPhysicalDocumentStore> createStore,
        string handlerPrefix)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: false,
            instance: instance,
            normalizer: normalizer);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: true,
            instance: instance,
            normalizer: normalizer);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, createExecutor());
        var oldRouteStore = createStore(initial.Manifest, initial.Target.Routes);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await oldRouteStore.SaveAsync(
            Save("before-loss", 41))).Status);

        var acknowledgementLosing = new BackfillAcknowledgementLosingExecutor(createExecutor());
        await Assert.ThrowsAsync<SimulatedBackfillAcknowledgementLossException>(() =>
            PhysicalSchemaApplication.ApplyAsync(
                additive.Target,
                acknowledgementLosing));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await oldRouteStore.SaveAsync(
            Save("between-attempts", 42))).Status);

        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(additive.Target, createExecutor())).Outcome);
        var currentStore = createStore(additive.Manifest, additive.Target.Routes);
        var route = additive.Target.Routes.Single();
        var queries = RelationalPhysicalQueryRuntime.Create(
            currentStore,
            additive.Manifest,
            route,
            provider,
            handlerPrefix);
        Assert.Equal(1, await CountPriorityAsync(queries, "41"));
        Assert.Equal(1, await CountPriorityAsync(queries, "42"));
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.NoChanges,
            (await PhysicalSchemaApplication.ApplyAsync(additive.Target, createExecutor())).Outcome);

        var rejection = await Assert.ThrowsAsync<PhysicalSchemaUpgradeRequiredException>(() =>
            oldRouteStore.SaveAsync(Save("after-publication", 43)));
        Assert.Equal(PhysicalSchemaUpgradeRequiredException.DiagnosticCode, rejection.Code);
        var publishedExecutor = createExecutor();
        await using (var applicationLock = await publishedExecutor.AcquireApplicationLockAsync(
                         additive.Target.Identity,
                         CancellationToken.None))
        {
            var acknowledgement = await publishedExecutor.ApplyOperationAsync(
                additive.Target.Identity,
                acknowledgementLosing.Backfill!,
                applicationLock,
                CancellationToken.None);
            Assert.Equal(acknowledgementLosing.Acknowledgement, acknowledgement);
        }
        Assert.Equal(0, await CountPriorityAsync(queries, "43"));

        static SaveDocumentRequest Save(string id, int priority) =>
            new("configurationDocument", id, "1", $"{{\"category\":\"tools\",\"priority\":{priority}}}", 0);

        static Task<long> CountPriorityAsync(IBoundedDocumentStore queries, string priority) =>
            queries.CountAsync(new DocumentQuery(
                "configurationDocument",
                "find-by-category-priority",
                [
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools")),
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("priority", priority))
                ],
                resultOperation: BoundedQueryResultOperation.Count));
    }

    public static async Task ApplicationLockDisposalIsHeartbeatRaceSafeAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var applicationLock = await createExecutor().AcquireApplicationLockAsync(
                model.Target.Identity,
                CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(90));
            await Task.WhenAll(
                applicationLock.DisposeAsync().AsTask(),
                applicationLock.DisposeAsync().AsTask());

            var successor = await createExecutor().AcquireApplicationLockAsync(
                model.Target.Identity,
                CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await successor.DisposeAsync();
        }
    }

    public static async Task TransientHeartbeatVerificationFailuresPreserveOwnershipAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<Func<CancellationToken, Task>, IPhysicalSchemaExecutor> createExecutor)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        var verifications = 0;
        var executor = createExecutor(_ =>
            Interlocked.Increment(ref verifications) is 2 or 3
                ? Task.FromException(new InvalidOperationException("simulated transient heartbeat verification failure"))
                : Task.CompletedTask);
        await using var applicationLock = await executor.AcquireApplicationLockAsync(
            model.Target.Identity,
            CancellationToken.None);

        await WaitUntilAsync(() => Volatile.Read(ref verifications) >= 5);

        Assert.False(applicationLock.OwnershipLost.IsCancellationRequested);
        await executor.ReadHistoryAsync(model.Target.Identity, applicationLock, CancellationToken.None);
        Assert.False(applicationLock.OwnershipLost.IsCancellationRequested);
    }

    public static async Task PersistentHeartbeatVerificationFailureMarksOwnershipLostAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<Func<CancellationToken, Task>, IPhysicalSchemaExecutor> createExecutor)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        var verifications = 0;
        var executor = createExecutor(_ =>
        {
            Interlocked.Increment(ref verifications);
            return Task.FromException(new InvalidOperationException("simulated persistent heartbeat verification failure"));
        });
        await using var applicationLock = await executor.AcquireApplicationLockAsync(
            model.Target.Identity,
            CancellationToken.None);

        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = applicationLock.OwnershipLost.Register(() => lost.TrySetResult());
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Ownership is only forfeited after the bounded run of retries, never on the first blip.
        Assert.True(Volatile.Read(ref verifications) >= 3);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400 && !condition(); attempt++)
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        Assert.True(condition(), "Timed out waiting for the heartbeat to reach the expected verification count.");
    }

    public static async Task TypedProjectionLiveAndBackfillValuesRemainEquivalentAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<Groundwork.Core.Manifests.StorageManifest, IReadOnlyList<ExecutableStorageRoute>, RelationalPhysicalDocumentStore> createStore,
        string handlerPrefix,
        PortablePhysicalType type,
        string backfilledJsonValue,
        string liveJsonValue,
        string queryValue,
        int? precision = null,
        int? scale = null)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: false,
            instance: instance,
            normalizer: normalizer);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: true,
            priorityType: type,
            priorityPrecision: precision,
            priorityScale: scale,
            instance: instance,
            normalizer: normalizer);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, createExecutor());
        var initialStore = createStore(initial.Manifest, initial.Target.Routes);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await initialStore.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "backfilled",
            "1",
            $"{{\"category\":\"tools\",\"priority\":{backfilledJsonValue}}}",
            0))).Status);

        await PhysicalSchemaApplication.ApplyAsync(additive.Target, createExecutor());
        var additiveStore = createStore(additive.Manifest, additive.Target.Routes);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await additiveStore.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "live",
            "1",
            $"{{\"category\":\"tools\",\"priority\":{liveJsonValue}}}",
            0))).Status);
        var route = additive.Target.Routes.Single();
        var queries = RelationalPhysicalQueryRuntime.Create(
            additiveStore,
            additive.Manifest,
            route,
            additive.Target.Provider,
            handlerPrefix);
        var count = await queries.CountAsync(new DocumentQuery(
            "configurationDocument",
            "find-by-category-priority",
            [
                DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools")),
                DocumentQueryClause.Of(DocumentQueryComparison.Equal("priority", queryValue))
            ],
            resultOperation: BoundedQueryResultOperation.Count));
        Assert.Equal(2, count);
    }

    public static async Task NullableUniqueProjectionUsesPortableNullDistinctSemanticsAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<Groundwork.Core.Manifests.StorageManifest, IReadOnlyList<ExecutableStorageRoute>, RelationalPhysicalDocumentStore> createStore)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: false,
            normalizer: normalizer,
            categoryUnique: true,
            categoryNullable: true,
            // Null-distinct uniqueness is exactly what Excluded means: the constraint applies only to
            // rows that have a value, so any number of rows may have none. It used to fall out of a
            // SQL Server implementation detail; now it is declared, and portable because of that.
            categoryMissingValues: MissingValueBehavior.Excluded);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, createExecutor());
        var store = createStore(model.Manifest, model.Target.Routes);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(
            new SaveDocumentRequest("configurationDocument", "missing-a", "1", "{}", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(
            new SaveDocumentRequest("configurationDocument", "missing-b", "1", "{}", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(
            new SaveDocumentRequest("configurationDocument", "present-a", "1", "{\"category\":\"same\"}", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, (await store.SaveAsync(
            new SaveDocumentRequest("configurationDocument", "present-b", "1", "{\"category\":\"same\"}", 0))).Status);
    }

    /// <summary>
    /// Widening an applied index to keep rows with no value is additive, but row exclusion is a filtered
    /// index, so creating it cannot reach the change: the object already exists carrying the predicate.
    /// The plan has to rebuild it, and the filter has to be gone from the catalog afterwards.
    /// </summary>
    public static async Task WideningANullExcludingIndexRebuildsItWithoutItsFilterAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<string, string, Task<string?>> readIndexFilter)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: false,
            normalizer: normalizer,
            categoryNullable: true,
            categoryMissingValues: MissingValueBehavior.Excluded,
            instance: instance);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, createExecutor());
        var route = model.Target.Routes.Single();
        var index = route.Indexes.Single(candidate => candidate.Identity == "by-category");
        var table = route.PrimaryStorage.Name.Identifier;
        Assert.NotNull(await readIndexFilter(table, index.Name.Identifier));

        var widened = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: false,
            normalizer: normalizer,
            categoryNullable: true,
            categoryMissingValues: MissingValueBehavior.IncludedAsNull,
            instance: instance);
        var result = await PhysicalSchemaApplication.ApplyAsync(widened.Target, createExecutor());

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome);
        Assert.Single(result.Plan.Operations.OfType<RebuildPhysicalIndexOperation>());
        Assert.Null(await readIndexFilter(table, index.Name.Identifier));
        // The widened state is the durable one, so a replay reconciles rather than rebuilding again.
        var restart = await PhysicalSchemaApplication.ApplyAsync(widened.Target, createExecutor());
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, restart.Outcome);
    }

    private static async Task<PhysicalSchemaAppliedState> PreparePendingStateAsync(
        PhysicalSchemaTarget target,
        IPhysicalSchemaExecutor executor,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        var history = await executor.ReadHistoryAsync(target.Identity, applicationLock, CancellationToken.None);
        var plan = PhysicalSchemaDiffPlanner.Plan(target, history, DateTimeOffset.UtcNow);
        var acknowledgements = new List<PhysicalSchemaOperationAcknowledgement>();
        foreach (var operation in plan.Operations.Where(candidate => candidate is not RecordPhysicalSchemaAppliedStateOperation))
        {
            acknowledgements.Add(await executor.ApplyOperationAsync(
                target.Identity,
                operation,
                applicationLock,
                CancellationToken.None));
        }
        return plan.Complete(acknowledgements, DateTimeOffset.UtcNow);
    }

    private sealed class AcknowledgementLosingExecutor(IPhysicalSchemaExecutor inner) : IPhysicalSchemaExecutor
    {
        private int lost;

        public ValueTask<IPhysicalSchemaApplicationLock> AcquireApplicationLockAsync(
            PhysicalSchemaTargetIdentity target,
            CancellationToken cancellationToken) =>
            inner.AcquireApplicationLockAsync(target, cancellationToken);

        public ValueTask<PhysicalSchemaHistoryState> ReadHistoryAsync(
            PhysicalSchemaTargetIdentity target,
            IPhysicalSchemaApplicationLock applicationLock,
            CancellationToken cancellationToken) =>
            inner.ReadHistoryAsync(target, applicationLock, cancellationToken);

        public async ValueTask<PhysicalSchemaOperationAcknowledgement> ApplyOperationAsync(
            PhysicalSchemaTargetIdentity target,
            PhysicalSchemaOperation operation,
            IPhysicalSchemaApplicationLock applicationLock,
            CancellationToken cancellationToken)
        {
            var acknowledgement = await inner.ApplyOperationAsync(target, operation, applicationLock, cancellationToken);
            if (Interlocked.Exchange(ref lost, 1) == 0)
                throw new SimulatedAcknowledgementLossException();
            return acknowledgement;
        }

        public ValueTask RecordAppliedStateAsync(
            PhysicalSchemaAppliedState state,
            string? expectedAppliedTargetFingerprint,
            IPhysicalSchemaApplicationLock applicationLock,
            CancellationToken cancellationToken) =>
            inner.RecordAppliedStateAsync(state, expectedAppliedTargetFingerprint, applicationLock, cancellationToken);
    }

    private sealed class SimulatedAcknowledgementLossException : Exception;
}

/// <summary>Failure injection shared by the provider lock-disposal conformance tests.</summary>
internal sealed class RelationalLockFailureSwitches
{
    public const string ReleaseFailureMessage = "Simulated application-lock release failure.";
    public const string VerificationFailureMessage = "Simulated application-lock verification failure.";
    public const string SessionCloseFailureMessage = "Simulated lock-session close failure.";

    public bool FailReleases { get; set; }
    public bool FailVerification { get; set; }
}

/// <summary>
/// One provider's failure-injecting executor. <c>CreateExecutor</c> takes whether the lock session
/// should also fail to close, which is what separates a released lock from a possibly leaked one.
/// </summary>
internal sealed record RelationalLockFailureHarness(
    RelationalLockFailureSwitches Switches,
    Func<bool, IPhysicalSchemaExecutor> CreateExecutor);

/// <summary>
/// Reports a failing session close while still really closing the inner connection, so a simulated
/// leak cannot strand a server session holding the lock for the rest of the run.
/// </summary>
internal sealed class DisposeFailingConnection : DbConnection
{
    private readonly DbConnection inner;

    public DisposeFailingConnection(DbConnection inner)
    {
        this.inner = inner;
        inner.StateChange += (_, args) => OnStateChange(args);
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => inner.ConnectionString;
        set => inner.ConnectionString = value;
    }

    public override string Database => inner.Database;
    public override string DataSource => inner.DataSource;
    public override string ServerVersion => inner.ServerVersion;
    public override ConnectionState State => inner.State;

    public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
    public override void Close() => inner.Close();
    public override void Open() => inner.Open();
    public override Task OpenAsync(CancellationToken cancellationToken) => inner.OpenAsync(cancellationToken);
    protected override DbCommand CreateDbCommand() => inner.CreateCommand();
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        inner.BeginTransaction(isolationLevel);

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        throw new InvalidOperationException(RelationalLockFailureSwitches.SessionCloseFailureMessage);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
