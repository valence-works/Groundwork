using System.Data.Common;
using System.Diagnostics;
using Groundwork.Documents.Store;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

internal static class RelationalSessionPoolPressure
{
    private static readonly TimeSpan MustFireTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MustNotFireBudget = TimeSpan.FromSeconds(1);

    public static Task<IAsyncDisposable> BlockSqlServerDocumentsAsync(string connectionString) =>
        BlockDocumentsAsync(
            new Microsoft.Data.SqlClient.SqlConnection(connectionString),
            "SELECT COUNT(*) FROM groundwork_documents WITH (TABLOCKX, HOLDLOCK);");

    public static Task<IAsyncDisposable> BlockPostgreSqlDocumentsAsync(string connectionString) =>
        BlockDocumentsAsync(
            new Npgsql.NpgsqlConnection(connectionString),
            "LOCK TABLE groundwork_documents IN ACCESS EXCLUSIVE MODE;");

    public static async Task AssertTwoOperationsRunWhileThirdWaitsForProviderPoolAsync(
        IDocumentStore store,
        Task twoConnectionsOpened,
        Task thirdSessionRequested,
        Func<int> openedSessions,
        IAsyncDisposable blocker)
    {
        var first = store.LoadAsync("configurationDocument", "pool-pressure-1");
        var second = store.LoadAsync("configurationDocument", "pool-pressure-2");
        try
        {
            await twoConnectionsOpened.WaitAsync(MustFireTimeout);

            using var cancellation = new CancellationTokenSource();
            var third = store.LoadAsync("configurationDocument", "pool-pressure-3", cancellation.Token);
            await AssertWaitsForProviderPoolSessionAsync(third, thirdSessionRequested, openedSessions, sessionLimit: 2);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
        }
        finally
        {
            await blocker.DisposeAsync();
        }

        await Task.WhenAll(first, second);
    }

    /// <summary>
    /// Asserts that an operation stays queued behind a saturated provider pool. The negative
    /// assertions are only meaningful once the operation has observably requested a session, so
    /// this first waits for that signal, then watches for a generous budget in which a pool-limit
    /// regression would surface as an extra session or an early completion.
    /// </summary>
    public static async Task AssertWaitsForProviderPoolSessionAsync(
        Task operation,
        Task sessionRequested,
        Func<int> openedSessions,
        int sessionLimit)
    {
        await sessionRequested.WaitAsync(MustFireTimeout);

        var observation = Stopwatch.StartNew();
        while (observation.Elapsed < MustNotFireBudget && !operation.IsCompleted && openedSessions() <= sessionLimit)
            await Task.Delay(10);

        Assert.Equal(sessionLimit, openedSessions());
        Assert.False(operation.IsCompleted);
    }

    private static async Task<IAsyncDisposable> BlockDocumentsAsync(DbConnection connection, string commandText)
    {
        DbTransaction? transaction = null;
        try
        {
            await connection.OpenAsync();
            transaction = await connection.BeginTransactionAsync();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync();
            return new TransactionBlocker(connection, transaction);
        }
        catch
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class TransactionBlocker(DbConnection connection, DbTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.DisposeAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
