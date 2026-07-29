using System.Diagnostics;
using System.Reflection;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;

namespace Groundwork.PhysicalStorage.Benchmarks;

/// <summary>Bounded parent-side proof for SQLite process termination and recovery.</summary>
internal static class SqliteProcessFailureRecovery
{
    internal static async Task<RecoveryEvidence> RunAsync(
        PhysicalStorageForm storageForm,
        string scratchDirectory,
        RecoveryFailurePoint failurePoint,
        CancellationToken cancellationToken,
        TimeSpan? configuredBound = null,
        string? evidenceOutputPath = null,
        Action<int>? workerStarted = null)
    {
        var bound = configuredBound ?? TimeSpan.FromMilliseconds(RecoveryProtocol.TimeoutMilliseconds);
        if (bound <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(configuredBound));
        var configuredBoundMilliseconds = checked((long)Math.Ceiling(bound.TotalMilliseconds));
        var stopwatch = Stopwatch.StartNew();
        using var wholeProof = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wholeProof.CancelAfter(bound);
        var token = wholeProof.Token;
        var runDirectory = Path.Combine(scratchDirectory, "recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        Process? worker = null;
        Process? recovery = null;
        try
        {
            var instance = "recovery_" + Guid.NewGuid().ToString("N")[..12];
            var databasePath = Path.Combine(runDirectory, "durable.db");
            var source = RecoveryProtocol.CaptureSourceSnapshot();
            await SeedAsync(storageForm, instance, databasePath, token);

            var requestPath = Path.Combine(runDirectory, "mutation-request.json");
            var barrierPath = Path.Combine(runDirectory, "mutation-barrier.json");
            await RecoveryProtocol.WriteAsync(requestPath, new RecoveryWorkerRequest(
                RecoveryProtocol.Version, source, storageForm, instance, databasePath, failurePoint, barrierPath), token);
            worker = Start("recovery-worker", requestPath);
            workerStarted?.Invoke(worker.Id);
            using (var barrierTimeout = RemainingTimeout(token, stopwatch, bound))
                await RecoveryProtocol.WaitForFileAsync(barrierPath, barrierTimeout.Token);
            var barrier = await RecoveryProtocol.ReadAsync<RecoveryBarrier>(barrierPath, token);
            if (barrier.ProtocolVersion != RecoveryProtocol.Version || barrier.Source != source ||
                barrier.FailurePoint != failurePoint || barrier.WorkerProcessId != worker.Id ||
                barrier.State != (failurePoint == RecoveryFailurePoint.PreCommit ? "staged" : "committed") ||
                worker.HasExited)
            {
                throw new InvalidOperationException("Recovery worker barrier did not bind a live declared failure point.");
            }

            worker.Kill(entireProcessTree: true);
            using (var killTimeout = RemainingTimeout(token, stopwatch, bound))
                await worker.WaitForExitAsync(killTimeout.Token);
            var workerExitCode = worker.ExitCode;
            if (workerExitCode == 0)
                throw new InvalidOperationException("Recovery worker exited successfully after the forced-kill barrier.");

            var verificationPath = Path.Combine(runDirectory, "recovery-result.json");
            var verificationRequestPath = Path.Combine(runDirectory, "recovery-request.json");
            await RecoveryProtocol.WriteAsync(verificationRequestPath, new RecoveryVerificationRequest(
                RecoveryProtocol.Version, source, storageForm, instance, databasePath, failurePoint, verificationPath), token);
            recovery = Start("recovery-verify", verificationRequestPath);
            using (var recoveryTimeout = RemainingTimeout(token, stopwatch, bound))
                await recovery.WaitForExitAsync(recoveryTimeout.Token);
            if (recovery.ExitCode != 0)
                throw new InvalidOperationException("Recovery verifier process failed.");
            var result = await RecoveryProtocol.ReadAsync<RecoveryVerificationResult>(verificationPath, token);
            if (result.ProtocolVersion != RecoveryProtocol.Version || result.Source != source ||
                result.FailurePoint != failurePoint || result.RecoveryProcessId != recovery.Id)
            {
                throw new InvalidOperationException("Recovery verifier result did not bind the declared process and source.");
            }

            var elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
            var evidence = new RecoveryEvidence(
                RecoveryProtocol.EvidenceSchemaVersion,
                source,
                BenchmarkProvider.Sqlite,
                storageForm,
                failurePoint,
                worker.Id,
                RecoveryProtocol.KillTreeMethod,
                workerExitCode,
                workerExitCode != 0,
                result.RecoveryProcessId,
                result.RecoveredVersion,
                result.RecoveredStateDigest,
                result.RetryOutcome,
                configuredBoundMilliseconds,
                elapsedMilliseconds,
                elapsedMilliseconds <= configuredBoundMilliseconds,
                Promotable: false);
            evidence = evidence with { Seal = RecoveryProtocol.Seal(evidence) };
            var evidencePath = evidenceOutputPath is null
                ? Path.Combine(runDirectory, "recovery-evidence.json")
                : Path.GetFullPath(evidenceOutputPath);
            await RecoveryProtocol.WriteAsync(evidencePath, evidence, token);
            var retained = await RecoveryProtocol.ReadAsync<RecoveryEvidence>(evidencePath, token);
            RecoveryProtocol.Verify(retained, evidence);
            return retained;
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                await TerminateIfLiveAsync(recovery, stopwatch, bound);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
            try
            {
                await TerminateIfLiveAsync(worker, stopwatch, bound);
            }
            catch (Exception exception) when (cleanupFailure is null)
            {
                cleanupFailure = exception;
            }
            if (evidenceOutputPath is not null)
                TryDeleteRunDirectory(runDirectory);
            if (cleanupFailure is not null)
                throw new TimeoutException("Recovery proof cleanup exceeded its configured bound.", cleanupFailure);
        }
    }

    private static CancellationTokenSource RemainingTimeout(
        CancellationToken cancellationToken,
        Stopwatch stopwatch,
        TimeSpan bound)
    {
        var remaining = bound - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("Recovery proof exceeded its configured bound.");
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(remaining);
        return timeout;
    }

    private static async Task TerminateIfLiveAsync(
        Process? process,
        Stopwatch stopwatch,
        TimeSpan bound)
    {
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            if (!process.HasExited)
            {
                using var cleanupTimeout = RemainingCleanupTimeout(stopwatch, bound);
                await process.WaitForExitAsync(cleanupTimeout.Token);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static CancellationTokenSource RemainingCleanupTimeout(Stopwatch stopwatch, TimeSpan bound)
    {
        var remaining = bound - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("Recovery proof cleanup exceeded its configured bound.");
        var timeout = new CancellationTokenSource();
        timeout.CancelAfter(remaining);
        return timeout;
    }

    private static void TryDeleteRunDirectory(string runDirectory)
    {
        try
        {
            Directory.Delete(runDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task SeedAsync(
        PhysicalStorageForm storageForm,
        string instance,
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var session = await SqliteBenchmarkTarget.InitializeRecoveryAsync(
            storageForm, instance, databasePath, cancellationToken);
        var seed = await session.Store.SaveAsync(
            RecoveryProtocol.Save(RecoveryProtocol.Content("open", 1), expectedVersion: 0), cancellationToken);
        if (seed.Status != DocumentStoreWriteStatus.Saved || seed.Document?.Version != 1)
            throw new InvalidOperationException("Recovery setup could not seed committed version 1 through the production store.");
    }

    private static Process Start(string command, string requestPath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = false,
            RedirectStandardOutput = false,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(requestPath);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start recovery process.");
    }
}
