using System.Data.Common;
using Groundwork.Provider.Relational;

namespace Groundwork.Relational.PhysicalStorage;

/// <summary>
/// Runs work inside a transaction whose disposal can never replace the failure already propagating
/// out of it.
/// </summary>
/// <remarks>
/// Disposing an uncommitted <see cref="DbTransaction"/> rolls it back, and rolling back over a
/// connection the server has already killed makes the driver itself fail — SQL Server's client
/// raises <see cref="NullReferenceException"/> from inside <c>SqlInternalTransaction.Rollback</c>.
/// With a plain <c>await using</c> that failure escapes the scope and, per the language's unwinding
/// rules, replaces the exception the body threw. That matters here because the lost-ownership path
/// keys off the body's exception type: a killed session surfaces a <see cref="DbException"/> that
/// callers are told to expect as an ownership-lost <see cref="InvalidOperationException"/>, and a
/// rollback failure substituting an unrelated type turns a deterministic contract into a coin flip.
/// The cleanup failure is kept as attached data rather than discarded, matching how lock acquisition
/// already reports its own cleanup failures.
/// </remarks>
internal static class RelationalTransactionScope
{
    public static async Task<T> ExecuteAsync<T>(
        DbConnection connection,
        Func<DbTransaction, CancellationToken, Task<T>> body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(body);

        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        T result;
        try
        {
            result = await body(transaction, cancellationToken);
        }
        catch (Exception exception)
        {
            try
            {
                await transaction.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                RelationalCleanupFailures.Attach(exception, cleanupFailure);
            }
            throw;
        }

        // The body has committed by the time it returns, so a failure here is a real one with nothing
        // to mask and is left to propagate.
        await transaction.DisposeAsync();
        return result;
    }
}
