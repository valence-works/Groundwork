using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
    string GroundworkAssemblyClosureSha256,
    int GroundworkAssemblyClosureCount);

internal sealed record RecoveryWorkerRequest(
    string ProtocolVersion,
    RecoverySourceSnapshot Source,
    PhysicalStorageForm StorageForm,
    string Instance,
    string DatabasePath,
    RecoveryFailurePoint FailurePoint,
    string InstrumentationBarrierPath,
    string ResponseReleasePath,
    string RequesterAcknowledgementPath);

internal sealed record RecoveryInstrumentationBarrier(
    string ProtocolVersion,
    RecoverySourceSnapshot Source,
    RecoveryFailurePoint FailurePoint,
    int WorkerProcessId,
    string State);

internal sealed record RecoveryRequesterAcknowledgement(
    string ProtocolVersion,
    RecoverySourceSnapshot Source,
    RecoveryFailurePoint FailurePoint,
    int WorkerProcessId);

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
    long RecoveredBeforeRetryVersion,
    string RecoveredBeforeRetryStateDigest,
    string RetryOutcome,
    long RecoveredAfterRetryVersion,
    string RecoveredAfterRetryStateDigest);

internal sealed record RecoveryEvidence(
    string SchemaVersion,
    RecoverySourceSnapshot Source,
    BenchmarkProvider Provider,
    PhysicalStorageForm StorageForm,
    RecoveryFailurePoint FailurePoint,
    int CoordinatorProcessId,
    int WorkerProcessId,
    string WorkerTerminationMethod,
    int WorkerExitCode,
    bool WorkerTerminated,
    bool RequesterAcknowledgementObserved,
    int RecoveryProcessId,
    long RecoveredBeforeRetryVersion,
    string RecoveredBeforeRetryStateDigest,
    string RetryOutcome,
    long RecoveredAfterRetryVersion,
    string RecoveredAfterRetryStateDigest,
    long ConfiguredRecoveryExecutionBoundMilliseconds,
    long RecoveryExecutionElapsedMilliseconds,
    bool RecoveryExecutionCompletedWithinBound,
    bool Promotable);

internal sealed record RecoveryProofResult(
    RecoveryEvidence Evidence,
    string EvidenceFileSha256);

internal static class RecoveryProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonOptions) { WriteIndented = false };
    internal const string Version = "groundwork.physical-storage.recovery/v1";
    internal const string EvidenceSchemaVersion = "groundwork.physical-storage.recovery-evidence/v1";
    internal const string DocumentId = "recovery-item";
    internal const int TimeoutMilliseconds = 15_000;
    internal const int CleanupTimeoutMilliseconds = 5_000;
    internal const string KillTreeMethod = "process.kill-tree";
    internal static string BenchmarkAssemblyPath { get; } = Assembly.GetExecutingAssembly().Location;

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

    internal static RecoverySourceSnapshot CaptureSourceSnapshot()
    {
        var assemblyDirectory = Path.GetDirectoryName(BenchmarkAssemblyPath)
            ?? throw new InvalidOperationException("Benchmark assembly directory is unavailable.");
        var git = BenchmarkMetadata.CaptureGit(Program.FindRepositoryRoot(assemblyDirectory));
        var closure = CaptureGroundworkAssemblyClosure(BenchmarkAssemblyPath);
        var source = new RecoverySourceSnapshot(
            git.Commit,
            git.Dirty,
            git.TreeDigest,
            closure.Digest,
            closure.Count);
        ValidateSourceSnapshot(source);
        return source;
    }

    internal static void ValidateSourceSnapshot(RecoverySourceSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if ((source.GitCommit.Length is not (40 or 64)) || !IsLowerHex(source.GitCommit))
            throw new InvalidOperationException("Recovery source Git commit is unavailable or invalid.");
        if (source.GitTreeDigest.Length != 64 || !IsLowerHex(source.GitTreeDigest))
            throw new InvalidOperationException("Recovery source Git tree digest is unavailable or invalid.");
        if (source.GroundworkAssemblyClosureSha256.Length != 64 || !IsLowerHex(source.GroundworkAssemblyClosureSha256) ||
            source.GroundworkAssemblyClosureCount < 1)
        {
            throw new InvalidOperationException("Recovery source Groundwork assembly closure is invalid.");
        }
    }

    internal static void RequireCurrentSource(RecoverySourceSnapshot expected)
    {
        ValidateSourceSnapshot(expected);
        if (CaptureSourceSnapshot() != expected)
            throw new InvalidOperationException("Recovery request source binding does not match this process.");
    }

    internal static async Task<RecoveryEvidence> VerifyRetainedAsync(
        string evidencePath,
        string expectedEvidenceSha256,
        CancellationToken cancellationToken)
    {
        RequireSha256(expectedEvidenceSha256, "Expected recovery evidence SHA-256");
        var bytes = await File.ReadAllBytesAsync(evidencePath, cancellationToken);
        var actualDigest = SHA256.HashData(bytes);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedEvidenceSha256),
                actualDigest))
        {
            throw new InvalidOperationException("Recovery evidence file SHA-256 does not match the caller-provided digest.");
        }
        var evidence = JsonSerializer.Deserialize<RecoveryEvidence>(bytes, JsonOptions)
            ?? throw new InvalidOperationException("Recovery evidence is null.");
        RequireCurrentSource(evidence.Source);
        VerifySemantics(evidence);
        return evidence;
    }

    internal static void VerifySemantics(RecoveryEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateSourceSnapshot(evidence.Source);
        if (!Enum.IsDefined(evidence.StorageForm) || !Enum.IsDefined(evidence.FailurePoint))
            throw new InvalidOperationException("Recovery evidence contains an undefined enum value.");
        var expectedBeforeVersion = evidence.FailurePoint == RecoveryFailurePoint.PreCommit ? 1 : 2;
        var expectedRetry = evidence.FailurePoint == RecoveryFailurePoint.PreCommit
            ? "saved"
            : "concurrencyConflict";
        if (evidence.SchemaVersion != EvidenceSchemaVersion ||
            evidence.Provider != BenchmarkProvider.Sqlite ||
            evidence.Promotable || evidence.RequesterAcknowledgementObserved ||
            evidence.CoordinatorProcessId <= 0 || evidence.WorkerProcessId <= 0 || evidence.RecoveryProcessId <= 0 ||
            evidence.CoordinatorProcessId == evidence.WorkerProcessId ||
            evidence.CoordinatorProcessId == evidence.RecoveryProcessId ||
            evidence.WorkerProcessId == evidence.RecoveryProcessId ||
            evidence.WorkerTerminationMethod != KillTreeMethod ||
            !evidence.WorkerTerminated || evidence.WorkerExitCode == 0 ||
            evidence.ConfiguredRecoveryExecutionBoundMilliseconds <= 0 ||
            evidence.RecoveryExecutionElapsedMilliseconds < 0 ||
            evidence.RecoveryExecutionCompletedWithinBound !=
            (evidence.RecoveryExecutionElapsedMilliseconds <= evidence.ConfiguredRecoveryExecutionBoundMilliseconds) ||
            !evidence.RecoveryExecutionCompletedWithinBound ||
            evidence.RecoveredBeforeRetryVersion != expectedBeforeVersion ||
            evidence.RecoveredBeforeRetryStateDigest != ExpectedStateDigest(evidence.FailurePoint) ||
            evidence.RetryOutcome != expectedRetry ||
            evidence.RecoveredAfterRetryVersion != 2 ||
            evidence.RecoveredAfterRetryStateDigest != ExpectedStateDigest(RecoveryFailurePoint.CommittedBeforeAcknowledgement))
        {
            throw new InvalidOperationException("Recovery evidence failed its semantic contract.");
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

    private static (string Digest, int Count) CaptureGroundworkAssemblyClosure(string rootPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(rootPath))
            ?? throw new InvalidOperationException("Benchmark assembly directory is unavailable.");
        var pending = new Queue<(string ExpectedName, string Path)>();
        var assemblies = new Dictionary<string, string>(StringComparer.Ordinal);
        pending.Enqueue((ReadAssemblyName(rootPath), Path.GetFullPath(rootPath)));
        while (pending.TryDequeue(out var item))
        {
            var actualName = ReadAssemblyName(item.Path);
            if (!string.Equals(actualName, item.ExpectedName, StringComparison.Ordinal))
                throw new InvalidOperationException($"Groundwork assembly closure name mismatch for '{item.ExpectedName}'.");
            if (!actualName.StartsWith("Groundwork.", StringComparison.Ordinal) ||
                !assemblies.TryAdd(actualName, item.Path))
                continue;
            foreach (var reference in ReadGroundworkReferences(item.Path))
            {
                var matches = Directory.EnumerateFiles(directory, "*.dll")
                    .Where(path => string.Equals(
                        Path.GetFileNameWithoutExtension(path),
                        reference,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length != 1)
                    throw new InvalidOperationException($"Groundwork assembly closure reference '{reference}' is missing or ambiguous.");
                pending.Enqueue((reference, matches[0]));
            }
        }
        var entries = assemblies
            .Select(pair => (
                pair.Key,
                Digest: Convert.FromHexString(BenchmarkSubprocessCoordinator.DigestFile(pair.Value))))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        using var closure = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendLengthDelimited(closure, Encoding.UTF8.GetBytes("groundwork-assembly-closure/v1"));
        foreach (var entry in entries)
        {
            AppendLengthDelimited(closure, Encoding.UTF8.GetBytes(entry.Key));
            AppendLengthDelimited(closure, entry.Digest);
        }
        return (Convert.ToHexString(closure.GetHashAndReset()).ToLowerInvariant(), entries.Length);
    }

    private static string ReadAssemblyName(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.GetString(metadata.GetAssemblyDefinition().Name);
    }

    private static IReadOnlyList<string> ReadGroundworkReferences(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .Where(name => name.StartsWith("Groundwork.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AppendLengthDelimited(IncrementalHash hash, byte[] value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static bool IsLowerHex(string value) =>
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequireSha256(string value, string label)
    {
        if (value.Length != 64 || !IsLowerHex(value))
            throw new ArgumentException($"{label} must be a lowercase SHA-256 digest.", nameof(value));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(BenchmarkJson.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };
        for (var index = options.Converters.Count - 1; index >= 0; index--)
        {
            if (options.Converters[index] is JsonStringEnumConverter)
                options.Converters.RemoveAt(index);
        }
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
