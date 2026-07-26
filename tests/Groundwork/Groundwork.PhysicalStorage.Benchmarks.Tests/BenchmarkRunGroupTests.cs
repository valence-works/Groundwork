using System.Security.Cryptography;
using System.Text.Json;
using Groundwork.Core.PhysicalStorage;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkRunGroupTests : IDisposable
{
    private readonly string scratch = Path.Combine(
        Path.GetTempPath(),
        $"groundwork-run-group-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Coordinator_group_baseline_matches_measured_runs_and_detects_process_median_regression()
    {
        var baselineRoot = Path.Combine(scratch, "baseline");
        var candidateRoot = Path.Combine(scratch, "candidate");
        var baseline = await WriteGroupAsync(baselineRoot, "baseline", 1_000);
        var candidate = await WriteGroupAsync(candidateRoot, "candidate", 1_300, writeManifest: false);

        var report = await BenchmarkRunGroupRegressionEvaluator.CompareAsync(
            candidateRoot,
            candidate,
            baselineRoot,
            RegressionPolicy.Scheduled,
            CancellationToken.None);

        Assert.True(report.Regressed);
        var evaluation = Assert.Single(report.Evaluations);
        Assert.True(evaluation.IsComparable);
        Assert.Equal(
            "Sqlite/PhysicalEntityTable/IndexedQuery/n1000-profileordinary-json-v1-selectivity5000bp",
            evaluation.CaseIdentity);
        Assert.Equal(3, report.MinimumIndependentRuns);
    }

    [Fact]
    public async Task Coordinator_baseline_option_consumes_a_group_root_and_propagates_confirmation()
    {
        var baselineRoot = Path.Combine(scratch, "coordinator-baseline");
        var candidateRoot = Path.Combine(scratch, "coordinator-candidate");
        await WriteGroupAsync(baselineRoot, "baseline", 1_000);
        var configuration = BenchmarkProfiles.Scheduled with
        {
            DatasetSize = 1_000,
            Providers = [BenchmarkProvider.Sqlite],
            StorageForms = [PhysicalStorageForm.PhysicalEntityTable]
        };
        var request = new BenchmarkRunRequest(
            FindRepositoryRoot(),
            configuration,
            [BenchmarkWorkload.IndexedQuery],
            candidateRoot,
            baselineRoot,
            AllowContainers: false,
            RegressionConfirmationRun: true,
            new BenchmarkMatrixDimensions([1_000], [0], [5_000], 3)
            {
                PayloadProfileIds = BenchmarkPayloadProfiles.DefaultReviewedIds
            });
        var coordinator = new BenchmarkSubprocessCoordinator(
            progress: null,
            async (requestPath, responsePath, cancellationToken) =>
            {
                var invocation = await BenchmarkSubprocessCoordinator.ReadAsync<BenchmarkWorkerInvocation>(
                    requestPath,
                    cancellationToken);
                await MaterializeWorkerAsync(
                    invocation,
                    requestPath,
                    responsePath,
                    latency: 1_300,
                    cancellationToken);
                return 0;
            });

        var result = await coordinator.RunAsync(request, CancellationToken.None);

        Assert.True(result.ConfirmedRegression);
        var manifest = await BenchmarkRunGroupVerifier.VerifyAsync(
            result.RunDirectory,
            CancellationToken.None);
        Assert.True(manifest.ConfirmedRegression);
        Assert.NotNull(manifest.RegressionReport);
    }

    [Fact]
    public async Task Verifier_rejects_tampered_protocol_artifacts()
    {
        var root = Path.Combine(scratch, "tampered");
        var manifest = await WriteGroupAsync(root, "tampered", 1_000);
        var request = Path.Combine(root, manifest.Runs[0].Request.Replace('/', Path.DirectorySeparatorChar));
        await File.AppendAllTextAsync(request, " ");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkRunGroupVerifier.VerifyAsync(root, CancellationToken.None));

        Assert.Contains("digest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Verifier_rejects_raw_measurements_that_no_longer_match_consumer_evidence()
    {
        var root = Path.Combine(scratch, "tampered-raw");
        await WriteGroupAsync(root, "tampered-raw", 1_000);
        await File.AppendAllTextAsync(
            Path.Combine(root, "runs", "000001", "raw", "measurements.jsonl"),
            Environment.NewLine);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkRunGroupVerifier.VerifyAsync(root, CancellationToken.None));

        Assert.Contains("raw measurements digest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Comparison_rejects_a_fully_resealed_forged_consumer_semantic_digest()
    {
        var baselineRoot = Path.Combine(scratch, "sealed-semantic-baseline");
        var candidateRoot = Path.Combine(scratch, "sealed-semantic-candidate");
        await WriteGroupAsync(baselineRoot, "baseline", 1_000);
        var candidate = await WriteGroupAsync(candidateRoot, "candidate", 1_000);
        var originalEntry = candidate.Runs[0];
        var workerRoot = Path.Combine(candidateRoot, "runs", originalEntry.Ordinal.ToString("D6"));
        var consumerPath = Path.Combine(workerRoot, "reports", "consumer-evidence.json");
        var consumer = await ReadJsonAsync<BenchmarkConsumerEvidenceReport>(consumerPath);
        await WriteJsonAsync(consumerPath, consumer with
        {
            Results = [consumer.Results[0] with { ResultDigest = new string('0', 64) }]
        });
        await ResealWorkerIntegrityAsync(workerRoot);

        var responsePath = Path.Combine(candidateRoot, originalEntry.Response.Replace('/', Path.DirectorySeparatorChar));
        var response = await ReadJsonAsync<BenchmarkWorkerResponse>(responsePath);
        response = response with
        {
            Artifacts = response.Artifacts! with { ConsumerEvidenceDigest = Digest(consumerPath) }
        };
        await WriteJsonAsync(responsePath, response);
        var resealedEntry = originalEntry with
        {
            ConsumerEvidenceDigest = Digest(consumerPath),
            ResponseDigest = Digest(responsePath)
        };
        candidate = candidate with
        {
            Runs = candidate.Runs.Select(entry => entry.Ordinal == resealedEntry.Ordinal ? resealedEntry : entry).ToArray()
        };
        await WriteJsonAsync(Path.Combine(candidateRoot, "run-group.json"), candidate);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkRunGroupRegressionEvaluator.CompareAsync(
                candidateRoot,
                candidate,
                baselineRoot,
                RegressionPolicy.Scheduled,
                CancellationToken.None));

        Assert.Contains("digest claims", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Comparison_rejects_a_fully_resealed_native_plan_sidecar_drift()
    {
        var baselineRoot = Path.Combine(scratch, "native-plan-baseline");
        var candidateRoot = Path.Combine(scratch, "native-plan-candidate");
        await WriteGroupAsync(baselineRoot, "baseline", 1_000);
        var candidate = await WriteGroupAsync(candidateRoot, "candidate", 1_000);
        var workerRoot = Path.Combine(candidateRoot, "runs", candidate.Runs[0].Ordinal.ToString("D6"));
        var manifest = await ReadJsonAsync<BenchmarkRunManifest>(Path.Combine(workerRoot, "manifest.json"));
        var planPath = Path.Combine(
            workerRoot,
            manifest.PlanArtifacts[0].Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(planPath, "SEARCH fixture_index with drift");
        await ResealWorkerIntegrityAsync(workerRoot);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkRunGroupRegressionEvaluator.CompareAsync(
                candidateRoot,
                candidate,
                baselineRoot,
                RegressionPolicy.Scheduled,
                CancellationToken.None));

        Assert.Contains("file and assertion sidecar differ", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Comparison_rejects_a_candidate_missing_a_baseline_tuple()
    {
        var baselineRoot = Path.Combine(scratch, "missing-baseline");
        var candidateRoot = Path.Combine(scratch, "missing-candidate");
        await WriteGroupAsync(
            baselineRoot,
            "baseline",
            1_000,
            workloads: [BenchmarkWorkload.IndexedQuery, BenchmarkWorkload.Insert]);
        var candidate = await WriteGroupAsync(
            candidateRoot,
            "candidate",
            1_000,
            writeManifest: false,
            workloads: [BenchmarkWorkload.IndexedQuery]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkRunGroupRegressionEvaluator.CompareAsync(
                candidateRoot,
                candidate,
                baselineRoot,
                RegressionPolicy.Scheduled,
                CancellationToken.None));

        Assert.Contains("missing baseline tuples", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Insert", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comparison_rejects_independent_runs_with_different_canonical_result_semantics()
    {
        var baselineRoot = Path.Combine(scratch, "semantic-baseline");
        var candidateRoot = Path.Combine(scratch, "semantic-candidate");
        await WriteGroupAsync(baselineRoot, "baseline", 1_000);
        var candidate = await WriteGroupAsync(
            candidateRoot,
            "candidate",
            1_000,
            semanticResultForRun: run => run == 2 ? "drifted-result" : "result");

        var report = await BenchmarkRunGroupRegressionEvaluator.CompareAsync(
            candidateRoot, candidate, baselineRoot, RegressionPolicy.Scheduled, CancellationToken.None);

        var evaluation = Assert.Single(report.Evaluations);
        Assert.False(evaluation.IsComparable);
        Assert.Contains(evaluation.Diagnostics, diagnostic =>
            diagnostic.Contains("canonical result semantics", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Comparison_rejects_a_candidate_with_an_extra_tuple()
    {
        var baselineRoot = Path.Combine(scratch, "extra-baseline");
        var candidateRoot = Path.Combine(scratch, "extra-candidate");
        await WriteGroupAsync(
            baselineRoot,
            "baseline",
            1_000,
            workloads: [BenchmarkWorkload.IndexedQuery]);
        var candidate = await WriteGroupAsync(
            candidateRoot,
            "candidate",
            1_000,
            writeManifest: false,
            workloads: [BenchmarkWorkload.IndexedQuery, BenchmarkWorkload.Insert]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkRunGroupRegressionEvaluator.CompareAsync(
                candidateRoot,
                candidate,
                baselineRoot,
                RegressionPolicy.Scheduled,
                CancellationToken.None));

        Assert.Contains("extra tuples", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Insert", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verifier_rejects_duplicate_semantic_worker_tuple_run_identities()
    {
        var root = Path.Combine(scratch, "duplicate-semantic-worker");
        await WriteGroupAsync(
            root,
            "duplicate-semantic-worker",
            1_000,
            independentRunIds: [1, 1, 3]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkRunGroupVerifier.VerifyAsync(root, CancellationToken.None));

        Assert.Contains("duplicate semantic worker", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
    }

    private static async Task<BenchmarkRunGroupManifest> WriteGroupAsync(
        string root,
        string groupId,
        long latency,
        bool writeManifest = true,
        IReadOnlyList<BenchmarkWorkload>? workloads = null,
        IReadOnlyList<int>? independentRunIds = null,
        Func<int, string>? semanticResultForRun = null)
    {
        const string commit = "test-commit";
        var treeDigest = new string('a', 64);
        var entries = new List<BenchmarkRunGroupEntry>();
        workloads ??= [BenchmarkWorkload.IndexedQuery];
        independentRunIds ??= [1, 2, 3];
        var ordinal = 0;
        foreach (var workload in workloads)
        {
            foreach (var independentRun in independentRunIds)
            {
                var ordinalText = (++ordinal).ToString("D6");
                var runRoot = Path.Combine(root, "runs", ordinalText);
                var requestPath = Path.Combine(root, "protocol", "requests", $"{ordinalText}.json");
                var responsePath = Path.Combine(root, "protocol", "responses", $"{ordinalText}.json");
                var manifestPath = Path.Combine(runRoot, "manifest.json");
                var elsaPath = Path.Combine(runRoot, "reports", "elsa-migration-evidence.json");
                var consumerPath = Path.Combine(runRoot, "reports", "consumer-evidence.json");
                Directory.CreateDirectory(Path.GetDirectoryName(requestPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(responsePath)!);

                var configuration = BenchmarkProfiles.Scheduled with
                {
                    DatasetSize = 1_000,
                    Providers = [BenchmarkProvider.Sqlite],
                    StorageForms = [PhysicalStorageForm.PhysicalEntityTable]
                };
                var shape = new BenchmarkDataShape(
                    1_000,
                    BenchmarkPayloadProfiles.For(workload),
                    5_000);
                configuration = configuration with { DataShape = shape };
                var request = new BenchmarkRunRequest(
                    root,
                    configuration,
                    [workload],
                    runRoot,
                    null,
                    AllowContainers: false,
                    RegressionConfirmationRun: false,
                    DataShape: shape,
                    IndependentRun: independentRun,
                    Role: BenchmarkExecutionRole.Measured);
                var invocation = new BenchmarkWorkerInvocation(
                    BenchmarkRunProtocol.ProtocolVersion,
                    groupId,
                    ordinal,
                    independentRun,
                    BenchmarkExecutionRole.Measured,
                    request)
                {
                    ExpectedGitCommit = commit,
                    ExpectedGitTreeDigest = treeDigest
                };
                await WriteJsonAsync(requestPath, invocation);
                var requestDigest = Digest(requestPath);

                await WriteSealedWorkerAsync(
                    invocation,
                    latency,
                    semanticResultForRun?.Invoke(independentRun) ?? "result",
                    CancellationToken.None);

                var artifacts = new BenchmarkWorkerArtifactDigests(
                    "manifest.json",
                    Digest(manifestPath),
                    "reports/elsa-migration-evidence.json",
                    Digest(elsaPath),
                    "reports/consumer-evidence.json",
                    Digest(consumerPath));
                var response = new BenchmarkWorkerResponse(
                    BenchmarkRunProtocol.ProtocolVersion,
                    groupId,
                    ordinal,
                    BenchmarkExecutionRole.Measured,
                    Succeeded: true,
                    runRoot,
                    consumerPath,
                    FailureType: null)
                {
                    RequestDigest = requestDigest,
                    GitCommit = commit,
                    GitTreeDigest = treeDigest,
                    Artifacts = artifacts
                };
                await WriteJsonAsync(responsePath, response);
                entries.Add(new BenchmarkRunGroupEntry(
                    ordinal,
                    independentRun,
                    BenchmarkExecutionRole.Measured,
                    Relative(root, requestPath),
                    Relative(root, responsePath),
                    Relative(root, consumerPath),
                    Digest(consumerPath))
                {
                    RequestDigest = requestDigest,
                    ResponseDigest = Digest(responsePath),
                    WorkerManifest = Relative(root, manifestPath),
                    WorkerManifestDigest = Digest(manifestPath),
                    ElsaMigrationEvidence = Relative(root, elsaPath),
                    ElsaMigrationEvidenceDigest = Digest(elsaPath)
                });
            }
        }

        var group = new BenchmarkRunGroupManifest(
            BenchmarkRunProtocol.ProtocolVersion,
            groupId,
            Promotable: false,
            ExternalOracleJoinRequired: true,
            commit,
            GitDirty: false,
            DateTimeOffset.UtcNow,
            entries)
        {
            GitTreeDigest = treeDigest
        };
        if (writeManifest)
            await WriteJsonAsync(Path.Combine(root, "run-group.json"), group);
        return group;
    }

    private static async Task MaterializeWorkerAsync(
        BenchmarkWorkerInvocation invocation,
        string requestPath,
        string responsePath,
        long latency,
        CancellationToken cancellationToken)
    {
        var runRoot = invocation.Request.OutputDirectory!;
        var manifestPath = Path.Combine(runRoot, "manifest.json");
        var elsaPath = Path.Combine(runRoot, "reports", "elsa-migration-evidence.json");
        var consumerPath = Path.Combine(runRoot, "reports", "consumer-evidence.json");
        await WriteSealedWorkerAsync(invocation, latency, "result", cancellationToken);
        var responseConsumer = invocation.Role == BenchmarkExecutionRole.Measured ? consumerPath : null;
        var consumerDigest = responseConsumer is null ? null : Digest(responseConsumer);

        var artifacts = new BenchmarkWorkerArtifactDigests(
            "manifest.json",
            Digest(manifestPath),
            "reports/elsa-migration-evidence.json",
            Digest(elsaPath),
            responseConsumer is null ? null : "reports/consumer-evidence.json",
            consumerDigest);
        await WriteJsonAsync(
            responsePath,
            new BenchmarkWorkerResponse(
                BenchmarkRunProtocol.ProtocolVersion,
                invocation.RunGroupId,
                invocation.Ordinal,
                invocation.Role,
                Succeeded: true,
                runRoot,
                responseConsumer,
                FailureType: null)
            {
                RequestDigest = Digest(requestPath),
                GitCommit = invocation.ExpectedGitCommit,
                GitTreeDigest = invocation.ExpectedGitTreeDigest,
                Artifacts = artifacts
            });
    }

    private static async Task WriteSealedWorkerAsync(
        BenchmarkWorkerInvocation invocation,
        long latency,
        string semanticResult,
        CancellationToken cancellationToken)
    {
        var runRoot = invocation.Request.OutputDirectory
                      ?? throw new InvalidOperationException("Fixture worker requires an output directory.");
        var layout = new ArtifactLayout(runRoot);
        var configuration = invocation.Request.Configuration with { DataShape = invocation.Request.DataShape };
        var machine = Machine(invocation.ExpectedGitCommit, invocation.ExpectedGitTreeDigest);
        var providers = new[]
        {
            new BenchmarkProviderMetadata(
                BenchmarkProvider.Sqlite,
                "test-provider",
                new Dictionary<string, string> { ["mode"] = "test" })
        };
        var tuple = BenchmarkRunTuple.From(invocation);
        var runId = $"{invocation.RunGroupId}-{invocation.Ordinal}";
        var samples = invocation.Role == BenchmarkExecutionRole.Measured
            ? Enumerable.Range(0, configuration.MeasurementIterations)
                .Select(iteration => new BenchmarkSample(
                    iteration,
                    Operations: 10,
                    ElapsedNanoseconds: 1_000_000_000,
                    AllocatedBytes: 1_000,
                    RoundTrips: 1,
                    LogicalPayloadBytes: 0,
                    LogicalMutations: 0,
                    StorageBefore: null,
                    StorageAfter: null,
                    ProviderWork: new Dictionary<string, long>(),
                    OperationLatencyNanoseconds: Enumerable.Repeat(latency, 10).ToArray())
                    .WithObservedCommandSignal())
                .ToArray()
            : [];
        var benchmarkCase = new BenchmarkCase(tuple.Provider, tuple.StorageForm, tuple.Workload);
        await using var writer = new BenchmarkArtifactWriter(layout);
        var planArtifacts = invocation.Role == BenchmarkExecutionRole.Measured
            ? await NativePlanFixtureArtifacts.WriteCanonicalAsync(
                writer,
                benchmarkCase,
                tuple.DataShape,
                cancellationToken)
            : [];
        var cases = invocation.Role == BenchmarkExecutionRole.Measured
            ? new[]
            {
                new BenchmarkCaseResult(
                    benchmarkCase,
                    new CorrectnessGateResult(true, true, true, true, true),
                    planArtifacts,
                    BenchmarkSummarizer.Summarize(benchmarkCase.Identity, samples),
                    samples,
                    [new BenchmarkObservableResult(
                        0,
                        "fixture-result",
                        semanticResult,
                        Version: 1,
                        Count: samples.Sum(sample => (long)sample.Operations),
                        Payload: null)])
            }
            : [];
        var report = new BenchmarkRunReport(
            BenchmarkProfiles.SchemaVersion,
            runId,
            configuration.Mode,
            cases,
            [],
            new BaselineEligibility(false, ["fixture is non-promotable"]),
            tuple.DataShape);
        var manifest = new BenchmarkRunManifest(
            BenchmarkProfiles.SchemaVersion,
            runId,
            "completed",
            configuration.Mode,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            invocation.ExpectedGitCommit,
            GitDirty: false,
            layout.RelativePath(layout.RawMeasurements),
            layout.RelativePath(layout.SummaryJson),
            layout.RelativePath(layout.ElsaMigrationEvidenceJson),
            layout.RelativePath(layout.MachineMetadata),
            layout.RelativePath(layout.ProviderMetadata),
            layout.RelativePath(layout.Configuration),
            planArtifacts,
            BaselineRun: null,
            RegressionConfirmationRun: false,
            Failure: null,
            ConsumerEvidence: invocation.Role == BenchmarkExecutionRole.Measured
                ? layout.RelativePath(layout.ConsumerEvidenceJson)
                : null,
            ArtifactIntegrity: layout.RelativePath(layout.ArtifactIntegrityJson));

        await writer.WriteManifestAsync(manifest, cancellationToken);
        await writer.WriteMachineAsync(machine, cancellationToken);
        await writer.WriteProvidersAsync(providers, cancellationToken);
        await writer.WriteConfigurationAsync(configuration, cancellationToken);
        foreach (var sample in samples)
            await writer.AppendSampleAsync(new RawBenchmarkRecord(benchmarkCase, sample), cancellationToken);
        await writer.WriteReportAsync(report, cancellationToken);
        if (invocation.Role == BenchmarkExecutionRole.Measured)
        {
            await writer.WriteConsumerEvidenceAsync(
                BenchmarkConsumerEvidenceReport.Create(
                    report,
                    configuration,
                    machine,
                    providers,
                    layout,
                    invocation.IndependentRun),
                cancellationToken);
        }
        await writer.WriteArtifactIntegrityAsync(manifest, cancellationToken);
    }

    private static BenchmarkMachineMetadata Machine(string commit, string treeDigest) => new(
        "test-os",
        "test-machine",
        "x64",
        ".NET test",
        "Release",
        8,
        false,
        1_000_000,
        "test-harness",
        commit,
        false,
        DateTimeOffset.UnixEpoch)
    {
        GitTreeDigest = treeDigest,
        CpuModel = "test-cpu",
        Memory = "test-memory",
        Storage = "test-storage",
        PowerManagement = "test-power"
    };

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, BenchmarkJson.Options));
    }

    private static async Task<T> ReadJsonAsync<T>(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, BenchmarkJson.Options)
               ?? throw new InvalidOperationException($"Fixture JSON '{path}' is null.");
    }

    private static async Task ResealWorkerIntegrityAsync(string workerRoot)
        => await NativePlanFixtureArtifacts.ResealIntegrityAsync(workerRoot, CancellationToken.None);

    private static string Digest(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));


    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Groundwork.slnx was not found.");
    }
}
