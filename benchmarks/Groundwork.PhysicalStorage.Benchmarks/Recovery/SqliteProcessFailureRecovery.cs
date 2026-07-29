using System.Diagnostics;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;

namespace Groundwork.PhysicalStorage.Benchmarks;

/// <summary>Bounded parent-side proof for SQLite process termination and recovery.</summary>
internal static class SqliteProcessFailureRecovery
{
    internal static async Task<RecoveryProofResult> RunAsync(
        PhysicalStorageForm storageForm,
        string scratchDirectory,
        RecoveryFailurePoint failurePoint,
        CancellationToken cancellationToken,
        TimeSpan? configuredRecoveryExecutionBound = null,
        string? evidenceOutputPath = null,
        Action<int>? workerStarted = null)
    {
        var bound = configuredRecoveryExecutionBound ?? TimeSpan.FromMilliseconds(RecoveryProtocol.TimeoutMilliseconds);
        if (bound <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(configuredRecoveryExecutionBound));
        var configuredRecoveryExecutionBoundMilliseconds = checked((long)Math.Ceiling(bound.TotalMilliseconds));
        var stopwatch = new Stopwatch();
        using var recoveryExecution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = recoveryExecution.Token;
        var runDirectory = Path.Combine(scratchDirectory, "recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        Process? worker = null;
        Process? recovery = null;
        try
        {
            var instance = "recovery_" + Guid.NewGuid().ToString("N")[..12];
            var databasePath = Path.Combine(runDirectory, "durable.db");
            var source = RecoveryProtocol.CaptureSourceSnapshot();
            stopwatch.Start();
            recoveryExecution.CancelAfter(bound);
            await SeedAsync(storageForm, instance, databasePath, token);

            var requestPath = Path.Combine(runDirectory, "mutation-request.json");
            var instrumentationBarrierPath = Path.Combine(runDirectory, "instrumentation-barrier.json");
            var responseReleasePath = Path.Combine(runDirectory, "response-release.json");
            var requesterAcknowledgementPath = Path.Combine(runDirectory, "requester-acknowledgement.json");
            await RecoveryProtocol.WriteAsync(requestPath, new RecoveryWorkerRequest(
                RecoveryProtocol.Version,
                source,
                storageForm,
                instance,
                databasePath,
                failurePoint,
                instrumentationBarrierPath,
                responseReleasePath,
                requesterAcknowledgementPath), token);
            worker = Start("recovery-worker", requestPath);
            workerStarted?.Invoke(worker.Id);
            using (var barrierTimeout = RemainingTimeout(token, stopwatch, bound))
                await RecoveryProtocol.WaitForFileAsync(instrumentationBarrierPath, barrierTimeout.Token);
            var barrier = await RecoveryProtocol.ReadAsync<RecoveryInstrumentationBarrier>(instrumentationBarrierPath, token);
            if (barrier.ProtocolVersion != RecoveryProtocol.Version || barrier.Source != source ||
                barrier.FailurePoint != failurePoint || barrier.WorkerProcessId != worker.Id ||
                barrier.State != (failurePoint == RecoveryFailurePoint.PreCommit
                    ? "staged-pre-commit"
                    : "committed-before-requester-acknowledgement") ||
                worker.HasExited)
            {
                throw new InvalidOperationException("Recovery worker barrier did not bind a live declared failure point.");
            }
            if (File.Exists(requesterAcknowledgementPath))
                throw new InvalidOperationException("Requester acknowledgement was observed before forced termination.");

            worker.Kill(entireProcessTree: true);
            using (var killTimeout = RemainingTimeout(token, stopwatch, bound))
                await worker.WaitForExitAsync(killTimeout.Token);
            var workerExitCode = worker.ExitCode;
            if (workerExitCode == 0)
                throw new InvalidOperationException("Recovery worker exited successfully after the forced-kill barrier.");
            if (File.Exists(requesterAcknowledgementPath))
                throw new InvalidOperationException("Requester acknowledgement was observed after forced termination.");

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
                Environment.ProcessId,
                worker.Id,
                RecoveryProtocol.KillTreeMethod,
                workerExitCode,
                workerExitCode != 0,
                RequesterAcknowledgementObserved: false,
                result.RecoveryProcessId,
                result.RecoveredBeforeRetryVersion,
                result.RecoveredBeforeRetryStateDigest,
                result.RetryOutcome,
                result.RecoveredAfterRetryVersion,
                result.RecoveredAfterRetryStateDigest,
                ConfiguredRecoveryExecutionBoundMilliseconds: configuredRecoveryExecutionBoundMilliseconds,
                RecoveryExecutionElapsedMilliseconds: elapsedMilliseconds,
                RecoveryExecutionCompletedWithinBound:
                    elapsedMilliseconds <= configuredRecoveryExecutionBoundMilliseconds,
                Promotable: false);
            RecoveryProtocol.VerifySemantics(evidence);
            var evidencePath = evidenceOutputPath is null
                ? Path.Combine(runDirectory, "recovery-evidence.json")
                : Path.GetFullPath(evidenceOutputPath);
            await RecoveryProtocol.WriteAsync(evidencePath, evidence, cancellationToken);
            var evidenceFileSha256 = BenchmarkSubprocessCoordinator.DigestFile(evidencePath);
            var retained = await RecoveryProtocol.VerifyRetainedAsync(
                evidencePath,
                evidenceFileSha256,
                cancellationToken);
            return new RecoveryProofResult(retained, evidenceFileSha256);
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
        var store = await SqliteBenchmarkTarget.InitializeRecoveryAsync(
            storageForm, instance, databasePath, cancellationToken);
        var seed = await store.SaveAsync(
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
        startInfo.ArgumentList.Add(RecoveryProtocol.BenchmarkAssemblyPath);
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(requestPath);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start recovery process.");
    }
}
