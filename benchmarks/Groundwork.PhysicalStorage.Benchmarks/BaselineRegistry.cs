using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Groundwork.PhysicalStorage.Benchmarks;

/// <summary>
/// A committed registry is the only path by which a scheduled run can become a
/// Groundwork baseline. Authority is supplied independently as a trusted set of
/// reviewed main merge commits; neither a hex-shaped value nor an artifact-local
/// signature grants authority by itself.
/// </summary>
public sealed record BaselineRegistry(
    string SchemaVersion,
    IReadOnlyList<BaselineGeneration> Generations,
    IReadOnlyList<BaselineActivation> Activations)
{
    public const string ContractVersion = "groundwork.physical-storage.baseline-registry/v1";

    public static BaselineRegistry Empty { get; } = new(ContractVersion, [], []);
}

public sealed record BaselineRegistryDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    string SchemaVersion,
    string Status,
    IReadOnlyList<BaselineGeneration> Generations,
    IReadOnlyList<BaselineActivation> Activations);

public sealed record BaselineActivation(string CompatibilityTupleIdentity, string GenerationId);

public sealed record BaselineApproval(
    string MainMergeCommit,
    string ApprovedExactHeadCommit,
    string ApprovedExactHeadTreeDigest);

public sealed record BaselineGeneration(
    string Id,
    BaselineApproval Approval,
    string? SupersedesGenerationId,
    IReadOnlyList<BaselineCompatibilityTuple> CompatibilityTuples,
    BaselineContentDigest RunGroup,
    IReadOnlyList<BaselineContentDigest> ArtifactDigests,
    BaselineGenerationEvidence Evidence);

public sealed record BaselineContentDigest(string RelativePath, string Sha256);

public sealed record BaselineDigestMap(
    BaselineContentDigest Artifact,
    IReadOnlyDictionary<string, string> Digests);

public sealed class BaselineApprovalTrust
{
    private readonly HashSet<string> trustedMainMergeCommits;

    public BaselineApprovalTrust(IEnumerable<string> trustedMainMergeCommits)
    {
        ArgumentNullException.ThrowIfNull(trustedMainMergeCommits);
        this.trustedMainMergeCommits = trustedMainMergeCommits.ToHashSet(StringComparer.Ordinal);
    }

    public bool IsTrusted(string commit) => trustedMainMergeCommits.Contains(commit);
}

public static class BaselineRegistryStore
{
    public const string RelativeRegistryPath =
        "benchmarks/Groundwork.PhysicalStorage.Benchmarks/baselines/v1/baseline-index.json";

    public static async Task<BaselineRegistry> LoadCommittedAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        var path = BenchmarkArtifactPaths.ResolveExisting(root, RelativeRegistryPath, "Committed baseline registry");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var document = await JsonSerializer.DeserializeAsync<BaselineRegistryDocument>(
                           stream,
                           BenchmarkJson.Options,
                           cancellationToken)
                       ?? throw new InvalidOperationException("Committed baseline registry is null.");
        if (!document.Schema.Equals("baseline-index.schema.json", StringComparison.Ordinal))
            throw new InvalidOperationException("Committed baseline registry does not reference its versioned schema.");
        if (document.Status is not ("no-active-generations" or "active-generations"))
            throw new InvalidOperationException("Committed baseline registry has an unknown activation status.");
        if (document.Status == "no-active-generations" && document.Activations.Count != 0)
            throw new InvalidOperationException("An inactive registry status cannot name an activation.");
        if (document.Status == "active-generations" && document.Activations.Count == 0)
            throw new InvalidOperationException("An active registry status must name at least one activation.");
        var registry = new BaselineRegistry(document.SchemaVersion, document.Generations, document.Activations);
        var diagnostics = BaselineRegistryValidator.StructuralDiagnostics(registry);
        if (diagnostics.Count > 0)
            throw new InvalidOperationException(string.Join(" ", diagnostics));
        return registry;
    }
}

/// <summary>
/// The complete set of values that must not drift between a generation and a
/// scheduled selector. Source is included deliberately: a registry record is
/// exact-HEAD evidence, not a floating "latest compatible" alias.
/// </summary>
public sealed record BaselineCompatibilityTuple(
    BenchmarkProvider Provider,
    Groundwork.Core.PhysicalStorage.PhysicalStorageForm StorageForm,
    BenchmarkWorkload Workload,
    string DataShapeIdentity,
    string SourceCommit,
    string SourceTreeDigest,
    string InputProfileDigest,
    string ProviderIdentityDigest,
    string MachineFingerprint)
{
    public string SemanticIdentity => $"{Provider}/{StorageForm}/{Workload}/{DataShapeIdentity}";

    public string Identity =>
        $"{SemanticIdentity}/{SourceCommit}/{SourceTreeDigest}/{InputProfileDigest}/{ProviderIdentityDigest}/{MachineFingerprint}";
}

public sealed record BaselineGenerationEvidence(
    string ExactHeadCommit,
    string ExactHeadTreeDigest,
    bool SourceClean,
    string FixedInputProfileDigest,
    IReadOnlyDictionary<BenchmarkProvider, string> ProviderIdentityDigests,
    string MachineMetadataDigest,
    IReadOnlyList<BaselineContentDigest> RawMeasurementDigests,
    IReadOnlyList<BaselineContentDigest> SummaryDigests,
    BaselineDigestMap CorrectnessDigests,
    BaselineDigestMap ResultDigests,
    BaselineDigestMap NativePlanDigests,
    BaselineDigestMap SynchronizedContentionDigests,
    IReadOnlyList<BaselineContentDigest> RequiredRecoveryEvidenceDigests,
    bool ThreeAdversarialReviewsPassed,
    bool HostedChecksPassed);

public sealed record BaselineSelectionRequest(
    BaselineCompatibilityTuple CompatibilityTuple,
    string? DirectArtifactPath = null);

public sealed record BaselineSelection(
    bool Promotable,
    BaselineGeneration? Generation,
    IReadOnlyList<string> Diagnostics);

public static class BaselineRegistrySelector
{
    public static BaselineSelection Select(
        BaselineRegistry registry,
        BaselineSelectionRequest request,
        string artifactRoot,
        BaselineApprovalTrust approvalTrust)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(approvalTrust);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);

        if (!string.IsNullOrWhiteSpace(request.DirectArtifactPath))
        {
            return new BaselineSelection(
                false,
                null,
                ["A caller-supplied direct artifact path is diagnostic-only and cannot select a promotable scheduled baseline."]);
        }

        var diagnostics = BaselineRegistryValidator.StructuralDiagnostics(registry);
        if (diagnostics.Count > 0)
            return new BaselineSelection(false, null, diagnostics);

        var activation = registry.Activations.SingleOrDefault(item =>
            item.CompatibilityTupleIdentity.Equals(request.CompatibilityTuple.Identity, StringComparison.Ordinal));
        if (activation is null)
        {
            return new BaselineSelection(
                false,
                null,
                ["No active trusted baseline generation exists for the requested exact compatibility tuple."]);
        }

        var generation = registry.Generations.Single(item => item.Id.Equals(activation.GenerationId, StringComparison.Ordinal));
        diagnostics.AddRange(BaselineGenerationEligibilityEvaluator.Evaluate(generation, approvalTrust).Diagnostics);
        if (diagnostics.Count > 0)
            return new BaselineSelection(false, null, diagnostics);

        diagnostics.AddRange(VerifyDeclaredContent(generation, artifactRoot));
        return diagnostics.Count == 0
            ? new BaselineSelection(true, generation, [])
            : new BaselineSelection(false, null, diagnostics);
    }

    private static IReadOnlyList<string> VerifyDeclaredContent(
        BaselineGeneration generation,
        string artifactRoot)
    {
        var diagnostics = new List<string>();
        foreach (var artifact in generation.ArtifactDigests)
        {
            try
            {
                var path = BenchmarkArtifactPaths.ResolveExisting(
                    artifactRoot,
                    artifact.RelativePath,
                    "Approved baseline artifact");
                var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                if (!FixedTimeEquals(artifact.Sha256, actual))
                    diagnostics.Add($"Approved baseline artifact content digest drifted for '{artifact.RelativePath}'.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
            {
                diagnostics.Add($"Approved baseline artifact '{artifact.RelativePath}' cannot be verified: {exception.Message}");
            }
        }

        VerifyDigestMapContent(generation.Evidence.CorrectnessDigests, "correctness", artifactRoot, diagnostics);
        VerifyDigestMapContent(generation.Evidence.ResultDigests, "result", artifactRoot, diagnostics);
        VerifyDigestMapContent(generation.Evidence.NativePlanDigests, "provider-native plan", artifactRoot, diagnostics);
        VerifyDigestMapContent(
            generation.Evidence.SynchronizedContentionDigests,
            "synchronized-contention",
            artifactRoot,
            diagnostics);
        return diagnostics;
    }

    private static void VerifyDigestMapContent(
        BaselineDigestMap digestMap,
        string subject,
        string artifactRoot,
        ICollection<string> diagnostics)
    {
        try
        {
            var path = BenchmarkArtifactPaths.ResolveExisting(
                artifactRoot,
                digestMap.Artifact.RelativePath,
                $"Approved baseline {subject} digest map");
            var retained = JsonSerializer.Deserialize<IReadOnlyDictionary<string, string>>(
                               File.ReadAllBytes(path),
                               BenchmarkJson.Options)
                           ?? throw new InvalidOperationException($"{subject} digest map is null.");
            if (!SameDigestMap(retained, digestMap.Digests))
                diagnostics.Add($"Approved baseline {subject} digest map does not match its retained canonical artifact.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            diagnostics.Add($"Approved baseline {subject} digest map cannot be verified: {exception.Message}");
        }
    }

    private static bool SameDigestMap(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count &&
        left.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(pair => pair.Key, StringComparer.Ordinal));

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
}

public static class BaselineRegistryValidator
{
    /// <summary>
    /// Use this activation gate from the reviewed registry change. Trust is an
    /// external input derived from reviewed Groundwork main history.
    /// </summary>
    public static void Validate(BaselineRegistry registry, BaselineApprovalTrust approvalTrust)
    {
        ArgumentNullException.ThrowIfNull(approvalTrust);
        var diagnostics = StructuralDiagnostics(registry);
        foreach (var generation in registry.Generations)
            diagnostics.AddRange(BaselineGenerationEligibilityEvaluator.Evaluate(generation, approvalTrust).Diagnostics);
        if (diagnostics.Count > 0)
            throw new InvalidOperationException(string.Join(" ", diagnostics.Distinct(StringComparer.Ordinal)));
    }

    internal static List<string> StructuralDiagnostics(BaselineRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var diagnostics = new List<string>();
        if (!registry.SchemaVersion.Equals(BaselineRegistry.ContractVersion, StringComparison.Ordinal))
            diagnostics.Add($"Baseline registry schema must be '{BaselineRegistry.ContractVersion}'.");
        if (registry.Generations.Select(generation => generation.Id).Distinct(StringComparer.Ordinal).Count() != registry.Generations.Count ||
            registry.Generations.Any(generation => string.IsNullOrWhiteSpace(generation.Id)))
        {
            diagnostics.Add("Baseline generation identities must be non-empty and unique.");
        }

        var generationIndexes = registry.Generations
            .Select((generation, index) => (generation.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        foreach (var (generation, index) in registry.Generations.Select((generation, index) => (generation, index)))
        {
            if (generation.SupersedesGenerationId is not null &&
                (!generationIndexes.TryGetValue(generation.SupersedesGenerationId, out var predecessorIndex) ||
                 predecessorIndex >= index))
            {
                diagnostics.Add(
                    $"Baseline generation '{generation.Id}' must supersede an earlier retained generation; cycles and forward references are forbidden.");
            }
            if (!IsCommit(generation.Approval.MainMergeCommit))
                diagnostics.Add($"Baseline generation '{generation.Id}' has a malformed main-merge authority identity.");
            if (!IsCommit(generation.Approval.ApprovedExactHeadCommit) ||
                !IsSha256(generation.Approval.ApprovedExactHeadTreeDigest))
            {
                diagnostics.Add($"Baseline generation '{generation.Id}' has malformed approved exact-head provenance.");
            }
            if (generation.CompatibilityTuples.Count == 0)
                diagnostics.Add($"Baseline generation '{generation.Id}' has no compatibility tuples.");
            if (generation.CompatibilityTuples.Select(tuple => tuple.Identity).Distinct(StringComparer.Ordinal).Count() != generation.CompatibilityTuples.Count)
                diagnostics.Add($"Baseline generation '{generation.Id}' contains duplicate exact compatibility tuples.");
            if (generation.CompatibilityTuples.Select(tuple => tuple.SemanticIdentity).Distinct(StringComparer.Ordinal).Count() != generation.CompatibilityTuples.Count)
                diagnostics.Add($"Baseline generation '{generation.Id}' contains duplicate scheduled semantic tuples.");
            if (generation.ArtifactDigests.Count == 0)
                diagnostics.Add($"Baseline generation '{generation.Id}' has no immutable artifact content digests.");
            ValidateDigests(generation.ArtifactDigests, $"Baseline generation '{generation.Id}' artifact", diagnostics);
            ValidateDigest(generation.RunGroup.Sha256, $"Baseline generation '{generation.Id}' run-group", diagnostics);
            if (generation.ArtifactDigests.All(artifact => artifact != generation.RunGroup))
                diagnostics.Add($"Baseline generation '{generation.Id}' artifact digests do not bind its run-group content.");
        }

        if (registry.Activations.Select(item => item.CompatibilityTupleIdentity).Distinct(StringComparer.Ordinal).Count() != registry.Activations.Count)
            diagnostics.Add("Exactly one generation may be active for an exact compatibility tuple.");
        var tupleIdentitiesByGeneration = registry.Generations.ToDictionary(
            generation => generation.Id,
            generation => generation.CompatibilityTuples
                .Select(tuple => tuple.Identity)
                .ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var activation in registry.Activations)
        {
            if (!tupleIdentitiesByGeneration.TryGetValue(activation.GenerationId, out var tupleIdentities) ||
                !tupleIdentities.Contains(activation.CompatibilityTupleIdentity))
            {
                diagnostics.Add("Baseline activation must reference a retained generation containing its exact compatibility tuple.");
            }
        }
        return diagnostics;
    }

    internal static bool IsCommit(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);

    internal static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    internal static void ValidateDigest(string value, string subject, ICollection<string> diagnostics)
    {
        if (!IsSha256(value))
            diagnostics.Add($"{subject} digest must be a SHA-256 value.");
    }

    internal static void ValidateDigests(
        IEnumerable<BaselineContentDigest> artifacts,
        string subject,
        ICollection<string> diagnostics)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            try
            {
                _ = BenchmarkArtifactPaths.RequireCanonicalRelative(artifact.RelativePath, subject);
            }
            catch (InvalidOperationException exception)
            {
                diagnostics.Add(exception.Message);
            }
            if (!paths.Add(artifact.RelativePath))
                diagnostics.Add($"{subject} paths must be unique.");
            ValidateDigest(artifact.Sha256, subject, diagnostics);
        }
    }
}

public static class BaselineRegistryTransitionValidator
{
    public static void Validate(
        BaselineRegistry previous,
        BaselineRegistry candidate,
        BaselineApprovalTrust approvalTrust)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(candidate);
        BaselineRegistryValidator.Validate(previous, approvalTrust);
        BaselineRegistryValidator.Validate(candidate, approvalTrust);

        var diagnostics = new List<string>();
        if (candidate.Generations.Count < previous.Generations.Count)
            diagnostics.Add("Baseline generations are append-only and cannot be deleted.");
        else
        {
            for (var index = 0; index < previous.Generations.Count; index++)
            {
                if (!JsonEqual(previous.Generations[index], candidate.Generations[index]))
                {
                    diagnostics.Add(
                        $"Retained baseline generation '{previous.Generations[index].Id}' cannot be mutated or reordered.");
                    break;
                }
            }
        }

        var priorActivations = previous.Activations.ToDictionary(
            activation => activation.CompatibilityTupleIdentity,
            StringComparer.Ordinal);
        var candidateActivations = candidate.Activations.ToDictionary(
            activation => activation.CompatibilityTupleIdentity,
            StringComparer.Ordinal);
        foreach (var (tuple, priorActivation) in priorActivations)
        {
            if (!candidateActivations.TryGetValue(tuple, out var candidateActivation))
                continue;
            if (candidateActivation.GenerationId.Equals(priorActivation.GenerationId, StringComparison.Ordinal))
                continue;
            var replacement = candidate.Generations.Single(generation =>
                generation.Id.Equals(candidateActivation.GenerationId, StringComparison.Ordinal));
            if (!string.Equals(
                    replacement.SupersedesGenerationId,
                    priorActivation.GenerationId,
                    StringComparison.Ordinal))
            {
                diagnostics.Add(
                    $"Activation replacement for '{tuple}' must explicitly supersede generation '{priorActivation.GenerationId}'.");
            }
        }

        if (diagnostics.Count > 0)
            throw new InvalidOperationException(string.Join(" ", diagnostics));
    }

    private static bool JsonEqual<T>(T left, T right) =>
        CryptographicOperations.FixedTimeEquals(
            JsonSerializer.SerializeToUtf8Bytes(left, BenchmarkJson.CompactOptions),
            JsonSerializer.SerializeToUtf8Bytes(right, BenchmarkJson.CompactOptions));
}

public static class BaselineGenerationEligibilityEvaluator
{
    public static BaselineEligibility Evaluate(
        BaselineGeneration generation,
        BaselineApprovalTrust approvalTrust)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(approvalTrust);
        var evidence = generation.Evidence;
        var diagnostics = new List<string>();
        if (!approvalTrust.IsTrusted(generation.Approval.MainMergeCommit))
            diagnostics.Add("Baseline activation authority is not a trusted reviewed Groundwork main merge commit.");
        if (!string.Equals(generation.Approval.ApprovedExactHeadCommit, evidence.ExactHeadCommit, StringComparison.Ordinal) ||
            !string.Equals(generation.Approval.ApprovedExactHeadTreeDigest, evidence.ExactHeadTreeDigest, StringComparison.Ordinal))
        {
            diagnostics.Add("Baseline approval authority is not bound to the generation's exact-head evidence.");
        }
        if (!evidence.SourceClean)
            diagnostics.Add("Baseline activation requires a clean exact-HEAD source identity.");
        if (!BaselineRegistryValidator.IsCommit(evidence.ExactHeadCommit))
            diagnostics.Add("Baseline activation requires an exact 40-character source commit.");
        BaselineRegistryValidator.ValidateDigest(evidence.ExactHeadTreeDigest, "Baseline exact-head tree", diagnostics);
        BaselineRegistryValidator.ValidateDigest(evidence.FixedInputProfileDigest, "Baseline fixed workload/profile input", diagnostics);
        BaselineRegistryValidator.ValidateDigest(evidence.MachineMetadataDigest, "Baseline machine metadata", diagnostics);
        if (!evidence.ThreeAdversarialReviewsPassed)
            diagnostics.Add("Baseline activation requires three adversarial exact-range reviews.");
        if (!evidence.HostedChecksPassed)
            diagnostics.Add("Baseline activation requires passing hosted checks.");

        var expected = BaselineMatrixRequirement.CreateClosedScheduledTuples(
            evidence.ExactHeadCommit,
            evidence.ExactHeadTreeDigest,
            evidence.FixedInputProfileDigest,
            evidence.ProviderIdentityDigests,
            evidence.MachineMetadataDigest);
        var actual = generation.CompatibilityTuples.ToDictionary(tuple => tuple.SemanticIdentity, StringComparer.Ordinal);
        if (actual.Count != generation.CompatibilityTuples.Count ||
            actual.Count != expected.Count ||
            !actual.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected.Keys))
        {
            diagnostics.Add("Baseline activation requires the complete closed exact-HEAD matrix with no missing or extra compatibility tuple.");
        }
        else if (expected.Any(pair => actual[pair.Key] != pair.Value))
            diagnostics.Add("Baseline compatibility identity drifted from the closed exact-head matrix.");

        var expectedProviderSet = Enum.GetValues<BenchmarkProvider>().ToHashSet();
        if (!expectedProviderSet.SetEquals(evidence.ProviderIdentityDigests.Keys) ||
            evidence.ProviderIdentityDigests.Values.Any(value => !BaselineRegistryValidator.IsSha256(value)))
        {
            diagnostics.Add("Baseline activation requires immutable image/version/effective-setting identities for every provider.");
        }
        RequireContent(generation.ArtifactDigests, evidence.RawMeasurementDigests, "raw measurement", diagnostics);
        RequireContent(generation.ArtifactDigests, evidence.SummaryDigests, "summary", diagnostics);
        RequireContent(generation.ArtifactDigests, evidence.RequiredRecoveryEvidenceDigests, "required recovery", diagnostics);
        BaselineRegistryValidator.ValidateDigests(evidence.RawMeasurementDigests, "Baseline raw measurement", diagnostics);
        BaselineRegistryValidator.ValidateDigests(evidence.SummaryDigests, "Baseline summary", diagnostics);
        BaselineRegistryValidator.ValidateDigests(evidence.RequiredRecoveryEvidenceDigests, "Baseline recovery evidence", diagnostics);

        var expectedIdentities = generation.CompatibilityTuples
            .Select(tuple => tuple.SemanticIdentity)
            .ToHashSet(StringComparer.Ordinal);
        RequireDigestMap(generation.ArtifactDigests, evidence.CorrectnessDigests, expectedIdentities, "correctness", diagnostics);
        RequireDigestMap(generation.ArtifactDigests, evidence.ResultDigests, expectedIdentities, "result", diagnostics);
        RequireDigestMap(generation.ArtifactDigests, evidence.NativePlanDigests, expectedIdentities, "provider-native plan", diagnostics);
        var contentionIdentities = generation.CompatibilityTuples
            .Where(tuple => tuple.Workload == BenchmarkWorkload.ConcurrentCreate)
            .Select(tuple => tuple.SemanticIdentity)
            .ToHashSet(StringComparer.Ordinal);
        RequireDigestMap(
            generation.ArtifactDigests,
            evidence.SynchronizedContentionDigests,
            contentionIdentities,
            "synchronized-contention",
            diagnostics);

        return new BaselineEligibility(diagnostics.Count == 0, diagnostics);
    }

    private static void RequireContent(
        IReadOnlyList<BaselineContentDigest> all,
        IReadOnlyList<BaselineContentDigest> required,
        string subject,
        ICollection<string> diagnostics)
    {
        if (required.Count == 0 || required.Any(item => !all.Contains(item)))
            diagnostics.Add($"Baseline activation requires immutable {subject} artifact content digests.");
    }

    private static void RequireDigestMap(
        IReadOnlyList<BaselineContentDigest> artifacts,
        BaselineDigestMap actual,
        IReadOnlySet<string> expected,
        string subject,
        ICollection<string> diagnostics)
    {
        if (!artifacts.Contains(actual.Artifact))
            diagnostics.Add($"Baseline activation requires the {subject} digest map to bind a retained canonical artifact.");
        BaselineRegistryValidator.ValidateDigests([actual.Artifact], $"Baseline {subject} digest map", diagnostics);
        if (!expected.SetEquals(actual.Digests.Keys) ||
            actual.Digests.Values.Any(value => !BaselineRegistryValidator.IsSha256(value)))
        {
            diagnostics.Add($"Baseline activation requires one immutable {subject} digest for every exact required tuple and no extras.");
        }
        var retainedDigests = artifacts.Select(artifact => artifact.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (actual.Digests.Values.Any(value => !retainedDigests.Contains(value)))
        {
            diagnostics.Add(
                $"Baseline activation requires every {subject} digest-map value to bind retained artifact content.");
        }
    }
}

public static class BaselineMatrixRequirement
{
    public static IReadOnlyDictionary<string, BaselineCompatibilityTuple> CreateClosedScheduledTuples(
        string sourceCommit,
        string sourceTreeDigest,
        string inputProfileDigest,
        IReadOnlyDictionary<BenchmarkProvider, string> providerIdentityDigests,
        string machineFingerprint)
    {
        var tuples = new Dictionary<string, BaselineCompatibilityTuple>(StringComparer.Ordinal);
        foreach (var provider in Enum.GetValues<BenchmarkProvider>())
        {
            if (!providerIdentityDigests.TryGetValue(provider, out var providerIdentityDigest))
                continue;
            foreach (var form in Enum.GetValues<Groundwork.Core.PhysicalStorage.PhysicalStorageForm>())
            {
                foreach (var workload in Enum.GetValues<BenchmarkWorkload>())
                {
                    foreach (var dataShape in BenchmarkProfiles.ScheduledDimensions.CreateShapes(workload))
                    {
                        var tuple = new BaselineCompatibilityTuple(
                            provider,
                            form,
                            workload,
                            dataShape.Identity,
                            sourceCommit,
                            sourceTreeDigest,
                            inputProfileDigest,
                            providerIdentityDigest,
                            machineFingerprint);
                        tuples.Add(tuple.SemanticIdentity, tuple);
                    }
                }
            }
        }
        return tuples;
    }
}
