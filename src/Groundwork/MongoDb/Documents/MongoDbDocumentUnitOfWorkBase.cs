using Groundwork.Documents.UnitOfWork;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Documents;

/// <summary>
/// Shared MongoDB document unit-of-work plumbing over one <see cref="IClientSessionHandle"/> with an
/// open transaction. Aborts swallow <see cref="MongoException"/> because the operation failure or
/// non-success result that triggered them is authoritative; disposal of a still-active unit rolls
/// back and lets rollback failures surface. Terminal completion follows the canonical
/// cleanup-failure aggregation of <see cref="DocumentUnitOfWorkCore"/>: session-dispose failures are
/// attached to the primary failure instead of replacing it.
/// </summary>
internal abstract class MongoDbDocumentUnitOfWorkBase(IClientSessionHandle session) : DocumentUnitOfWorkCore
{
    protected IClientSessionHandle Session => session;

    protected sealed override string CleanupFailuresDataKey => "Groundwork.MongoDb.CleanupFailures";

    /// <summary>Commits the session's transaction (with provider-specific retry semantics).</summary>
    protected abstract Task CommitTransactionAsync(CancellationToken cancellationToken);

    protected sealed override Task CommitCoreAsync(CancellationToken cancellationToken) =>
        CompleteAsync(() => CommitTransactionAsync(cancellationToken));

    protected sealed override Task AbortAsync(CancellationToken cancellationToken) =>
        CompleteAsync(async () =>
        {
            try
            {
                if (session.IsInTransaction)
                    await session.AbortTransactionAsync(cancellationToken);
            }
            catch (MongoException)
            {
                // A failed write or non-success result already makes the unit of work terminal.
            }
        });

    protected sealed override ValueTask DisposeCoreAsync() =>
        new(CompleteAsync(async () =>
        {
            if (session.IsInTransaction)
                await session.AbortTransactionAsync();
        }));

    protected sealed override ValueTask<Exception?> ReleaseResourcesAsync(Exception? primaryFailure) =>
        CaptureCleanupFailureAsync(primaryFailure, () =>
        {
            session.Dispose();
            return ValueTask.CompletedTask;
        });
}
