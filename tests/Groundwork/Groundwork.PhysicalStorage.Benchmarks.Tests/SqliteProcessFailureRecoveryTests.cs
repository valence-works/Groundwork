using Groundwork.Core.PhysicalStorage;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class SqliteProcessFailureRecoveryTests : IAsyncDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), $"groundwork-recovery-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public async Task ProcessFailureRecovery_proves_the_declared_durable_outcome(
        int failurePointValue,
        long expectedVersion)
    {
        var failurePoint = (RecoveryFailurePoint)failurePointValue;
        var evidence = await SqliteProcessFailureRecovery.RunAsync(
            PhysicalStorageForm.SharedDocuments,
            scratch,
            failurePoint,
            CancellationToken.None);

        Assert.Equal(RecoveryProtocol.KillTreeMethod, evidence.WorkerTerminationMethod);
        Assert.True(evidence.WorkerTerminated);
        Assert.NotEqual(0, evidence.WorkerExitCode);
        Assert.NotEqual(evidence.WorkerProcessId, evidence.RecoveryProcessId);
        Assert.True(evidence.CompletedWithinBound);
        Assert.InRange(evidence.ElapsedMilliseconds, 0, evidence.ConfiguredBoundMilliseconds);
        Assert.Equal(expectedVersion, evidence.RecoveredVersion);
        Assert.False(evidence.Promotable);
        Assert.Equal(
            failurePoint == RecoveryFailurePoint.CommittedBeforeAcknowledgement
                ? "concurrencyConflict"
                : "notAttempted",
            evidence.RetryOutcome);
    }

    [Fact]
    public async Task Recovery_evidence_fails_closed_for_resealed_semantic_tampering()
    {
        var evidence = await SqliteProcessFailureRecovery.RunAsync(
            PhysicalStorageForm.DedicatedDocumentTable,
            scratch,
            RecoveryFailurePoint.CommittedBeforeAcknowledgement,
            CancellationToken.None);

        AssertResealedRejected(evidence, evidence with { FailurePoint = RecoveryFailurePoint.PreCommit });
        AssertResealedRejected(evidence, evidence with { RecoveredStateDigest = new string('0', 64) });
        AssertResealedRejected(evidence, evidence with { RecoveredVersion = 1 });
        AssertResealedRejected(evidence, evidence with { RetryOutcome = "notAttempted" });
        AssertResealedRejected(evidence, evidence with { WorkerProcessId = evidence.WorkerProcessId + 1 });
        AssertResealedRejected(evidence, evidence with { RecoveryProcessId = evidence.WorkerProcessId });
        AssertResealedRejected(evidence, evidence with { WorkerTerminationMethod = "claimed" });
        AssertResealedRejected(evidence, evidence with { WorkerExitCode = 0 });
        AssertResealedRejected(evidence, evidence with { WorkerTerminated = false });
        AssertResealedRejected(evidence, evidence with { ConfiguredBoundMilliseconds = evidence.ConfiguredBoundMilliseconds + 1 });
        AssertResealedRejected(evidence, evidence with { ElapsedMilliseconds = evidence.ElapsedMilliseconds + 1 });
        AssertResealedRejected(evidence, evidence with { CompletedWithinBound = false });
        AssertResealedRejected(evidence, evidence with
        {
            Source = evidence.Source with { GitCommit = evidence.Source.GitCommit + "0" }
        });
        AssertResealedRejected(evidence, evidence with
        {
            Source = evidence.Source with { GitTreeDigest = new string('0', 64) }
        });
        AssertResealedRejected(evidence, evidence with
        {
            Source = evidence.Source with { HarnessAssemblySha256 = new string('0', 64) }
        });
        Assert.Throws<InvalidOperationException>(() =>
            RecoveryProtocol.Verify(evidence with { Seal = string.Empty }, evidence));

        var retained = JsonSerializer.Serialize(evidence, BenchmarkJson.CompactOptions);
        Assert.DoesNotContain("databasePath", retained, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data source", retained, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Recovery_protocol_rejects_an_incomplete_barrier_within_its_bound()
    {
        var missing = Path.Combine(scratch, "never-created.json");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            WaitForMissingBarrierAsync(missing));
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
    public async Task Recovery_proof_cli_retains_only_sanitized_sealed_evidence()
    {
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
        Assert.Contains("harnessAssemblySha256", retained, StringComparison.Ordinal);
        Assert.Contains("process.kill-tree", retained, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_children_reject_a_source_snapshot_that_they_cannot_recompute()
    {
        var source = RecoveryProtocol.CaptureSourceSnapshot() with { GitTreeDigest = new string('0', 64) };
        var mutation = new RecoveryWorkerRequest(
            RecoveryProtocol.Version,
            source,
            PhysicalStorageForm.SharedDocuments,
            "source-check",
            Path.Combine(scratch, "not-opened.db"),
            RecoveryFailurePoint.PreCommit,
            Path.Combine(scratch, "barrier.json"));
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

    [Fact]
    public async Task Recovery_open_existing_rejects_a_missing_database_without_creating_it()
    {
        var databasePath = Path.Combine(scratch, "missing", "durable.db");

        await Assert.ThrowsAsync<FileNotFoundException>(() => SqliteBenchmarkTarget.OpenExistingRecoveryAsync(
            PhysicalStorageForm.SharedDocuments,
            "missing_database",
            databasePath,
            CancellationToken.None));

        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    public async Task Incomplete_child_cleanup_is_bounded_and_leaves_no_live_worker()
    {
        using var cancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        var workerProcessId = 0;

        var exception = await Record.ExceptionAsync(() => SqliteProcessFailureRecovery.RunAsync(
            PhysicalStorageForm.SharedDocuments,
            scratch,
            RecoveryFailurePoint.PreCommit,
            cancellation.Token,
            TimeSpan.FromSeconds(1),
            workerStarted: processId =>
            {
                workerProcessId = processId;
                cancellation.Cancel();
            }));

        Assert.NotNull(exception);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.True(workerProcessId > 0);
        AssertProcessExited(workerProcessId);
    }

    [Fact]
    public async Task Retained_evidence_verifier_recomputes_source_before_accepting_a_resealed_document()
    {
        var output = Path.Combine(scratch, "retained-source", "recovery-evidence.json");
        var evidence = await SqliteProcessFailureRecovery.RunAsync(
            PhysicalStorageForm.SharedDocuments,
            scratch,
            RecoveryFailurePoint.PreCommit,
            CancellationToken.None,
            evidenceOutputPath: output);
        var accepted = await RecoveryProtocol.VerifyRetainedAsync(output, CancellationToken.None);
        Assert.Equal(evidence, accepted);

        var tampered = evidence with
        {
            Source = evidence.Source with { GitTreeDigest = new string('0', 64) }
        };
        tampered = tampered with { Seal = RecoveryProtocol.Seal(tampered) };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(tampered, BenchmarkJson.Options));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RecoveryProtocol.VerifyRetainedAsync(output, CancellationToken.None));
    }

    [Fact]
    public async Task Retained_evidence_rejects_an_omitted_default_valued_required_member_without_a_seal_change()
    {
        var output = Path.Combine(scratch, "retained-incomplete", "recovery-evidence.json");
        await SqliteProcessFailureRecovery.RunAsync(
            PhysicalStorageForm.SharedDocuments,
            scratch,
            RecoveryFailurePoint.PreCommit,
            CancellationToken.None,
            evidenceOutputPath: output);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsObject();
        Assert.True(node.Remove("provider"));
        await File.WriteAllTextAsync(output, node.ToJsonString());

        await Assert.ThrowsAsync<JsonException>(() =>
            RecoveryProtocol.VerifyRetainedAsync(output, CancellationToken.None));
    }

    private static void AssertResealedRejected(RecoveryEvidence expected, RecoveryEvidence altered)
    {
        altered = altered with { Seal = RecoveryProtocol.Seal(altered) };
        Assert.Throws<InvalidOperationException>(() => RecoveryProtocol.Verify(altered, expected));
    }

    private static async Task WaitForMissingBarrierAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        try
        {
            await RecoveryProtocol.WaitForFileAsync(path, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

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
