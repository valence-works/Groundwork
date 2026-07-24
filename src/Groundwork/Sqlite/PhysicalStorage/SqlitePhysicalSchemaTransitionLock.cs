using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.SchemaEvolution;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.PhysicalStorage;

internal static class SqlitePhysicalSchemaTransitionLock
{
    private static readonly ConcurrentDictionary<PhysicalSchemaTargetIdentity, SemaphoreSlim> ProcessLocks = new();

    public static async ValueTask<IAsyncDisposable> AcquireAsync(
        string connectionString,
        PhysicalSchemaTargetIdentity target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var gate = ProcessLocks.GetOrAdd(target, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        FileStream? fileLock = null;
        try
        {
            fileLock = await AcquireFileLockAsync(connectionString, target, cancellationToken);
            return new Lease(gate, fileLock);
        }
        catch
        {
            fileLock?.Dispose();
            gate.Release();
            throw;
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

    private static async Task<FileStream?> AcquireFileLockAsync(
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

    private sealed class Lease(SemaphoreSlim gate, FileStream? fileLock) : IAsyncDisposable
    {
        private int disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                fileLock?.Dispose();
                gate.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
