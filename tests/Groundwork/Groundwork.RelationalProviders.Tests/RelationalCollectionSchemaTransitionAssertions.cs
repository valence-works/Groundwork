using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Relational.Documents;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

internal static class RelationalCollectionSchemaTransitionAssertions
{
    public static async Task SuccessfulTransitionFencesOldWritersAsync(
        (StorageManifest Manifest, PhysicalSchemaTarget Target) initial,
        (StorageManifest Manifest, PhysicalSchemaTarget Target) additive,
        Func<StorageManifest, IReadOnlyList<ExecutableStorageRoute>, RelationalPhysicalDocumentStore> createStore,
        Func<
            RelationalPhysicalDocumentStore,
            StorageManifest,
            ExecutableStorageRoute,
            IBoundedDocumentMutationStore> createMutationStore,
        Func<
            Func<PhysicalSchemaOperation, CancellationToken, Task>?,
            IPhysicalSchemaExecutor> createExecutor)
    {
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, createExecutor(null));
        var oldStore = createStore(initial.Manifest, initial.Target.Routes);
        var oldMutationStore = createMutationStore(
            oldStore,
            initial.Manifest,
            initial.Target.Routes.Single());
        await oldStore.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "preexisting",
            "1",
            """{"category":"x","permissions":["old"]}"""));

        await using var inFlightUnitOfWork = await oldStore.BeginAsync(
            DocumentCommitScope.Of("configurationDocument"));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await inFlightUnitOfWork.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "before",
            "1",
            """{"category":"pending","permissions":["before"]}"""))).Status);

        var schemaEntered = NewSignal();
        var releaseSchema = NewSignal();
        var apply = PhysicalSchemaApplication.ApplyAsync(
            additive.Target,
            createExecutor(async (operation, cancellationToken) =>
            {
                if (operation is not BackfillCanonicalJsonOperation
                    {
                        SubjectKind: CanonicalJsonBackfillSubjectKind.CollectionElements
                    })
                    return;
                schemaEntered.TrySetResult();
                await releaseSchema.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }));
        await AssertStillWaitingAsync(schemaEntered.Task);

        await inFlightUnitOfWork.CommitAsync();
        await schemaEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var duringWriter = oldStore.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "during",
            "1",
            """{"category":"x","permissions":["during"]}"""));
        var duringMutation = oldMutationStore.ExecuteAsync(new DocumentMutation(
            "configurationDocument",
            "revoke-pending",
            $"stale-{Guid.NewGuid():N}"));
        await AssertStillWaitingAsync(duringWriter);
        await AssertStillWaitingAsync(duringMutation);
        releaseSchema.TrySetResult();

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, (await apply).Outcome);
        var rejection = await Assert.ThrowsAsync<PhysicalSchemaUpgradeRequiredException>(() => duringWriter);
        Assert.Equal(PhysicalSchemaUpgradeRequiredException.DiagnosticCode, rejection.Code);
        var mutationRejection = await Assert.ThrowsAsync<PhysicalSchemaUpgradeRequiredException>(() => duringMutation);
        Assert.Equal(PhysicalSchemaUpgradeRequiredException.DiagnosticCode, mutationRejection.Code);
        await Assert.ThrowsAsync<PhysicalSchemaUpgradeRequiredException>(() => oldStore.SaveAsync(
            new SaveDocumentRequest(
                "configurationDocument",
                "after",
                "1",
                """{"category":"x","permissions":["after"]}""")));
        await Assert.ThrowsAsync<PhysicalSchemaUpgradeRequiredException>(() => oldStore.SaveAsync(
            new SaveDocumentRequest(
                "configurationDocument",
                "preexisting",
                "1",
                """{"category":"changed","permissions":["changed"]}""",
                1)));
        await Assert.ThrowsAsync<PhysicalSchemaUpgradeRequiredException>(() => oldStore.DeleteAsync(
            new DeleteDocumentRequest("configurationDocument", "preexisting", 1)));
        var unitOfWorkRejection = await Assert.ThrowsAsync<PhysicalSchemaUpgradeRequiredException>(() =>
            oldStore.BeginAsync(DocumentCommitScope.Of("configurationDocument")));
        Assert.Equal(PhysicalSchemaUpgradeRequiredException.DiagnosticCode, unitOfWorkRejection.Code);
        var postTransitionMutationRejection = await Assert.ThrowsAsync<PhysicalSchemaUpgradeRequiredException>(() =>
            oldMutationStore.ExecuteAsync(new DocumentMutation(
                "configurationDocument",
                "revoke-pending",
                $"post-transition-{Guid.NewGuid():N}")));
        Assert.Equal(
            PhysicalSchemaUpgradeRequiredException.DiagnosticCode,
            postTransitionMutationRejection.Code);

        var restarted = createStore(additive.Manifest, additive.Target.Routes);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await restarted.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "after",
            "1",
            """{"category":"x","permissions":["after"]}"""))).Status);
        Assert.Contains(
            "\"category\":\"x\"",
            (await restarted.LoadAsync("configurationDocument", "preexisting"))!.ContentJson,
            StringComparison.Ordinal);
        Assert.Null(await restarted.LoadAsync("configurationDocument", "during"));
    }

    public static async Task FailedTransitionPreservesOldWriterAdmissionAsync(
        (StorageManifest Manifest, PhysicalSchemaTarget Target) initial,
        (StorageManifest Manifest, PhysicalSchemaTarget Target) additive,
        Func<StorageManifest, IReadOnlyList<ExecutableStorageRoute>, RelationalPhysicalDocumentStore> createStore,
        Func<
            Func<PhysicalSchemaOperation, CancellationToken, Task>?,
            IPhysicalSchemaExecutor> createExecutor)
    {
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, createExecutor(null));
        var oldStore = createStore(initial.Manifest, initial.Target.Routes);
        await Assert.ThrowsAsync<InjectedTransitionException>(() =>
            PhysicalSchemaApplication.ApplyAsync(
                additive.Target,
                createExecutor((operation, _) =>
                    operation is BackfillCanonicalJsonOperation
                    {
                        SubjectKind: CanonicalJsonBackfillSubjectKind.CollectionElements
                    }
                        ? Task.FromException(new InjectedTransitionException())
                        : Task.CompletedTask)));

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await oldStore.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "after-failure",
            "1",
            """{"category":"x","permissions":["old"]}"""))).Status);
        var newStore = createStore(additive.Manifest, additive.Target.Routes);
        await Assert.ThrowsAsync<PhysicalSchemaUpgradeRequiredException>(() => newStore.SaveAsync(
            new SaveDocumentRequest(
                "configurationDocument",
                "new-before-retry",
                "1",
                """{"category":"x","permissions":["new"]}""")));
    }

    private static async Task AssertStillWaitingAsync(Task task)
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            task.WaitAsync(TimeSpan.FromMilliseconds(150)));
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class InjectedTransitionException : Exception
    {
    }
}
