using System.Data;
using System.Data.Common;
using Groundwork.Provider.Relational;
using Groundwork.Relational.PhysicalStorage;
using Xunit;

namespace Groundwork.Sqlite.Tests;

/// <summary>
/// A killed session makes the driver's own rollback fail, and C# lets an exception from disposal
/// replace the one already propagating. These pin the behaviour deterministically, because the
/// integration test that first caught it (SQL Server, terminated lock session) only loses the race
/// on a loaded machine.
/// </summary>
public sealed class RelationalTransactionScopeTests
{
    [Fact]
    public async Task Failing_rollback_does_not_replace_the_failure_that_is_already_propagating()
    {
        var connection = new FakeConnection(disposeFailure: new NullReferenceException("driver rollback"));
        var bodyFailure = new FakeDbException("session was killed");

        var thrown = await Assert.ThrowsAsync<FakeDbException>(() =>
            RelationalTransactionScope.ExecuteAsync<bool>(
                connection,
                (_, _) => throw bodyFailure,
                CancellationToken.None));

        Assert.Same(bodyFailure, thrown);
    }

    [Fact]
    public async Task Failing_rollback_is_retained_against_the_original_failure()
    {
        var disposeFailure = new NullReferenceException("driver rollback");
        var connection = new FakeConnection(disposeFailure);

        var thrown = await Assert.ThrowsAsync<FakeDbException>(() =>
            RelationalTransactionScope.ExecuteAsync<bool>(
                connection,
                (_, _) => throw new FakeDbException("session was killed"),
                CancellationToken.None));

        var retained = Assert.IsType<List<Exception>>(thrown.Data[RelationalCleanupFailures.DataKey]);
        Assert.Same(disposeFailure, Assert.Single(retained));
    }

    [Fact]
    public async Task A_successful_body_disposes_its_transaction()
    {
        var connection = new FakeConnection(disposeFailure: null);

        var result = await RelationalTransactionScope.ExecuteAsync(
            connection,
            (transaction, _) => Task.FromResult(transaction is not null),
            CancellationToken.None);

        Assert.True(result);
        Assert.True(connection.Transaction!.Disposed);
    }

    [Fact]
    public async Task A_successful_body_surfaces_its_own_disposal_failure()
    {
        // Nothing is being masked here, so the failure is the caller's to see.
        var disposeFailure = new NullReferenceException("driver rollback");
        var connection = new FakeConnection(disposeFailure);

        var thrown = await Assert.ThrowsAsync<NullReferenceException>(() =>
            RelationalTransactionScope.ExecuteAsync(
                connection,
                (_, _) => Task.FromResult(true),
                CancellationToken.None));

        Assert.Same(disposeFailure, thrown);
    }

    private sealed class FakeDbException(string message) : DbException(message);

    private sealed class FakeConnection(Exception? disposeFailure) : DbConnection
    {
        public FakeTransaction? Transaction { get; private set; }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            Transaction = new FakeTransaction(this, disposeFailure);

        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => string.Empty;
        public override string DataSource => string.Empty;
        public override string ServerVersion => string.Empty;
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() { }
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private sealed class FakeTransaction(DbConnection connection, Exception? disposeFailure) : DbTransaction
    {
        public bool Disposed { get; private set; }

        protected override DbConnection DbConnection => connection;
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        public override void Commit() { }
        public override void Rollback() { }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
            if (disposeFailure is not null)
                throw disposeFailure;
        }
    }
}
