using System.Security.Cryptography;
using System.Text.Json;
using Groundwork.Core.PhysicalStorage;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class ScheduledGroupVerificationTests : IAsyncLifetime
{
    private readonly string scratch = Path.Combine(
        Path.GetTempPath(),
        $"groundwork-scheduled-group-verification-{Guid.NewGuid():N}");

    [Fact]
    public async Task Sealed_scheduled_group_verifies_and_the_cli_accepts_the_exact_contract()
    {
        var fixture = await ScheduledGroupFixture.CreateAsync(scratch);

        var summary = await BenchmarkScheduledGroupVerifier.VerifyAsync(
            fixture.Root,
            fixture.Commit,
            CancellationToken.None);
        var exitCode = await Program.Main(
        [
            "verify-scheduled-group",
            "--root", fixture.Root,
            "--expected-git-commit", fixture.Commit
        ]);

        Assert.True(summary.ScheduledGroupVerified);
        Assert.Equal(4, summary.VerifiedWorkerCount);
        Assert.Equal(1, summary.VerifiedWarmupWorkerCount);
        Assert.Equal(3, summary.VerifiedMeasuredWorkerCount);
        Assert.Equal(1, summary.SemanticTupleCount);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Rejects_a_forged_consumer_bound_claim_even_when_the_artifact_ledger_is_resealed()
    {
        var fixture = await ScheduledGroupFixture.CreateAsync(
            scratch,
            new ScheduledGroupFixture.Options { ForgeMeasuredConsumerResultDigest = true });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkScheduledGroupVerifier.VerifyAsync(fixture.Root, fixture.Commit, CancellationToken.None));

        Assert.Contains("digest claims", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_a_resealed_concurrent_load_digest_that_does_not_match_raw_evidence()
    {
        var fixture = await ScheduledGroupFixture.CreateAsync(
            scratch,
            new ScheduledGroupFixture.Options
            {
                Workload = BenchmarkWorkload.ConcurrentCreate,
                ForgeMeasuredConcurrentLoadDigest = true
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkScheduledGroupVerifier.VerifyAsync(fixture.Root, fixture.Commit, CancellationToken.None));

        Assert.Contains("digest claims", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_a_resealed_target_scoped_signal_change_that_is_not_bound_by_the_summary()
    {
        var fixture = await ScheduledGroupFixture.CreateAsync(
            scratch,
            new ScheduledGroupFixture.Options { TamperMeasuredRawDatabaseSignal = true });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkScheduledGroupVerifier.VerifyAsync(fixture.Root, fixture.Commit, CancellationToken.None));

        Assert.Contains("raw measurements", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_a_resealed_raw_and_summary_pair_that_claims_round_trips_while_telemetry_is_unavailable()
    {
        var fixture = await ScheduledGroupFixture.CreateAsync(
            scratch,
            new ScheduledGroupFixture.Options { ForgeMeasuredUnavailableRoundTrips = true });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkScheduledGroupVerifier.VerifyAsync(fixture.Root, fixture.Commit, CancellationToken.None));

        Assert.Contains("target-scoped database-signal invariant", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_a_resealed_raw_and_summary_pair_with_a_forged_observed_count()
    {
        var fixture = await ScheduledGroupFixture.CreateAsync(
            scratch,
            new ScheduledGroupFixture.Options { ForgeMeasuredObservedCountMismatch = true });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkScheduledGroupVerifier.VerifyAsync(fixture.Root, fixture.Commit, CancellationToken.None));

        Assert.Contains("target-scoped database-signal invariant", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_a_resealed_raw_and_summary_pair_with_conflicting_secondary_signal_count()
    {
        var fixture = await ScheduledGroupFixture.CreateAsync(
            scratch,
            new ScheduledGroupFixture.Options { ForgeMeasuredSecondarySignalCount = true });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkScheduledGroupVerifier.VerifyAsync(fixture.Root, fixture.Commit, CancellationToken.None));

        Assert.Contains("target-scoped database-signal invariant", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_measured_workers_that_do_not_meet_the_authenticated_operation_and_duration_floors()
    {
        var fixture = await ScheduledGroupFixture.CreateAsync(
            scratch,
            new ScheduledGroupFixture.Options
            {
                MeasuredSampleCount = 30,
                OperationsPerSample = 1,
                ElapsedNanosecondsPerSample = 1_000_000
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkScheduledGroupVerifier.VerifyAsync(fixture.Root, fixture.Commit, CancellationToken.None));

        Assert.Contains("100-operation and 30-second", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rejects_dirty_or_wrong_commit_scheduled_evidence(bool dirty)
    {
        var fixture = await ScheduledGroupFixture.CreateAsync(
            scratch,
            new ScheduledGroupFixture.Options { GitDirty = dirty });
        var expectedCommit = dirty ? fixture.Commit : new string('c', fixture.Commit.Length);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkScheduledGroupVerifier.VerifyAsync(fixture.Root, expectedCommit, CancellationToken.None));

        Assert.Contains("expected clean Git commit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_a_run_group_path_that_attempts_to_escape_its_root()
    {
        var fixture = await ScheduledGroupFixture.CreateAsync(scratch);
        var manifestPath = Path.Combine(fixture.Root, "run-group.json");
        var manifest = await ReadJsonAsync<BenchmarkRunGroupManifest>(manifestPath);
        var first = manifest.Runs[0] with { Request = "../escaped-request.json" };
        await WriteJsonAsync(manifestPath, manifest with { Runs = [first, .. manifest.Runs.Skip(1)] });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkScheduledGroupVerifier.VerifyAsync(fixture.Root, fixture.Commit, CancellationToken.None));

        Assert.Contains("escapes the group root", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_a_measured_worker_without_the_required_artifact_integrity_ledger()
    {
        var fixture = await ScheduledGroupFixture.CreateAsync(
            scratch,
            new ScheduledGroupFixture.Options { WriteMeasuredIntegrityLedger = false });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkScheduledGroupVerifier.VerifyAsync(fixture.Root, fixture.Commit, CancellationToken.None));

        Assert.Contains("artifact-integrity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
        return Task.CompletedTask;
    }

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

    private sealed class ScheduledGroupFixture
    {
        private const string TreeDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        public const string DefaultCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private ScheduledGroupFixture(string root, string commit)
        {
            Root = root;
            Commit = commit;
        }

        public string Root { get; }
        public string Commit { get; }

        public sealed record Options
        {
            public bool ForgeMeasuredConsumerResultDigest { get; init; }
            public bool ForgeMeasuredConcurrentLoadDigest { get; init; }
            public bool TamperMeasuredRawDatabaseSignal { get; init; }
            public bool ForgeMeasuredUnavailableRoundTrips { get; init; }
            public bool ForgeMeasuredObservedCountMismatch { get; init; }
            public bool ForgeMeasuredSecondarySignalCount { get; init; }
            public bool GitDirty { get; init; }
            public bool WriteMeasuredIntegrityLedger { get; init; } = true;
            public int MeasuredSampleCount { get; init; } = 30;
            public int OperationsPerSample { get; init; } = 10;
            public long ElapsedNanosecondsPerSample { get; init; } = 1_000_000_000;
            public BenchmarkWorkload Workload { get; init; } = BenchmarkWorkload.Insert;
        }

        public static async Task<ScheduledGroupFixture> CreateAsync(string root, Options? options = null)
        {
            options ??= new Options();
            var fixture = new ScheduledGroupFixture(root, DefaultCommit);
            var entries = new List<BenchmarkRunGroupEntry>();
            var ordinal = 0;
            await fixture.AddWorkerAsync(
                ++ordinal,
                BenchmarkExecutionRole.UntimedWarmup,
                independentRun: 0,
                options,
                entries);
            for (var independentRun = 1; independentRun <= 3; independentRun++)
            {
                await fixture.AddWorkerAsync(
                    ++ordinal,
                    BenchmarkExecutionRole.Measured,
                    independentRun,
                    options,
                    entries);
            }

            await WriteJsonAsync(
                Path.Combine(root, "run-group.json"),
                new BenchmarkRunGroupManifest(
                    BenchmarkRunProtocol.ProtocolVersion,
                    "scheduled-fixture",
                    Promotable: false,
                    ExternalOracleJoinRequired: true,
                    fixture.Commit,
                    options.GitDirty,
                    DateTimeOffset.UnixEpoch,
                    entries)
                {
                    GitTreeDigest = TreeDigest
                });
            return fixture;
        }

        private async Task AddWorkerAsync(
            int ordinal,
            BenchmarkExecutionRole role,
            int independentRun,
            Options options,
            ICollection<BenchmarkRunGroupEntry> entries)
        {
            var ordinalText = ordinal.ToString("D6");
            var workerRoot = Path.Combine(Root, "runs", ordinalText);
            var requestPath = Path.Combine(Root, "protocol", "requests", $"{ordinalText}.json");
            var responsePath = Path.Combine(Root, "protocol", "responses", $"{ordinalText}.json");
            var configuration = BenchmarkProfiles.Scheduled with
            {
                DatasetSize = 1_000,
                Providers = [BenchmarkProvider.Sqlite],
                StorageForms = [PhysicalStorageForm.PhysicalEntityTable],
                DataShape = null
            };
            var shape = new BenchmarkDataShape(
                1_000,
                BenchmarkPayloadProfiles.For(options.Workload),
                BenchmarkSelectivityPolicy.IndexedQueryAcceptanceBasisPoints);
            var request = new BenchmarkRunRequest(
                Root,
                configuration,
                [options.Workload],
                workerRoot,
                null,
                AllowContainers: false,
                RegressionConfirmationRun: false,
                DataShape: shape,
                IndependentRun: independentRun,
                Role: role);
            var invocation = new BenchmarkWorkerInvocation(
                BenchmarkRunProtocol.ProtocolVersion,
                "scheduled-fixture",
                ordinal,
                independentRun,
                role,
                request)
            {
                ExpectedGitCommit = Commit,
                ExpectedGitTreeDigest = TreeDigest
            };
            await WriteJsonAsync(requestPath, invocation);

            var persistedConfiguration = configuration with { DataShape = shape };
            var layout = new ArtifactLayout(workerRoot);
            var machine = new BenchmarkMachineMetadata(
                "fixture-os",
                "fixture-machine",
                "x64",
                ".NET 10",
                "Release",
                8,
                false,
                1_000_000_000,
                "fixture-harness",
                Commit,
                options.GitDirty,
                DateTimeOffset.UnixEpoch)
            {
                GitTreeDigest = TreeDigest,
                CpuModel = "fixture-cpu",
                Memory = "fixture-memory",
                Storage = "fixture-storage",
                PowerManagement = "fixture-power"
            };
            var providers = new[]
            {
                new BenchmarkProviderMetadata(
                    BenchmarkProvider.Sqlite,
                    "fixture-provider",
                    new Dictionary<string, string> { ["mode"] = "fixture" })
            };
            var samples = role == BenchmarkExecutionRole.Measured
                ? CreateSamples(options, options.Workload)
                : [];
            var forgeUnavailableRoundTrips = options.ForgeMeasuredUnavailableRoundTrips &&
                                                role == BenchmarkExecutionRole.Measured;
            var forgeObservedCountMismatch = options.ForgeMeasuredObservedCountMismatch &&
                                               role == BenchmarkExecutionRole.Measured;
            var forgeSecondarySignalCount = options.ForgeMeasuredSecondarySignalCount &&
                                            role == BenchmarkExecutionRole.Measured;
            if (forgeUnavailableRoundTrips)
                samples = ForgeUnavailableRoundTrips(samples);
            else if (forgeObservedCountMismatch)
                samples = ForgeObservedCountMismatch(samples);
            else if (forgeSecondarySignalCount)
                samples = ForgeSecondarySignalCount(samples);
            var forgeDatabaseSignal =
                forgeUnavailableRoundTrips || forgeObservedCountMismatch || forgeSecondarySignalCount;
            var benchmarkCase = new BenchmarkCase(
                BenchmarkProvider.Sqlite,
                PhysicalStorageForm.PhysicalEntityTable,
                options.Workload);
            var report = forgeDatabaseSignal
                ? CreateForgedDatabaseSignalReport(samples, benchmarkCase, shape, workerRoot)
                : CreateReport(role, samples, benchmarkCase, shape, workerRoot);
            var runId = $"scheduled-fixture-{ordinalText}";
            var workerManifest = new BenchmarkRunManifest(
                BenchmarkProfiles.SchemaVersion,
                runId,
                "completed",
                BenchmarkRunMode.Scheduled,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                Commit,
                options.GitDirty,
                "raw/measurements.jsonl",
                "reports/summary.json",
                "reports/elsa-migration-evidence.json",
                "metadata/machine.json",
                "metadata/providers.json",
                "metadata/configuration.json",
                [],
                BaselineRun: null,
                RegressionConfirmationRun: false,
                Failure: null,
                ConsumerEvidence: role == BenchmarkExecutionRole.Measured
                    ? "reports/consumer-evidence.json"
                    : null,
                ArtifactIntegrity: "reports/artifact-integrity.json");

            if (forgeDatabaseSignal)
            {
                await WriteForgedDatabaseSignalArtifactsAsync(
                    layout,
                    workerManifest,
                    machine,
                    providers,
                    persistedConfiguration,
                    benchmarkCase,
                    samples,
                    report,
                    independentRun);
            }
            else
            {
                await using var writer = new BenchmarkArtifactWriter(layout);
                await writer.WriteManifestAsync(workerManifest, CancellationToken.None);
                await writer.WriteMachineAsync(machine, CancellationToken.None);
                await writer.WriteProvidersAsync(providers, CancellationToken.None);
                await writer.WriteConfigurationAsync(persistedConfiguration, CancellationToken.None);
                foreach (var sample in options.TamperMeasuredRawDatabaseSignal && role == BenchmarkExecutionRole.Measured
                             ? TamperFirstSignal(samples)
                             : samples)
                    await writer.AppendSampleAsync(new RawBenchmarkRecord(benchmarkCase, sample), CancellationToken.None);
                await writer.WriteReportAsync(report, CancellationToken.None);
                if (role == BenchmarkExecutionRole.Measured)
                {
                    var consumer = BenchmarkConsumerEvidenceReport.Create(
                        report,
                        persistedConfiguration,
                        machine,
                        providers,
                        layout,
                        independentRun);
                    if (options.ForgeMeasuredConsumerResultDigest && independentRun == 1)
                    {
                        consumer = consumer with
                        {
                            Results =
                            [
                                consumer.Results[0] with { ResultDigest = new string('0', 64) }
                            ]
                        };
                    }
                    if (options.ForgeMeasuredConcurrentLoadDigest && independentRun == 1)
                    {
                        consumer = consumer with
                        {
                            Results =
                            [
                                consumer.Results[0] with { ConcurrentLoadEvidenceDigest = new string('0', 64) }
                            ]
                        };
                    }
                    await writer.WriteConsumerEvidenceAsync(consumer, CancellationToken.None);
                }
                await writer.WriteManifestAsync(workerManifest, CancellationToken.None);
                if (role != BenchmarkExecutionRole.Measured || options.WriteMeasuredIntegrityLedger)
                    await writer.WriteArtifactIntegrityAsync(workerManifest, CancellationToken.None);
            }

            var manifestPath = Path.Combine(workerRoot, "manifest.json");
            var elsaEvidencePath = Path.Combine(workerRoot, "reports", "elsa-migration-evidence.json");
            var consumerPath = role == BenchmarkExecutionRole.Measured
                ? Path.Combine(workerRoot, "reports", "consumer-evidence.json")
                : null;
            var artifacts = new BenchmarkWorkerArtifactDigests(
                "manifest.json",
                Digest(manifestPath),
                "reports/elsa-migration-evidence.json",
                Digest(elsaEvidencePath),
                consumerPath is null ? null : "reports/consumer-evidence.json",
                consumerPath is null ? null : Digest(consumerPath));
            var response = new BenchmarkWorkerResponse(
                BenchmarkRunProtocol.ProtocolVersion,
                "scheduled-fixture",
                ordinal,
                role,
                Succeeded: true,
                workerRoot,
                consumerPath,
                FailureType: null)
            {
                RequestDigest = Digest(requestPath),
                GitCommit = Commit,
                GitTreeDigest = TreeDigest,
                Artifacts = artifacts
            };
            await WriteJsonAsync(responsePath, response);
            entries.Add(new BenchmarkRunGroupEntry(
                ordinal,
                independentRun,
                role,
                Relative(Root, requestPath),
                Relative(Root, responsePath),
                consumerPath is null ? null : Relative(Root, consumerPath),
                consumerPath is null ? null : Digest(consumerPath))
            {
                RequestDigest = Digest(requestPath),
                ResponseDigest = Digest(responsePath),
                WorkerManifest = Relative(Root, manifestPath),
                WorkerManifestDigest = Digest(manifestPath),
                ElsaMigrationEvidence = Relative(Root, elsaEvidencePath),
                ElsaMigrationEvidenceDigest = Digest(elsaEvidencePath)
            });
        }

        private static IReadOnlyList<BenchmarkSample> CreateSamples(
            Options options,
            BenchmarkWorkload workload) =>
            Enumerable.Range(0, options.MeasuredSampleCount)
                .Select(iteration =>
                {
                    var operations = workload == BenchmarkWorkload.ConcurrentCreate
                        ? BenchmarkProfiles.Scheduled.Concurrency
                        : options.OperationsPerSample;
                    return new BenchmarkSample(
                    iteration,
                    operations,
                    options.ElapsedNanosecondsPerSample,
                    1_000,
                    null,
                    0,
                    1,
                    null,
                    null,
                    new Dictionary<string, long>(),
                    Enumerable.Repeat(100L, operations).ToArray())
                    {
                        ConcurrentLoad = workload == BenchmarkWorkload.ConcurrentCreate
                            ? new ConcurrentLoadEvidence(
                                BenchmarkProfiles.Scheduled.Concurrency,
                                WaveCount: 1,
                                ReleasedTogetherWaveCount: 1,
                                Attempts: BenchmarkProfiles.Scheduled.Concurrency,
                                Completions: BenchmarkProfiles.Scheduled.Concurrency,
                                SuccessfulOperations: 1,
                                ConflictOperations: BenchmarkProfiles.Scheduled.Concurrency - 1,
                                PeakInFlightProductionStoreCalls: BenchmarkProfiles.Scheduled.Concurrency)
                            : null
                    };
                })
                .ToArray();

        private static IReadOnlyList<BenchmarkSample> TamperFirstSignal(IReadOnlyList<BenchmarkSample> samples) =>
            samples.Select((sample, index) => index == 0
                ? sample with
                {
                    RoundTrips = 1,
                    DatabaseSignal = new DatabaseSignalEvidence(
                        DatabaseSignalAvailability.Observed,
                        "target-scoped-diagnostic-command",
                        null,
                        1,
                        null,
                        1)
                }
                : sample).ToArray();

        private static IReadOnlyList<BenchmarkSample> ForgeUnavailableRoundTrips(IReadOnlyList<BenchmarkSample> samples) =>
            samples.Select(sample => sample with { RoundTrips = 1 }).ToArray();

        private static IReadOnlyList<BenchmarkSample> ForgeObservedCountMismatch(IReadOnlyList<BenchmarkSample> samples) =>
            samples.Select(sample => sample with
            {
                RoundTrips = 2,
                DatabaseSignal = new DatabaseSignalEvidence(
                    DatabaseSignalAvailability.Observed,
                    "target-scoped-diagnostic-command",
                    null,
                    CommandStarts: 1,
                    ClientActivities: null,
                    ObservableRoundTrips: 2)
            }).ToArray();

        private static IReadOnlyList<BenchmarkSample> ForgeSecondarySignalCount(IReadOnlyList<BenchmarkSample> samples) =>
            samples.Select(sample => sample with
            {
                RoundTrips = 1,
                DatabaseSignal = new DatabaseSignalEvidence(
                    DatabaseSignalAvailability.Observed,
                    "target-scoped-diagnostic-command",
                    null,
                    CommandStarts: 1,
                    ClientActivities: 2,
                    ObservableRoundTrips: 1)
            }).ToArray();

        private static BenchmarkRunReport CreateForgedDatabaseSignalReport(
            IReadOnlyList<BenchmarkSample> samples,
            BenchmarkCase benchmarkCase,
            BenchmarkDataShape shape,
            string workerRoot)
        {
            var operations = samples.Sum(sample => (long)sample.Operations);
            var elapsed = samples.Sum(sample => sample.ElapsedNanoseconds);
            var latencyCount = samples.Sum(sample => sample.OperationLatencyNanoseconds.Count);
            return new BenchmarkRunReport(
                BenchmarkProfiles.SchemaVersion,
                $"scheduled-fixture-{Path.GetFileName(workerRoot)}",
                BenchmarkRunMode.Scheduled,
                [new BenchmarkCaseResult(
                    benchmarkCase,
                    new CorrectnessGateResult(true, true, true, true, true),
                    [],
                    new BenchmarkCaseSummary(
                        benchmarkCase.Identity,
                        samples.Count,
                        latencyCount,
                        100,
                        100,
                        100,
                        operations * 1_000_000_000d / elapsed,
                        100,
                        samples.Sum(sample => sample.RoundTrips!.Value) / (double)operations,
                        null,
                        null,
                        null,
                        new Dictionary<string, double>()),
                    samples,
                    [new BenchmarkObservableResult(0, "fixture-result", "validated", 1, operations, null)])],
                [],
                new BaselineEligibility(false, ["fixture is non-promotable"]),
                shape);
        }

        private static async Task WriteForgedDatabaseSignalArtifactsAsync(
            ArtifactLayout layout,
            BenchmarkRunManifest manifest,
            BenchmarkMachineMetadata machine,
            IReadOnlyList<BenchmarkProviderMetadata> providers,
            BenchmarkRunConfiguration configuration,
            BenchmarkCase benchmarkCase,
            IReadOnlyList<BenchmarkSample> samples,
            BenchmarkRunReport report,
            int independentRun)
        {
            layout.CreateDirectories();
            await WriteJsonAsync(layout.Manifest, manifest);
            await WriteJsonAsync(layout.MachineMetadata, machine);
            await WriteJsonAsync(layout.ProviderMetadata, providers);
            await WriteJsonAsync(layout.Configuration, configuration);
            await File.WriteAllLinesAsync(
                layout.RawMeasurements,
                samples.Select(sample => JsonSerializer.Serialize(
                    new RawBenchmarkRecord(benchmarkCase, sample),
                    BenchmarkJson.CompactOptions)));
            await WriteJsonAsync(layout.SummaryJson, report);
            await WriteJsonAsync(layout.ElsaMigrationEvidenceJson, ElsaMigrationEvidenceReport.From(report));
            var consumer = BenchmarkConsumerEvidenceReport.Create(
                report,
                configuration,
                machine,
                providers,
                layout,
                independentRun);
            await WriteJsonAsync(layout.ConsumerEvidenceJson, consumer);

            var artifacts = new[]
            {
                layout.RelativePath(layout.Manifest),
                manifest.RawMeasurements,
                manifest.Summary,
                manifest.ElsaMigrationEvidence,
                manifest.MachineMetadata,
                manifest.ProviderMetadata,
                manifest.Configuration,
                manifest.ConsumerEvidence!
            }.Order(StringComparer.Ordinal)
             .Select(relative => new BenchmarkArtifactDigest(
                 relative,
                 Digest(Path.Combine(layout.Root, relative.Replace('/', Path.DirectorySeparatorChar)))) )
             .ToArray();
            await WriteJsonAsync(
                layout.ArtifactIntegrityJson,
                new BenchmarkArtifactIntegrity(BenchmarkArtifactIntegrity.ContractVersion, manifest.RunId, artifacts));
        }

        private static BenchmarkRunReport CreateReport(
            BenchmarkExecutionRole role,
            IReadOnlyList<BenchmarkSample> samples,
            BenchmarkCase benchmarkCase,
            BenchmarkDataShape shape,
            string workerRoot)
        {
            var cases = role == BenchmarkExecutionRole.Measured
                ? new[]
                {
                    new BenchmarkCaseResult(
                        benchmarkCase,
                        new CorrectnessGateResult(true, true, true, true, true),
                        [],
                        BenchmarkSummarizer.Summarize(benchmarkCase.Identity, samples),
                        samples,
                        [new BenchmarkObservableResult(0, "fixture-result", "validated", 1, samples.Sum(sample => sample.Operations), null)])
                }
                : [];
            return new BenchmarkRunReport(
                BenchmarkProfiles.SchemaVersion,
                $"scheduled-fixture-{Path.GetFileName(workerRoot)}",
                BenchmarkRunMode.Scheduled,
                cases,
                [],
                new BaselineEligibility(false, ["fixture is non-promotable"]),
                shape);
        }

        private static string Digest(string path) =>
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

        private static string Relative(string root, string path) =>
            Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    }
}
