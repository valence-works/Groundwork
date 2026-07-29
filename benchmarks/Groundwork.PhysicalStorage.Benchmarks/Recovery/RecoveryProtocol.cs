using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;

namespace Groundwork.PhysicalStorage.Benchmarks;

internal enum RecoveryFailurePoint
{
    PreCommit,
    CommittedBeforeAcknowledgement
}

internal sealed record RecoverySourceSnapshot(
    string GitCommit,
    bool GitDirty,
    string GitTreeDigest,
    string HarnessAssemblySha256);

internal sealed record RecoveryWorkerRequest(
    string ProtocolVersion,
    RecoverySourceSnapshot Source,
    PhysicalStorageForm StorageForm,
    string Instance,
    string DatabasePath,
    RecoveryFailurePoint FailurePoint,
    string BarrierPath);

internal sealed record RecoveryBarrier(
    string ProtocolVersion,
    RecoverySourceSnapshot Source,
    RecoveryFailurePoint FailurePoint,
    int WorkerProcessId,
    string State);

internal sealed record RecoveryVerificationRequest(
    string ProtocolVersion,
    RecoverySourceSnapshot Source,
    PhysicalStorageForm StorageForm,
    string Instance,
    string DatabasePath,
    RecoveryFailurePoint FailurePoint,
    string ResultPath);

internal sealed record RecoveryVerificationResult(
    string ProtocolVersion,
    RecoverySourceSnapshot Source,
    RecoveryFailurePoint FailurePoint,
    int RecoveryProcessId,
    long RecoveredVersion,
    string RecoveredStateDigest,
    string RetryOutcome);

internal sealed record RecoveryEvidence(
    string SchemaVersion,
    RecoverySourceSnapshot Source,
    BenchmarkProvider Provider,
    PhysicalStorageForm StorageForm,
    RecoveryFailurePoint FailurePoint,
    int WorkerProcessId,
    string WorkerTerminationMethod,
    int WorkerExitCode,
    bool WorkerTerminated,
    int RecoveryProcessId,
    long RecoveredVersion,
    string RecoveredStateDigest,
    string RetryOutcome,
    long ConfiguredBoundMilliseconds,
    long ElapsedMilliseconds,
    bool CompletedWithinBound,
    bool Promotable)
{
    public string Seal { get; init; } = string.Empty;
}

internal static class RecoveryProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonOptions) { WriteIndented = false };
    internal const string Version = "groundwork.physical-storage.recovery/v1";
    internal const string EvidenceSchemaVersion = "groundwork.physical-storage.recovery-evidence/v1";
    internal const string DocumentId = "recovery-item";
    internal const int TimeoutMilliseconds = 15_000;
    internal const string KillTreeMethod = "process.kill-tree";

    internal static SaveDocumentRequest Save(string content, long expectedVersion) =>
        new(BenchmarkModelFactory.DocumentKind, DocumentId, "1", content, expectedVersion);

    internal static string Content(string status, int rank) =>
        JsonSerializer.Serialize(new { status, rank, category = "recovery" }, BenchmarkJson.CompactOptions);

    internal static string StateDigest(string id, long version, string content) =>
        Digest($"{id}\n{version}\n{content}");

    internal static string StateDigest(DocumentEnvelope document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return StateDigest(document.Id, document.Version, document.ContentJson);
    }

    internal static string ExpectedStateDigest(RecoveryFailurePoint failurePoint) =>
        failurePoint == RecoveryFailurePoint.PreCommit
            ? StateDigest(DocumentId, 1, Content("open", 1))
            : StateDigest(DocumentId, 2, Content("closed", 2));

    internal static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static string DigestFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    internal static RecoverySourceSnapshot CaptureSourceSnapshot()
    {
        var root = FindRepositoryRoot();
        var git = BenchmarkMetadata.CaptureGit(root);
        return new RecoverySourceSnapshot(
            git.Commit,
            git.Dirty,
            git.TreeDigest,
            DigestFile(Assembly.GetExecutingAssembly().Location));
    }

    internal static void RequireCurrentSource(RecoverySourceSnapshot expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (CaptureSourceSnapshot() != expected)
            throw new InvalidOperationException("Recovery request source binding does not match this process.");
    }

    internal static async Task<RecoveryEvidence> VerifyRetainedAsync(string evidencePath, CancellationToken cancellationToken)
    {
        var evidence = await ReadAsync<RecoveryEvidence>(evidencePath, cancellationToken);
        RequireCurrentSource(evidence.Source);
        Verify(evidence, evidence);
        return evidence;
    }

    internal static string Seal(RecoveryEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return Digest(JsonSerializer.Serialize(evidence with { Seal = string.Empty }, CompactJsonOptions));
    }

    internal static void Verify(RecoveryEvidence evidence, RecoveryEvidence expected)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(expected);
        var expectedVersion = evidence.FailurePoint == RecoveryFailurePoint.PreCommit ? 1 : 2;
        var expectedRetry = evidence.FailurePoint == RecoveryFailurePoint.PreCommit
            ? "notAttempted"
            : "concurrencyConflict";
        if (evidence.SchemaVersion != EvidenceSchemaVersion ||
            evidence.Source != expected.Source ||
            evidence.Provider != BenchmarkProvider.Sqlite ||
            evidence.Provider != expected.Provider ||
            evidence.StorageForm != expected.StorageForm ||
            evidence.FailurePoint != expected.FailurePoint ||
            evidence.Promotable ||
            evidence.Promotable != expected.Promotable ||
            evidence.WorkerProcessId <= 0 || evidence.WorkerProcessId != expected.WorkerProcessId ||
            evidence.RecoveryProcessId <= 0 || evidence.RecoveryProcessId != expected.RecoveryProcessId ||
            evidence.WorkerProcessId == evidence.RecoveryProcessId ||
            evidence.WorkerTerminationMethod != KillTreeMethod ||
            evidence.WorkerTerminationMethod != expected.WorkerTerminationMethod ||
            !evidence.WorkerTerminated || evidence.WorkerTerminated != expected.WorkerTerminated ||
            evidence.WorkerExitCode == 0 || evidence.WorkerExitCode != expected.WorkerExitCode ||
            evidence.ConfiguredBoundMilliseconds <= 0 || evidence.ConfiguredBoundMilliseconds != expected.ConfiguredBoundMilliseconds ||
            evidence.ElapsedMilliseconds < 0 || evidence.ElapsedMilliseconds != expected.ElapsedMilliseconds ||
            evidence.CompletedWithinBound != (evidence.ElapsedMilliseconds <= evidence.ConfiguredBoundMilliseconds) ||
            !evidence.CompletedWithinBound || evidence.CompletedWithinBound != expected.CompletedWithinBound ||
            evidence.RecoveredVersion != expectedVersion || evidence.RecoveredVersion != expected.RecoveredVersion ||
            evidence.RecoveredStateDigest != ExpectedStateDigest(evidence.FailurePoint) ||
            evidence.RecoveredStateDigest != expected.RecoveredStateDigest ||
            evidence.RetryOutcome != expectedRetry || evidence.RetryOutcome != expected.RetryOutcome ||
            !string.Equals(evidence.Seal, Seal(evidence), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Recovery evidence failed its sealed semantic contract.");
        }
        var serialized = JsonSerializer.Serialize(evidence, CompactJsonOptions);
        if (serialized.Contains("Data Source", StringComparison.OrdinalIgnoreCase) ||
            serialized.Contains("DatabasePath", StringComparison.OrdinalIgnoreCase) ||
            serialized.Contains("Password", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Recovery evidence retained unsafe target metadata.");
        }
    }

    internal static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
        File.Move(temporary, path, overwrite: false);
    }

    internal static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Recovery protocol artifact '{path}' is null.");
    }

    internal static async Task WaitForFileAsync(string path, CancellationToken cancellationToken)
    {
        while (!File.Exists(path))
            await Task.Delay(25, cancellationToken);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Groundwork.slnx was not found for recovery source binding.");
    }

    private static JsonSerializerOptions CreateJsonOptions() => new(BenchmarkJson.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };
}
