using System.Data.Common;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Provider.Relational;

namespace Groundwork.Relational.Documents;

/// <summary>
/// Shared relational document unit-of-work plumbing over either a directly owned
/// <see cref="DbTransaction"/> (with the store's connection gate and an optional schema-transition
/// lease) or a session-factory-owned <see cref="RelationalUnitOfWork"/>. Terminal completion follows
/// the canonical cleanup-failure aggregation of <see cref="DocumentUnitOfWorkCore"/>: the primary
/// failure is rethrown and transaction-dispose, lease-dispose, and gate-release failures are attached
/// to it under <see cref="RelationalCleanupFailures.DataKey"/>.
/// </summary>
internal abstract class RelationalDocumentUnitOfWorkBase : DocumentUnitOfWorkCore
{
    private readonly DbTransaction? transaction;
    private readonly RelationalUnitOfWork? ownedUnitOfWork;
    private readonly IAsyncDisposable? transitionLease;
    private readonly SemaphoreSlim? connectionGate;

    protected RelationalDocumentUnitOfWorkBase(
        DbTransaction transaction,
        SemaphoreSlim connectionGate,
        IAsyncDisposable? transitionLease = null)
    {
        this.transaction = transaction;
        this.connectionGate = connectionGate;
        this.transitionLease = transitionLease;
    }

    protected RelationalDocumentUnitOfWorkBase(RelationalUnitOfWork ownedUnitOfWork)
    {
        this.ownedUnitOfWork = ownedUnitOfWork;
    }

    protected sealed override string CleanupFailuresDataKey => RelationalCleanupFailures.DataKey;

    protected sealed override Task CommitCoreAsync(CancellationToken cancellationToken) =>
        ownedUnitOfWork is not null
            ? CompleteOwnedAsync(() => ownedUnitOfWork.CommitAsync(cancellationToken))
            : CompleteAsync(() => transaction!.CommitAsync(cancellationToken));

    protected sealed override Task AbortAsync(CancellationToken cancellationToken)
    {
        if (Completed)
            return Task.CompletedTask;
        return ownedUnitOfWork is not null
            ? CompleteOwnedAsync(() => ownedUnitOfWork.RollbackAsync(cancellationToken))
            : CompleteAsync(() => transaction!.RollbackAsync(cancellationToken));
    }

    protected sealed override ValueTask DisposeCoreAsync()
    {
        if (ownedUnitOfWork is not null)
        {
            MarkCompleted();
            return ownedUnitOfWork.DisposeAsync();
        }
        return new(CompleteAsync(() => transaction!.RollbackAsync()));
    }

    protected sealed override async ValueTask<Exception?> ReleaseResourcesAsync(Exception? primaryFailure)
    {
        primaryFailure = await CaptureCleanupFailureAsync(primaryFailure, () => transaction!.DisposeAsync());
        if (transitionLease is not null)
            primaryFailure = await CaptureCleanupFailureAsync(primaryFailure, () => transitionLease.DisposeAsync());
        return await CaptureCleanupFailureAsync(primaryFailure, () =>
        {
            connectionGate!.Release();
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>Routes an operation through the owned unit of work's executor or the direct transaction.</summary>
    protected Task<T> ExecuteAsync<T>(
        Func<DbTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        ownedUnitOfWork is not null
            ? ownedUnitOfWork.Executor.ExecuteAsync(
                (_, currentTransaction, ct) => operation(currentTransaction, ct),
                cancellationToken)
            : operation(transaction!, cancellationToken);

    private async Task CompleteOwnedAsync(Func<Task> terminalAction)
    {
        try
        {
            await terminalAction();
        }
        finally
        {
            MarkCompleted();
        }
    }
}
