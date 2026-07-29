using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.SchemaEvolution;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.PhysicalStorage;

internal static class SqlitePhysicalSchemaTransitionLock
{
    private static readonly ConcurrentDictionary<PhysicalSchemaTargetIdentity, SemaphoreSlim> ExclusiveProcessLocks = new();

    /// <summary>Acquires the exclusive lease used while inspecting or applying a physical-schema transition.</summary>
    public static async ValueTask<IAsyncDisposable> AcquireAsync(
        string connectionString,
        PhysicalSchemaTargetIdentity target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var gate = ExclusiveProcessLocks.GetOrAdd(target, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        FileStream? fileLock = null;
        try
        {
            fileLock = await AcquireExclusiveFileLockAsync(connectionString, target, cancellationToken);
            return new Lease(gate, fileLock);
        }
        catch
        {
            fileLock?.Dispose();
            gate.Release();
            throw;
        }
    }

    /// <summary>
    /// Acquires a shared steady-state lease. Runtime operations may overlap with one another, but an exclusive
    /// physical-schema transition cannot begin until every shared lease has been released.
    /// </summary>
    public static async ValueTask<IAsyncDisposable> AcquireSharedAsync(
        string connectionString,
        PhysicalSchemaTargetIdentity target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var lockPath = FileLockPath(connectionString, target);
        if (lockPath is null)
            return NullLease.Instance;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new Lease(
                    gate: null,
                    fileLock: new FileStream(
                        lockPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete,
                        1,
                        FileOptions.Asynchronous));
            }
            catch (FileNotFoundException)
            {
                await EnsureLockFileExistsAsync(lockPath, cancellationToken);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }
    }

    internal static string? FileLockPath(string connectionString, PhysicalSchemaTargetIdentity target)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (SqliteRelationalSessions.IsInMemory(builder) || string.IsNullOrWhiteSpace(builder.DataSource))
            return null;
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(target.ToString())))[..16].ToLowerInvariant();
        return $"{Path.GetFullPath(builder.DataSource)}.groundwork-{fingerprint}.schema.lock";
    }

    private static async Task<FileStream?> AcquireExclusiveFileLockAsync(
        string connectionString,
        PhysicalSchemaTargetIdentity target,
        CancellationToken cancellationToken)
    {
        var lockPath = FileLockPath(connectionString, target);
        if (lockPath is null)
            return null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }
    }

    private static async Task EnsureLockFileExistsAsync(string lockPath, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete,
                    1,
                    FileOptions.Asynchronous);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }
    }

    private sealed class Lease(SemaphoreSlim? gate, FileStream? fileLock) : IAsyncDisposable
    {
        private int disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                fileLock?.Dispose();
                gate?.Release();
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullLease : IAsyncDisposable
    {
        public static readonly NullLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
