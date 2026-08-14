using System.Data.Common;
using System.Diagnostics;
using Groundwork.Documents.Store;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

internal static class RelationalSessionPoolPressure
{
    private static readonly TimeSpan MustFireTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MustNotFireBudget = TimeSpan.FromSeconds(1);

    public static Task<IAsyncDisposable> BlockSqlServerDocumentsAsync(string connectionString, string table) =>
        BlockDocumentsAsync(
            new Microsoft.Data.SqlClient.SqlConnection(connectionString),
            $"SELECT COUNT(*) FROM [{table}] WITH (TABLOCKX, HOLDLOCK);");

    public static Task<IAsyncDisposable> BlockPostgreSqlDocumentsAsync(string connectionString, string table) =>
        BlockDocumentsAsync(
            new Npgsql.NpgsqlConnection(connectionString),
            $"LOCK TABLE \"{table}\" IN ACCESS EXCLUSIVE MODE;");

    public static async Task AssertOperationsRunWhileTheNextWaitsForTheProviderPoolAsync(
        IDocumentStore store,
        Task poolSaturated,
        Task queuedSessionRequested,
        Func<int> openedSessions,
        int sessionLimit,
        IAsyncDisposable blocker)
    {
        var saturating = Enumerable.Range(1, sessionLimit)
            .Select(index => store.LoadAsync("configurationDocument", $"pool-pressure-{index}"))
            .ToArray();
        try
        {
            await poolSaturated.WaitAsync(MustFireTimeout);

            using var cancellation = new CancellationTokenSource();
            var queued = store.LoadAsync(
                "configurationDocument",
                $"pool-pressure-{sessionLimit + 1}",
                cancellation.Token);
            await AssertWaitsForProviderPoolSessionAsync(queued, queuedSessionRequested, openedSessions, sessionLimit);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        }
        finally
        {
            await blocker.DisposeAsync();
        }

        await Task.WhenAll(saturating);
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
