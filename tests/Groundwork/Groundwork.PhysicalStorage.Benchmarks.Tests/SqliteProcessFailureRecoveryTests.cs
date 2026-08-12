using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Groundwork.Core.PhysicalStorage;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class SqliteProcessFailureRecoveryTests : IAsyncDisposable
{
    /// <summary>
    /// Bound for the proof that aborts through caller cancellation. It is never meant to elapse, so it is far
    /// wider than any plausible seed-and-spawn stall on a shared runner.
    /// </summary>
    private static readonly TimeSpan CancellationAbortBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Bound for the proof that aborts by exhausting the deadline, so that proof costs roughly this much wall
    /// clock. The recovery execution bound also covers seeding and process spawn, which is why the proof warms
    /// that setup first rather than betting a whole benchmark run on how fast a cold runner gets there.
    /// </summary>
    private static readonly TimeSpan DeadlineExhaustionBound = TimeSpan.FromSeconds(5);

    /// <summary>The grace the harness itself allows an aborted child to exit within.</summary>
    private static readonly TimeSpan CleanupGrace = TimeSpan.FromMilliseconds(RecoveryProtocol.CleanupTimeoutMilliseconds);

    /// <summary>Slack for process scheduling on a contended CI runner, applied on top of a configured bound.</summary>
    private static readonly TimeSpan SchedulingTolerance = TimeSpan.FromSeconds(2);

    private readonly string scratch = Path.Combine(Path.GetTempPath(), $"groundwork-recovery-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(0, 1, "saved")]
    [InlineData(1, 2, "concurrencyConflict")]
    public async Task ProcessFailureRecovery_proves_the_declared_durable_outcome(
        int failurePointValue,
        long expectedBeforeRetryVersion,
        string expectedRetryOutcome)
    {
        var failurePoint = (RecoveryFailurePoint)failurePointValue;
        var retained = await RunProofAsync(
            PhysicalStorageForm.SharedDocuments,
            failurePoint,
            $"outcome-{failurePointValue}.json");
        var (result, output) = retained;
        var evidence = result.Evidence;

        Assert.Equal(BenchmarkSubprocessCoordinator.DigestFile(output), result.EvidenceFileSha256);
        Assert.Equal(RecoveryProtocol.KillTreeMethod, evidence.WorkerTerminationMethod);
        Assert.True(evidence.WorkerTerminated);
        Assert.False(evidence.RequesterAcknowledgementObserved);
        Assert.NotEqual(0, evidence.WorkerExitCode);
        AssertDistinct(evidence.CoordinatorProcessId, evidence.WorkerProcessId, evidence.RecoveryProcessId);
        Assert.True(evidence.RecoveryExecutionCompletedWithinBound);
        Assert.InRange(
            evidence.RecoveryExecutionElapsedMilliseconds,
            0,
            evidence.ConfiguredRecoveryExecutionBoundMilliseconds);
        Assert.Equal(expectedBeforeRetryVersion, evidence.RecoveredBeforeRetryVersion);
        Assert.Equal(2, evidence.RecoveredAfterRetryVersion);
        Assert.Equal(expectedRetryOutcome, evidence.RetryOutcome);
        Assert.Equal(
            RecoveryProtocol.ExpectedStateDigest(failurePoint),
            evidence.RecoveredBeforeRetryStateDigest);
        Assert.Equal(
            RecoveryProtocol.ExpectedStateDigest(RecoveryFailurePoint.CommittedBeforeAcknowledgement),
            evidence.RecoveredAfterRetryStateDigest);
        Assert.False(evidence.Promotable);
    }

    [Fact]
    public async Task Retained_evidence_digest_rejects_semantic_and_source_tampering()
    {
        var (result, output) = await RunProofAsync(
            PhysicalStorageForm.DedicatedDocumentTable,
            RecoveryFailurePoint.CommittedBeforeAcknowledgement,
            Path.Combine("tamper", "recovery-evidence.json"));
        var evidence = result.Evidence;

        await AssertAnchoredRejectedAsync(output, result.EvidenceFileSha256, evidence with { FailurePoint = RecoveryFailurePoint.PreCommit });
        await AssertAnchoredRejectedAsync(output, result.EvidenceFileSha256, evidence with { StorageForm = PhysicalStorageForm.SharedDocuments });
        await AssertAnchoredRejectedAsync(output, result.EvidenceFileSha256, evidence with { CoordinatorProcessId = evidence.CoordinatorProcessId + 1 });
        await AssertAnchoredRejectedAsync(output, result.EvidenceFileSha256, evidence with { WorkerProcessId = evidence.WorkerProcessId + 1 });
        await AssertAnchoredRejectedAsync(output, result.EvidenceFileSha256, evidence with { RecoveryProcessId = evidence.RecoveryProcessId + 1 });
        await AssertAnchoredRejectedAsync(output, result.EvidenceFileSha256, evidence with { WorkerExitCode = 0 });
        await AssertAnchoredRejectedAsync(output, result.EvidenceFileSha256, evidence with { WorkerTerminated = false });
        await AssertAnchoredRejectedAsync(output, result.EvidenceFileSha256, evidence with { RequesterAcknowledgementObserved = true });
        await AssertAnchoredRejectedAsync(output, result.EvidenceFileSha256, evidence with
        {
            ConfiguredRecoveryExecutionBoundMilliseconds = evidence.ConfiguredRecoveryExecutionBoundMilliseconds + 1
        });
        await AssertAnchoredRejectedAsync(output, result.EvidenceFileSha256, evidence with
        {
            Source = evidence.Source with { GitCommit = new string('0', 40) }
        });
        await AssertAnchoredRejectedAsync(output, result.EvidenceFileSha256, evidence with
        {
            Source = evidence.Source with { GroundworkAssemblyClosureSha256 = new string('0', 64) }
        });
    }

    [Fact]
    public async Task Recovery_protocol_rejects_an_incomplete_barrier_within_its_bound()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecoveryProtocol.WaitForFileAsync(
            Path.Combine(scratch, "never-created.json"), timeout.Token));
    }

    [Fact]
    public void Recovery_proof_command_rejects_incomplete_and_unsafe_arguments()
    {
        Assert.Throws<ArgumentException>(() => RecoveryProofCommandLine.Parse(["recovery-proof"]));
        Assert.Throws<ArgumentException>(() => RecoveryProofCommandLine.Parse(
            ["recovery-proof", "--form", "invalid", "--failure-point", "pre-commit", "--output", "evidence.json"]));
        Assert.Throws<ArgumentException>(() => RecoveryProofCommandLine.Parse(
            ["recovery-proof", "--form", "shared", "--failure-point", "pre-commit", "--output", "evidence.json", "--timeout-ms", "0"]));

        var command = RecoveryProofCommandLine.Parse(
            ["recovery-proof", "--form", "entity", "--failure-point", "committed-before-ack", "--output", "evidence.json"]);
        Assert.Equal(PhysicalStorageForm.PhysicalEntityTable, command.StorageForm);
        Assert.Equal(RecoveryFailurePoint.CommittedBeforeAcknowledgement, command.FailurePoint);
    }

    [Fact]
    public async Task Recovery_proof_cli_retains_sanitized_evidence_and_emits_the_external_digest()
    {
        await WarmRecoveryStartupAsync();
        var output = Path.Combine(scratch, "retained", "recovery-evidence.json");
        var exitCode = await Program.Main(
        [
            "recovery-proof", "--form", "shared", "--failure-point", "pre-commit",
            "--output", output, "--timeout-ms", "15000"
        ]);

        Assert.Equal(0, exitCode);
        var retained = await File.ReadAllTextAsync(output);
        Assert.DoesNotContain("databasePath", retained, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data source", retained, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("groundworkAssemblyClosureSha256", retained, StringComparison.Ordinal);
        Assert.Contains("requesterAcknowledgementObserved", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("evidenceFileSha256", retained, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_children_reject_a_source_snapshot_that_they_cannot_recompute()
    {
        var source = RecoveryProtocol.CaptureSourceSnapshot() with
        {
            GroundworkAssemblyClosureSha256 = new string('0', 64)
        };
        var mutation = new RecoveryWorkerRequest(
            RecoveryProtocol.Version,
            source,
            PhysicalStorageForm.SharedDocuments,
            "source-check",
            Path.Combine(scratch, "not-opened.db"),
            RecoveryFailurePoint.PreCommit,
            Path.Combine(scratch, "instrumentation.json"),
            Path.Combine(scratch, "release.json"),
            Path.Combine(scratch, "ack.json"));
        var verification = new RecoveryVerificationRequest(
            RecoveryProtocol.Version,
            source,
            PhysicalStorageForm.SharedDocuments,
            "source-check",
            Path.Combine(scratch, "not-opened.db"),
            RecoveryFailurePoint.PreCommit,
            Path.Combine(scratch, "result.json"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteRecoveryWorker.RunMutationAsync(mutation, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteRecoveryWorker.VerifyAsync(verification, CancellationToken.None));
    }

    [Theory]
    [InlineData("unavailable", 64, 1)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 64, 1)]
    [InlineData("not-hex-not-hex-not-hex-not-hex-not-hex!!", 64, 1)]
    [InlineData("0000000000000000000000000000000000000000", 63, 1)]
    [InlineData("0000000000000000000000000000000000000000", 64, 0)]
    public void Source_validation_rejects_unavailable_or_invalid_identity(string commit, int treeLength, int closureCount)
    {
        var source = new RecoverySourceSnapshot(
            commit,
            GitDirty: false,
            new string('0', treeLength),
            new string('0', 64),
            closureCount);

        Assert.Throws<InvalidOperationException>(() => RecoveryProtocol.ValidateSourceSnapshot(source));
    }

    [Fact]
    public async Task Recovery_open_existing_rejects_a_missing_database_without_creating_it()
    {
        var databasePath = Path.Combine(scratch, "missing", "durable.db");
        await Assert.ThrowsAsync<FileNotFoundException>(() => SqliteBenchmarkTarget.OpenExistingRecoveryAsync(
            PhysicalStorageForm.SharedDocuments, "missing_database", databasePath, CancellationToken.None));
        Assert.False(File.Exists(databasePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Inspect_only_recovery_open_rejects_empty_or_corrupt_files_without_repair(int kind)
    {
        var databasePath = Path.Combine(scratch, $"invalid-{kind}.db");
        Directory.CreateDirectory(scratch);
        var original = kind == 0 ? Array.Empty<byte>() : "not-a-sqlite-database"u8.ToArray();
        await File.WriteAllBytesAsync(databasePath, original);

        await Assert.ThrowsAnyAsync<Exception>(() => SqliteBenchmarkTarget.OpenExistingRecoveryAsync(
            PhysicalStorageForm.SharedDocuments, $"invalid_{kind}", databasePath, CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(databasePath));
    }

    [Fact]
    public async Task Incomplete_child_cleanup_is_bounded_and_leaves_no_live_worker()
    {
        using var cancellation = new CancellationTokenSource();

        var proof = await RunAbortedProofAsync(
            CancellationAbortBound,
            cancellation.Token,
            _ => cancellation.Cancel());

        AssertBoundedCleanupLeftNoLiveWorker(proof);

        // The caller cancelled while the bound still had almost all of its 30 seconds left, so the abort has
        // to be that cancellation rather than the harness running out of time.
        Assert.IsAssignableFrom<OperationCanceledException>(proof.Exception);
    }

    [Fact]
    public async Task Exhausted_recovery_deadline_still_confirms_child_exit_within_the_cleanup_grace()
    {
        // Wait on the harness's own execution token rather than sleeping past a guessed deadline: it trips
        // exactly when the configured bound is exhausted, however slowly this runner started the worker.
        var proof = await RunAbortedProofAsync(
            DeadlineExhaustionBound,
            CancellationToken.None,
            recoveryExecution => recoveryExecution.WaitHandle.WaitOne(DeadlineExhaustionBound + SchedulingTolerance));

        AssertBoundedCleanupLeftNoLiveWorker(proof);
        Assert.True(
            proof.Elapsed >= DeadlineExhaustionBound,
            $"Recovery aborted after {proof.Elapsed} without exhausting its {DeadlineExhaustionBound} bound.");
    }

    [Fact]
    public async Task Retained_evidence_verifier_requires_the_out_of_band_digest_and_current_source()
    {
        var (result, output) = await RunProofAsync(
            PhysicalStorageForm.SharedDocuments,
            RecoveryFailurePoint.PreCommit,
            Path.Combine("retained-source", "recovery-evidence.json"));
        var accepted = await RecoveryProtocol.VerifyRetainedAsync(
            output, result.EvidenceFileSha256, CancellationToken.None);
        Assert.Equal(result.Evidence, accepted);
        Assert.Equal(0, await Program.Main(
        [
            "recovery-evidence-verify",
            "--evidence", output,
            "--expected-evidence-sha256", result.EvidenceFileSha256
        ]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RecoveryProtocol.VerifyRetainedAsync(
            output, new string('0', 64), CancellationToken.None));
        Assert.Throws<ArgumentException>(() => RecoveryEvidenceVerifyCommandLine.Parse(
            ["recovery-evidence-verify", "--evidence", output]));
    }

    [Fact]
    public async Task Retained_evidence_rejects_missing_numeric_and_undefined_enum_members()
    {
        var (_, output) = await RunProofAsync(
            PhysicalStorageForm.SharedDocuments,
            RecoveryFailurePoint.PreCommit,
            Path.Combine("retained-strict", "recovery-evidence.json"));
        var original = JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsObject();

        var missing = original.DeepClone().AsObject();
        Assert.True(missing.Remove("provider"));
        await File.WriteAllTextAsync(output, missing.ToJsonString());
        await Assert.ThrowsAsync<JsonException>(() => RecoveryProtocol.VerifyRetainedAsync(
            output, BenchmarkSubprocessCoordinator.DigestFile(output), CancellationToken.None));

        var numeric = original.DeepClone().AsObject();
        numeric["failurePoint"] = 1;
        await File.WriteAllTextAsync(output, numeric.ToJsonString());
        await Assert.ThrowsAsync<JsonException>(() => RecoveryProtocol.VerifyRetainedAsync(
            output, BenchmarkSubprocessCoordinator.DigestFile(output), CancellationToken.None));

        var undefined = original.DeepClone().AsObject();
        undefined["storageForm"] = "unknownForm";
        await File.WriteAllTextAsync(output, undefined.ToJsonString());
        await Assert.ThrowsAsync<JsonException>(() => RecoveryProtocol.VerifyRetainedAsync(
            output, BenchmarkSubprocessCoordinator.DigestFile(output), CancellationToken.None));
    }

    [Fact]
    public async Task Retained_evidence_semantics_reject_same_process_and_over_bound_receipts()
    {
        var (result, output) = await RunProofAsync(
            PhysicalStorageForm.SharedDocuments,
            RecoveryFailurePoint.PreCommit,
            Path.Combine("retained-semantics", "recovery-evidence.json"));
        var evidence = result.Evidence;

        await AssertFreshlyAnchoredRejectedAsync(output, evidence with
        {
            RecoveryProcessId = evidence.CoordinatorProcessId
        });
        await AssertFreshlyAnchoredRejectedAsync(output, evidence with
        {
            RecoveryExecutionElapsedMilliseconds =
                evidence.ConfiguredRecoveryExecutionBoundMilliseconds + 1,
            RecoveryExecutionCompletedWithinBound = false
        });
    }

    private static async Task AssertAnchoredRejectedAsync(
        string output,
        string originalDigest,
        RecoveryEvidence altered)
    {
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(altered, BenchmarkJson.Options));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecoveryProtocol.VerifyRetainedAsync(
            output, originalDigest, CancellationToken.None));
    }

    private static async Task AssertFreshlyAnchoredRejectedAsync(string output, RecoveryEvidence altered)
    {
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(altered, BenchmarkJson.Options));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecoveryProtocol.VerifyRetainedAsync(
            output, BenchmarkSubprocessCoordinator.DigestFile(output), CancellationToken.None));
    }

    private async Task<(RecoveryProofResult Result, string Output)> RunProofAsync(
        PhysicalStorageForm storageForm,
        RecoveryFailurePoint failurePoint,
        string relativeOutput)
    {
        await WarmRecoveryStartupAsync();
        var output = Path.Combine(scratch, relativeOutput);
        var result = await SqliteProcessFailureRecovery.RunAsync(
            storageForm,
            scratch,
            failurePoint,
            CancellationToken.None,
            evidenceOutputPath: output);
        return (result, output);
    }

    /// <summary>Runs a proof that aborts at the worker-started handshake, timing the cleanup that follows.</summary>
    private async Task<AbortedProof> RunAbortedProofAsync(
        TimeSpan recoveryExecutionBound,
        CancellationToken cancellationToken,
        Action<CancellationToken> abortAtWorkerStart)
    {
        await WarmRecoveryStartupAsync();
        var stopwatch = Stopwatch.StartNew();
        var workerProcessId = 0;
        var abortedAt = TimeSpan.Zero;

        var exception = await Record.ExceptionAsync(() => SqliteProcessFailureRecovery.RunAsync(
            PhysicalStorageForm.SharedDocuments,
            scratch,
            RecoveryFailurePoint.PreCommit,
            cancellationToken,
            recoveryExecutionBound,
            workerStarted: (processId, recoveryExecution) =>
            {
                workerProcessId = processId;
                abortAtWorkerStart(recoveryExecution);
                abortedAt = stopwatch.Elapsed;
            }));

        return new AbortedProof(
            exception,
            recoveryExecutionBound,
            workerProcessId,
            stopwatch.Elapsed,
            stopwatch.Elapsed - abortedAt);
    }

    /// <summary>
    /// Pays the one-off cost of the seeding path before a bounded proof is timed. Every recovery execution
    /// bound — the default 15 seconds included — covers seeding and process spawn as well as the work under
    /// test, and a cold start of that path costs seconds on a contended runner.
    /// </summary>
    private async Task WarmRecoveryStartupAsync()
    {
        RecoveryProtocol.CaptureSourceSnapshot();
        var store = await SqliteBenchmarkTarget.InitializeRecoveryAsync(
            PhysicalStorageForm.SharedDocuments,
            "warmup_" + Guid.NewGuid().ToString("N")[..12],
            Path.Combine(scratch, "warmup", "durable.db"),
            CancellationToken.None);
        await store.SaveAsync(
            RecoveryProtocol.Save(RecoveryProtocol.Content("open", 1), expectedVersion: 0),
            CancellationToken.None);
    }

    private static void AssertBoundedCleanupLeftNoLiveWorker(AbortedProof proof)
    {
        Assert.NotNull(proof.Exception);
        Assert.True(
            proof.WorkerProcessId > 0,
            $"Recovery worker never started within the {proof.RecoveryExecutionBound} bound: {proof.Exception}");

        // A cleanup that outran its grace is reported as a TimeoutException wrapping the child failure, so the
        // exception shape confirms the bound was honoured independently of this runner's clock.
        Assert.False(
            proof.Exception is TimeoutException { InnerException: not null },
            $"Cleanup exceeded its grace period: {proof.Exception}");
        Assert.InRange(proof.CleanupElapsed, TimeSpan.Zero, CleanupGrace + SchedulingTolerance);
        AssertProcessExited(proof.WorkerProcessId);
    }

    /// <summary>A recovery proof that was aborted at the worker-started handshake.</summary>
    /// <param name="CleanupElapsed">Wall clock spent between that abort and the harness returning.</param>
    private readonly record struct AbortedProof(
        Exception? Exception,
        TimeSpan RecoveryExecutionBound,
        int WorkerProcessId,
        TimeSpan Elapsed,
        TimeSpan CleanupElapsed);

    private static void AssertDistinct(params int[] values) => Assert.Equal(values.Length, values.Distinct().Count());

    private static void AssertProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.True(process.HasExited, $"Recovery worker {processId} remained live after bounded cleanup.");
        }
        catch (ArgumentException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        return ValueTask.CompletedTask;
    }
}
