using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Groundwork.Core.PhysicalStorage;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BaselineRegistryTests
{
    [Fact]
    public async Task Committed_empty_registry_loads_as_truthful_non_promotable_state()
    {
        var repositoryRoot = FindRepositoryRoot();

        var registry = await BaselineRegistryStore.LoadCommittedAsync(repositoryRoot, CancellationToken.None);

        Assert.Equal(BaselineRegistry.ContractVersion, registry.SchemaVersion);
        Assert.Empty(registry.Generations);
        Assert.Empty(registry.Activations);
        var selection = BaselineRegistrySelector.Select(
            registry,
            new BaselineSelectionRequest(Tuple()),
            repositoryRoot,
            Trust());
        Assert.False(selection.Promotable);
        Assert.Contains(selection.Diagnostics, diagnostic => diagnostic.Contains("No active", StringComparison.Ordinal));
    }

    [Fact]
    public void Direct_artifact_path_is_diagnostic_and_never_promotable()
    {
        var result = BaselineRegistrySelector.Select(
            BaselineRegistry.Empty,
            new BaselineSelectionRequest(Tuple(), DirectArtifactPath: "artifacts/unreviewed"),
            Path.GetTempPath(),
            Trust());

        Assert.False(result.Promotable);
        Assert.Null(result.Generation);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("direct", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Selection_rejects_run_group_content_drift()
    {
        using var fixture = new ArtifactFixture();
        var generation = Generation(fixture, "generation-1", ApprovalCommit);
        fixture.Write("generation-1/run-group.json", "drifted-content");

        var result = Select(Registry(generation), fixture);

        Assert.False(result.Promotable);
        Assert.Null(result.Generation);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("content digest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Selection_rejects_mapped_claim_artifact_drift()
    {
        using var fixture = new ArtifactFixture();
        var generation = Generation(fixture, "generation-1", ApprovalCommit);
        fixture.Write("generation-1/claims/correctness.json", "{}");

        var result = Select(Registry(generation), fixture);

        Assert.False(result.Promotable);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("correctness", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("content digest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Selection_rejects_mapped_claim_that_disagrees_with_its_verified_artifact()
    {
        using var fixture = new ArtifactFixture();
        var generation = Generation(fixture, "generation-1", ApprovalCommit);
        var originalMap = generation.Evidence.CorrectnessDigests;
        var replacementArtifact = fixture.WriteDigestMap(originalMap.Artifact.RelativePath, new Dictionary<string, string>());
        generation = generation with
        {
            ArtifactDigests = generation.ArtifactDigests
                .Select(artifact => artifact == originalMap.Artifact ? replacementArtifact.Artifact : artifact)
                .ToArray(),
            Evidence = generation.Evidence with
            {
                CorrectnessDigests = originalMap with { Artifact = replacementArtifact.Artifact }
            }
        };

        var result = Select(Registry(generation), fixture);

        Assert.False(result.Promotable);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("correctness digest map", StringComparison.OrdinalIgnoreCase) &&
            diagnostic.Contains("does not match", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Selection_rejects_mapped_claim_without_a_retained_artifact_binding()
    {
        using var fixture = new ArtifactFixture();
        var generation = Generation(fixture, "generation-1", ApprovalCommit);
        generation = generation with
        {
            ArtifactDigests = generation.ArtifactDigests
                .Where(artifact => artifact != generation.Evidence.CorrectnessDigests.Artifact)
                .ToArray()
        };

        var result = Select(Registry(generation), fixture);

        Assert.False(result.Promotable);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("correctness digest map", StringComparison.OrdinalIgnoreCase) &&
            diagnostic.Contains("retained canonical artifact", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Selection_rejects_mapped_claim_digest_without_retained_content()
    {
        using var fixture = new ArtifactFixture();
        var generation = Generation(fixture, "generation-1", ApprovalCommit);
        var originalMap = generation.Evidence.CorrectnessDigests;
        var unretained = Sha256("unretained-correctness-content");
        generation = generation with
        {
            Evidence = generation.Evidence with
            {
                CorrectnessDigests = originalMap with
                {
                    Digests = originalMap.Digests.ToDictionary(
                        pair => pair.Key,
                        _ => unretained,
                        StringComparer.Ordinal)
                }
            }
        };

        var result = Select(Registry(generation), fixture);

        Assert.False(result.Promotable);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("correctness digest-map value", StringComparison.OrdinalIgnoreCase) &&
            diagnostic.Contains("retained artifact content", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Registry_rejects_two_activations_for_the_same_exact_tuple()
    {
        using var fixture = new ArtifactFixture();
        var generation = Generation(fixture, "generation-1", ApprovalCommit);
        var activation = new BaselineActivation(Tuple().Identity, generation.Id);
        var registry = Registry(generation) with { Activations = [activation, activation] };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            BaselineRegistryValidator.Validate(registry, Trust()));

        Assert.Contains("Exactly one generation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Incomplete_recovery_evidence_fails_closed()
    {
        using var fixture = new ArtifactFixture();
        var generation = Generation(fixture, "generation-1", ApprovalCommit);
        generation = generation with
        {
            Evidence = generation.Evidence with { RequiredRecoveryEvidenceDigests = [] }
        };

        var result = Select(Registry(generation), fixture);

        Assert.False(result.Promotable);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("recovery", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_or_extra_closed_matrix_tuple_fails_before_activation()
    {
        using var fixture = new ArtifactFixture();
        var complete = Generation(fixture, "generation-1", ApprovalCommit);
        var incomplete = complete with
        {
            CompatibilityTuples = complete.CompatibilityTuples
                .Take(complete.CompatibilityTuples.Count - 1)
                .ToArray()
        };

        var result = Select(Registry(incomplete), fixture);

        Assert.False(result.Promotable);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("complete closed exact-HEAD matrix", StringComparison.Ordinal));
    }

    [Fact]
    public void Plausible_but_untrusted_main_commit_is_not_authority()
    {
        using var fixture = new ArtifactFixture();
        var generation = Generation(fixture, "generation-1", UntrustedCommit);

        var result = BaselineRegistrySelector.Select(
            Registry(generation),
            new BaselineSelectionRequest(Tuple()),
            fixture.Root,
            Trust());

        Assert.False(result.Promotable);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("not a trusted reviewed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Approval_authority_must_bind_the_exact_head_evidence()
    {
        using var fixture = new ArtifactFixture();
        var generation = Generation(fixture, "generation-1", ApprovalCommit);
        generation = generation with
        {
            Approval = generation.Approval with { ApprovedExactHeadTreeDigest = Sha256("different-tree") }
        };

        var result = Select(Registry(generation), fixture);

        Assert.False(result.Promotable);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("not bound", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Append_only_successor_preserves_history_and_explicitly_replaces_activations()
    {
        using var fixture = new ArtifactFixture();
        var initial = Generation(fixture, "generation-1", ApprovalCommit);
        var successor = Generation(
            fixture,
            "generation-2",
            ApprovalCommit2,
            supersedesGenerationId: initial.Id);
        var previous = Registry(initial);
        var candidate = new BaselineRegistry(
            BaselineRegistry.ContractVersion,
            [initial, successor],
            Activations(successor));

        BaselineRegistryTransitionValidator.Validate(previous, candidate, Trust());
        var result = Select(candidate, fixture);

        Assert.True(result.Promotable);
        Assert.Same(successor, result.Generation);
        Assert.Equal(2, candidate.Generations.Count);
    }

    [Fact]
    public void Transition_rejects_deleted_or_mutated_prior_generation()
    {
        using var fixture = new ArtifactFixture();
        var initial = Generation(fixture, "generation-1", ApprovalCommit);
        var previous = Registry(initial);
        var deleted = BaselineRegistry.Empty;
        var mutated = Registry(initial with
        {
            Approval = initial.Approval with { MainMergeCommit = ApprovalCommit2 }
        });

        var deletedException = Assert.Throws<InvalidOperationException>(() =>
            BaselineRegistryTransitionValidator.Validate(previous, deleted, Trust()));
        var mutatedException = Assert.Throws<InvalidOperationException>(() =>
            BaselineRegistryTransitionValidator.Validate(previous, mutated, Trust()));

        Assert.Contains("cannot be deleted", deletedException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be mutated", mutatedException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Transition_rejects_cycle_or_forward_supersession()
    {
        using var fixture = new ArtifactFixture();
        var first = Generation(
            fixture,
            "generation-1",
            ApprovalCommit,
            supersedesGenerationId: "generation-2");
        var second = Generation(
            fixture,
            "generation-2",
            ApprovalCommit2,
            supersedesGenerationId: first.Id);
        var registry = new BaselineRegistry(
            BaselineRegistry.ContractVersion,
            [first, second],
            Activations(second));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            BaselineRegistryValidator.Validate(registry, Trust()));

        Assert.Contains("cycles and forward references", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Transition_rejects_activation_replacement_without_explicit_supersession()
    {
        using var fixture = new ArtifactFixture();
        var initial = Generation(fixture, "generation-1", ApprovalCommit);
        var replacement = Generation(fixture, "generation-2", ApprovalCommit2);
        var previous = Registry(initial);
        var candidate = new BaselineRegistry(
            BaselineRegistry.ContractVersion,
            [initial, replacement],
            Activations(replacement));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            BaselineRegistryTransitionValidator.Validate(previous, candidate, Trust()));

        Assert.Contains("must explicitly supersede", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static BaselineSelection Select(BaselineRegistry registry, ArtifactFixture fixture) =>
        BaselineRegistrySelector.Select(
            registry,
            new BaselineSelectionRequest(Tuple()),
            fixture.Root,
            Trust());

    private static BaselineRegistry Registry(BaselineGeneration generation) =>
        new(BaselineRegistry.ContractVersion, [generation], Activations(generation));

    private static IReadOnlyList<BaselineActivation> Activations(BaselineGeneration generation) =>
        generation.CompatibilityTuples
            .Select(tuple => new BaselineActivation(tuple.Identity, generation.Id))
            .ToArray();

    private static BaselineCompatibilityTuple Tuple() => Tuples().Single(tuple =>
        tuple.Provider == BenchmarkProvider.Sqlite &&
        tuple.StorageForm == PhysicalStorageForm.SharedDocuments &&
        tuple.Workload == BenchmarkWorkload.IndexedQuery &&
        tuple.DataShapeIdentity == new BenchmarkDataShape(
            1_000,
            BenchmarkPayloadProfiles.For(BenchmarkWorkload.IndexedQuery),
            BenchmarkSelectivityPolicy.IndexedQueryAcceptanceBasisPoints).Identity);

    private static IReadOnlyList<BaselineCompatibilityTuple> Tuples() =>
        BaselineMatrixRequirement.CreateClosedScheduledTuples(
                SourceCommit,
                Sha256("tree"),
                Sha256("profile"),
                ProviderIdentities(),
                Sha256("machine"))
            .Values
            .ToArray();

    private static BaselineGeneration Generation(
        ArtifactFixture fixture,
        string id,
        string authorityCommit,
        string? supersedesGenerationId = null)
    {
        var prefix = id;
        fixture.Write($"{prefix}/run-group.json", $"run-group-{id}");
        fixture.Write($"{prefix}/raw/measurements.jsonl", $"raw-{id}");
        fixture.Write($"{prefix}/reports/summary.json", $"summary-{id}");
        fixture.Write($"{prefix}/recovery.json", $"recovery-{id}");
        var tuple = Tuple();
        var tuples = Tuples();
        fixture.Write($"{prefix}/claims/correctness-evidence.json", $"correctness-{id}");
        fixture.Write($"{prefix}/claims/result-evidence.json", $"result-{id}");
        fixture.Write($"{prefix}/claims/native-plan-evidence.json", $"plan-{id}");
        fixture.Write($"{prefix}/claims/contention-evidence.json", $"contention-{id}");
        var correctnessEvidence = fixture.Content($"{prefix}/claims/correctness-evidence.json");
        var resultEvidence = fixture.Content($"{prefix}/claims/result-evidence.json");
        var planEvidence = fixture.Content($"{prefix}/claims/native-plan-evidence.json");
        var contentionEvidence = fixture.Content($"{prefix}/claims/contention-evidence.json");
        var correctness = Digests(tuples, correctnessEvidence.Sha256);
        var results = Digests(tuples, resultEvidence.Sha256);
        var plans = Digests(tuples, planEvidence.Sha256);
        var contention = Digests(
            tuples.Where(item => item.Workload == BenchmarkWorkload.ConcurrentCreate),
            contentionEvidence.Sha256);
        var correctnessMap = fixture.WriteDigestMap($"{prefix}/claims/correctness.json", correctness);
        var resultMap = fixture.WriteDigestMap($"{prefix}/claims/results.json", results);
        var planMap = fixture.WriteDigestMap($"{prefix}/claims/native-plans.json", plans);
        var contentionMap = fixture.WriteDigestMap($"{prefix}/claims/synchronized-contention.json", contention);
        var runGroup = fixture.Content($"{prefix}/run-group.json");
        var raw = fixture.Content($"{prefix}/raw/measurements.jsonl");
        var summary = fixture.Content($"{prefix}/reports/summary.json");
        var recovery = fixture.Content($"{prefix}/recovery.json");
        return new BaselineGeneration(
            Id: id,
            Approval: new BaselineApproval(authorityCommit, tuple.SourceCommit, tuple.SourceTreeDigest),
            SupersedesGenerationId: supersedesGenerationId,
            CompatibilityTuples: tuples,
            RunGroup: runGroup,
            ArtifactDigests:
            [
                runGroup,
                raw,
                summary,
                recovery,
                correctnessEvidence,
                resultEvidence,
                planEvidence,
                contentionEvidence,
                correctnessMap.Artifact,
                resultMap.Artifact,
                planMap.Artifact,
                contentionMap.Artifact
            ],
            Evidence: new BaselineGenerationEvidence(
                ExactHeadCommit: tuple.SourceCommit,
                ExactHeadTreeDigest: tuple.SourceTreeDigest,
                SourceClean: true,
                FixedInputProfileDigest: tuple.InputProfileDigest,
                ProviderIdentityDigests: ProviderIdentities(),
                MachineMetadataDigest: tuple.MachineFingerprint,
                RawMeasurementDigests: [raw],
                SummaryDigests: [summary],
                CorrectnessDigests: correctnessMap,
                ResultDigests: resultMap,
                NativePlanDigests: planMap,
                SynchronizedContentionDigests: contentionMap,
                RequiredRecoveryEvidenceDigests: [recovery],
                ThreeAdversarialReviewsPassed: true,
                HostedChecksPassed: true));
    }

    private const string SourceCommit = "0123456789abcdef0123456789abcdef01234567";
    private const string ApprovalCommit = "89abcdef0123456789abcdef0123456789abcdef";
    private const string ApprovalCommit2 = "fedcba9876543210fedcba9876543210fedcba98";
    private const string UntrustedCommit = "1111111111111111111111111111111111111111";

    private static BaselineApprovalTrust Trust() => new([ApprovalCommit, ApprovalCommit2]);

    private static IReadOnlyDictionary<BenchmarkProvider, string> ProviderIdentities() =>
        Enum.GetValues<BenchmarkProvider>()
            .ToDictionary(provider => provider, provider => Sha256($"provider-{provider}"));

    private static IReadOnlyDictionary<string, string> Digests(
        IEnumerable<BaselineCompatibilityTuple> tuples,
        string retainedContentDigest) =>
        tuples.ToDictionary(
            tuple => tuple.SemanticIdentity,
            _ => retainedContentDigest);

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class ArtifactFixture : IDisposable
    {
        public ArtifactFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"groundwork-baseline-registry-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Write(string relativePath, string value)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
        }

        public BaselineDigestMap WriteDigestMap(
            string relativePath,
            IReadOnlyDictionary<string, string> digests)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(digests, BenchmarkJson.Options));
            return new BaselineDigestMap(Content(relativePath), digests);
        }

        public BaselineContentDigest Content(string relativePath) =>
            new(relativePath, Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, relativePath)))).ToLowerInvariant());

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".git")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root was not found.");
    }
}
