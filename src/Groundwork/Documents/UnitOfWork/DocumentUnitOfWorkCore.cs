using System.Runtime.ExceptionServices;
using Groundwork.Documents.Store;

namespace Groundwork.Documents.UnitOfWork;

/// <summary>
/// Provider-agnostic complete-once state machine shared by every <see cref="IDocumentUnitOfWork"/>
/// implementation: active/terminal guards, abort on non-success staging results, and canonical
/// cleanup-failure handling in which the primary failure is rethrown and any cleanup failures are
/// aggregated onto its <see cref="Exception.Data"/> under <see cref="CleanupFailuresDataKey"/>.
/// </summary>
/// <remarks>
/// Providers contribute only transaction primitives (<see cref="CommitCoreAsync"/>,
/// <see cref="AbortAsync"/>, <see cref="DisposeCoreAsync"/>, <see cref="ReleaseResourcesAsync"/>)
/// plus the provider-specific staging bodies passed to <see cref="StageWriteAsync"/>. No relational
/// or MongoDB types appear here; the base stays layerable from <c>Groundwork.Documents</c>.
/// </remarks>
internal abstract class DocumentUnitOfWorkCore : IDocumentUnitOfWork
{
    private bool completed;

    public abstract Task<DocumentStoreWriteResult> SaveAsync(SaveDocumentRequest request, CancellationToken cancellationToken = default);

    public abstract Task<DocumentStoreWriteResult> DeleteAsync(DeleteDocumentRequest request, CancellationToken cancellationToken = default);

    public abstract Task<DocumentEnvelope?> LoadAsync(string documentKind, string id, CancellationToken cancellationToken = default);

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await CommitCoreAsync(cancellationToken);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await AbortAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (completed)
            return;
        await DisposeCoreAsync();
    }

    /// <summary>The exception message thrown when a member is used after the unit completed.</summary>
    protected abstract string AlreadyCompletedMessage { get; }

    /// <summary>The <see cref="Exception.Data"/> key under which cleanup failures are aggregated.</summary>
    protected abstract string CleanupFailuresDataKey { get; }

    /// <summary>Commits the underlying transaction and makes the unit terminal.</summary>
    protected abstract Task CommitCoreAsync(CancellationToken cancellationToken);

    /// <summary>Rolls back the underlying transaction and makes the unit terminal. Must be a no-op on a completed unit.</summary>
    protected abstract Task AbortAsync(CancellationToken cancellationToken);

    /// <summary>Disposes a still-active unit (rollback-on-dispose semantics).</summary>
    protected abstract ValueTask DisposeCoreAsync();

    /// <summary>
    /// Releases provider resources after the terminal action ran. Implementations must not throw:
    /// failures are folded into <paramref name="primaryFailure"/> via
    /// <see cref="CaptureCleanupFailureAsync"/>/<see cref="AttachCleanupFailure"/> and the resulting
    /// primary failure is returned for the caller to rethrow.
    /// </summary>
    protected abstract ValueTask<Exception?> ReleaseResourcesAsync(Exception? primaryFailure);

    /// <summary>Runs before the abort triggered by a staged write returning a non-success status.</summary>
    protected virtual Task BeforeNonSuccessAbortAsync(CancellationToken callerCancellationToken) => Task.CompletedTask;

    /// <summary>Runs after the abort of an unconverted staged-write failure, immediately before the failure is rethrown.</summary>
    protected virtual void BeforeRethrowStagedWriteFailure(CancellationToken cancellationToken)
    {
    }

    protected bool Completed => completed;

    protected void MarkCompleted() => completed = true;

    protected void EnsureActive()
    {
        if (completed)
            throw new InvalidOperationException(AlreadyCompletedMessage);
    }

    /// <summary>
    /// Stages one save/delete. A non-success status aborts the unit (after
    /// <see cref="BeforeNonSuccessAbortAsync"/>) before the result is returned. An exception either
    /// converts to a terminal result after aborting — when <paramref name="convertFailure"/> selects a
    /// conversion for it — or aborts with cleanup-failure aggregation and rethrows the original
    /// failure with its stack preserved.
    /// </summary>
    protected async Task<DocumentStoreWriteResult> StageWriteAsync(
        Func<CancellationToken, Task<DocumentStoreWriteResult>> operation,
        DocumentStoreWriteStatus successStatus,
        CancellationToken cancellationToken,
        Func<Exception, Func<CancellationToken, Task<DocumentStoreWriteResult>>?>? convertFailure = null)
    {
        try
        {
            var result = await operation(cancellationToken);
            if (result.Status != successStatus)
            {
                await BeforeNonSuccessAbortAsync(cancellationToken);
                await AbortAsync(CancellationToken.None);
            }
            return result;
        }
        catch (Exception primaryFailure)
        {
            var conversion = convertFailure?.Invoke(primaryFailure);
            if (conversion is not null)
            {
                await AbortAsync(CancellationToken.None);
                return await conversion(cancellationToken);
            }
            try
            {
                await AbortAsync(CancellationToken.None);
            }
            catch (Exception cleanupFailure)
            {
                AttachCleanupFailure(primaryFailure, cleanupFailure);
            }
            BeforeRethrowStagedWriteFailure(cancellationToken);
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw;
        }
    }

    /// <summary>
    /// Marks the unit terminal, runs the terminal action (commit or rollback), then releases provider
    /// resources. The terminal action's failure stays primary; cleanup failures are attached to it and
    /// the primary failure is rethrown with its stack preserved.
    /// </summary>
    protected async Task CompleteAsync(Func<Task> terminalAction)
    {
        if (completed)
            return;
        completed = true;
        Exception? primaryFailure = null;
        try
        {
            await terminalAction();
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        primaryFailure = await ReleaseResourcesAsync(primaryFailure);
        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
    }

    /// <summary>
    /// Runs one cleanup step. Its failure becomes the primary failure when none exists yet and is
    /// attached to the existing primary failure otherwise; the effective primary failure is returned.
    /// </summary>
    protected async ValueTask<Exception?> CaptureCleanupFailureAsync(Exception? primaryFailure, Func<ValueTask> cleanup)
    {
        try
        {
            await cleanup();
        }
        catch (Exception cleanupFailure)
        {
            if (primaryFailure is null)
                return cleanupFailure;
            AttachCleanupFailure(primaryFailure, cleanupFailure);
        }
        return primaryFailure;
    }

    /// <summary>Aggregates a cleanup failure onto the primary failure's <see cref="Exception.Data"/>.</summary>
    protected void AttachCleanupFailure(Exception primaryFailure, Exception cleanupFailure)
    {
        ArgumentNullException.ThrowIfNull(primaryFailure);
        ArgumentNullException.ThrowIfNull(cleanupFailure);

        if (primaryFailure.Data[CleanupFailuresDataKey] is List<Exception> failures)
        {
            failures.Add(cleanupFailure);
            return;
        }

        primaryFailure.Data[CleanupFailuresDataKey] = new List<Exception> { cleanupFailure };
    }
}
