using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Core.SchemaEvolution;

/// <summary>The closed lifecycle of a relationship materialization generation transition.</summary>
public enum RelationshipMaterializationTransitionStage
{
    Prepared,
    Validated,
    Rejected,
    Cancelled,
    Activated
}

/// <summary>
/// One exact generated relationship materialization generation. The generation and fingerprint are
/// captured together from the generated schema so a provider cannot validate or activate a
/// different physical shape under the same relationship route.
/// </summary>
public sealed class RelationshipMaterializationGeneration : IEquatable<RelationshipMaterializationGeneration>
{
    public RelationshipMaterializationGeneration(PhysicalRelationshipMaterializationSchema schema)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        RelationshipIdentity = schema.RelationshipIdentity;
        GenerationIdentity = schema.GenerationIdentity;
        MaterializationFingerprint = schema.Fingerprint;
    }

    public PhysicalRelationshipMaterializationSchema Schema { get; }

    public string RelationshipIdentity { get; }

    public string GenerationIdentity { get; }

    public string MaterializationFingerprint { get; }

    public bool Equals(RelationshipMaterializationGeneration? other) =>
        other is not null &&
        string.Equals(RelationshipIdentity, other.RelationshipIdentity, StringComparison.Ordinal) &&
        string.Equals(GenerationIdentity, other.GenerationIdentity, StringComparison.Ordinal) &&
        string.Equals(MaterializationFingerprint, other.MaterializationFingerprint, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as RelationshipMaterializationGeneration);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(RelationshipIdentity),
        StringComparer.Ordinal.GetHashCode(GenerationIdentity),
        StringComparer.Ordinal.GetHashCode(MaterializationFingerprint));
}

/// <summary>
/// A redacted, deterministic validation failure for a reference whose target does not exist in a
/// legacy store. It deliberately exposes only the relationship route and a one-way target-key
/// identity, never the stored reference, target scope, or comparison key.
/// </summary>
public sealed class RelationshipMaterializationDanglingReference :
    IEquatable<RelationshipMaterializationDanglingReference>
{
    public const string DiagnosticCode = "GW-RELATIONSHIP-013";

    private RelationshipMaterializationDanglingReference(
        RelationshipMaterializationGeneration generation,
        string targetKeyIdentity)
    {
        Generation = generation;
        RelationshipRouteIdentity = generation.RelationshipIdentity;
        TargetKeyIdentity = targetKeyIdentity;
    }

    public RelationshipMaterializationGeneration Generation { get; }

    public string RelationshipRouteIdentity { get; }

    /// <summary>
    /// A domain-separated SHA-256 identity for the target key. This is stable for the same route,
    /// generation, materialization, scope, and projected comparison key, but does not contain the
    /// stored reference value.
    /// </summary>
    public string TargetKeyIdentity { get; }

    public string Message =>
        $"{DiagnosticCode}: Relationship route '{RelationshipRouteIdentity}' has a legacy dangling reference at target key '{TargetKeyIdentity}'.";

    public static RelationshipMaterializationDanglingReference Create(
        RelationshipMaterializationGeneration generation,
        string targetScope,
        string targetComparisonKey)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetComparisonKey);
        var canonical = string.Concat(
            "groundwork.relationship.dangling-reference.v1\u001e",
            generation.RelationshipIdentity, "\u001e",
            generation.GenerationIdentity, "\u001e",
            generation.MaterializationFingerprint, "\u001e",
            targetScope, "\u001e",
            targetComparisonKey);
        var targetKeyIdentity = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new RelationshipMaterializationDanglingReference(generation, targetKeyIdentity);
    }

    public bool Equals(RelationshipMaterializationDanglingReference? other) =>
        other is not null &&
        Equals(Generation, other.Generation) &&
        string.Equals(TargetKeyIdentity, other.TargetKeyIdentity, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as RelationshipMaterializationDanglingReference);

    public override int GetHashCode() => HashCode.Combine(Generation, StringComparer.Ordinal.GetHashCode(TargetKeyIdentity));
}

/// <summary>
/// Immutable validation evidence for one exact candidate generation. A successful result means a
/// provider has finished its complete backfill and validation; a failed result currently records
/// legacy dangling references, which must block cutover.
/// </summary>
public sealed class RelationshipMaterializationValidationEvidence
{
    private RelationshipMaterializationValidationEvidence(
        RelationshipMaterializationGeneration generation,
        IReadOnlyList<RelationshipMaterializationDanglingReference> danglingReferences)
    {
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        DanglingReferences = Array.AsReadOnly((danglingReferences ?? throw new ArgumentNullException(nameof(danglingReferences)))
            .OrderBy(reference => reference.TargetKeyIdentity, StringComparer.Ordinal)
            .ToArray());
        if (DanglingReferences.Any(reference => !Equals(reference.Generation, generation)))
        {
            throw new ArgumentException(
                "Relationship materialization validation evidence must bind one exact candidate generation.",
                nameof(danglingReferences));
        }
    }

    public RelationshipMaterializationGeneration Generation { get; }

    public IReadOnlyList<RelationshipMaterializationDanglingReference> DanglingReferences { get; }

    public bool IsValid => DanglingReferences.Count == 0;

    public static RelationshipMaterializationValidationEvidence Succeeded(
        RelationshipMaterializationGeneration generation) =>
        new(generation, []);

    public static RelationshipMaterializationValidationEvidence Failed(
        RelationshipMaterializationGeneration generation,
        IReadOnlyList<RelationshipMaterializationDanglingReference> danglingReferences)
    {
        ArgumentNullException.ThrowIfNull(danglingReferences);
        if (danglingReferences.Count == 0)
            throw new ArgumentException(
                "Failed relationship materialization validation requires at least one diagnostic.",
                nameof(danglingReferences));
        return new RelationshipMaterializationValidationEvidence(generation, danglingReferences);
    }
}

/// <summary>
/// Raised when a caller tries to activate a candidate rejected by complete validation. The stable,
/// redacted diagnostics are retained so operators can identify and repair the legacy data without
/// exposing stored reference values.
/// </summary>
public sealed class RelationshipMaterializationTransitionRejectedException : InvalidOperationException
{
    public RelationshipMaterializationTransitionRejectedException(
        RelationshipMaterializationValidationEvidence evidence)
        : base(CreateMessage(evidence)) => Evidence = evidence;

    public RelationshipMaterializationValidationEvidence Evidence { get; }

    private static string CreateMessage(RelationshipMaterializationValidationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.IsValid)
            throw new ArgumentException(
                "A successful relationship materialization validation cannot reject cutover.",
                nameof(evidence));
        return string.Join("; ", evidence.DanglingReferences.Select(reference => reference.Message));
    }
}

/// <summary>
/// An immutable, provider-neutral transition for one existing relationship materialization. It is
/// only a contract for provider executors: it neither admits a provider nor performs schema or
/// document I/O. A new generation cannot become active until complete evidence for that exact
/// generation validates successfully.
/// </summary>
public sealed class RelationshipMaterializationTransition
{
    private RelationshipMaterializationTransition(
        RelationshipMaterializationGeneration activeGeneration,
        RelationshipMaterializationGeneration candidateGeneration,
        RelationshipMaterializationTransitionStage stage,
        RelationshipMaterializationValidationEvidence? validationEvidence,
        RelationshipMaterializationGeneration? previousGeneration)
    {
        ActiveGeneration = activeGeneration;
        CandidateGeneration = candidateGeneration;
        Stage = stage;
        ValidationEvidence = validationEvidence;
        PreviousGeneration = previousGeneration;
    }

    /// <summary>The generation serving ordinary relationship checks at this lifecycle point.</summary>
    public RelationshipMaterializationGeneration ActiveGeneration { get; }

    /// <summary>The independently materialized generation proposed for future activation.</summary>
    public RelationshipMaterializationGeneration CandidateGeneration { get; }

    public RelationshipMaterializationTransitionStage Stage { get; }

    public RelationshipMaterializationValidationEvidence? ValidationEvidence { get; }

    /// <summary>The generation replaced by a successful cutover, retained as transition evidence.</summary>
    public RelationshipMaterializationGeneration? PreviousGeneration { get; }

    public static RelationshipMaterializationTransition Prepare(
        RelationshipMaterializationGeneration activeGeneration,
        RelationshipMaterializationGeneration candidateGeneration)
    {
        ArgumentNullException.ThrowIfNull(activeGeneration);
        ArgumentNullException.ThrowIfNull(candidateGeneration);
        if (!string.Equals(
                activeGeneration.RelationshipIdentity,
                candidateGeneration.RelationshipIdentity,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Relationship materialization transitions require the same relationship route.",
                nameof(candidateGeneration));
        }
        if (string.Equals(
                activeGeneration.GenerationIdentity,
                candidateGeneration.GenerationIdentity,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Relationship materialization transitions require a new generation identity.",
                nameof(candidateGeneration));
        }

        return new RelationshipMaterializationTransition(
            activeGeneration,
            candidateGeneration,
            RelationshipMaterializationTransitionStage.Prepared,
            null,
            null);
    }

    /// <summary>
    /// Records the outcome of the candidate's complete backfill validation. The active generation
    /// is intentionally unchanged for both valid and rejected evidence.
    /// </summary>
    public RelationshipMaterializationTransition CompleteValidation(
        RelationshipMaterializationValidationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        RequireStage(RelationshipMaterializationTransitionStage.Prepared, "complete validation");
        if (!Equals(CandidateGeneration, evidence.Generation))
        {
            throw new ArgumentException(
                "Relationship materialization validation evidence does not bind the exact candidate generation and fingerprint.",
                nameof(evidence));
        }

        return new RelationshipMaterializationTransition(
            ActiveGeneration,
            CandidateGeneration,
            evidence.IsValid
                ? RelationshipMaterializationTransitionStage.Validated
                : RelationshipMaterializationTransitionStage.Rejected,
            evidence,
            null);
    }

    /// <summary>
    /// Records cancellation before cutover. Because instances are immutable, cancellation cannot
    /// alter the generation that remains active for ordinary relationship maintenance.
    /// </summary>
    public RelationshipMaterializationTransition Cancel()
    {
        RequireStage(RelationshipMaterializationTransitionStage.Prepared, "cancel");
        return new RelationshipMaterializationTransition(
            ActiveGeneration,
            CandidateGeneration,
            RelationshipMaterializationTransitionStage.Cancelled,
            null,
            null);
    }

    /// <summary>
    /// Atomically advances the contract to the validated candidate. Provider executors must make
    /// their physical activation match this state change; rejected or incomplete candidates cannot
    /// call this method.
    /// </summary>
    public RelationshipMaterializationTransition CutOver()
    {
        if (Stage == RelationshipMaterializationTransitionStage.Rejected)
            throw new RelationshipMaterializationTransitionRejectedException(ValidationEvidence!);
        RequireStage(RelationshipMaterializationTransitionStage.Validated, "cut over");
        return new RelationshipMaterializationTransition(
            CandidateGeneration,
            CandidateGeneration,
            RelationshipMaterializationTransitionStage.Activated,
            ValidationEvidence,
            ActiveGeneration);
    }

    private void RequireStage(RelationshipMaterializationTransitionStage expected, string operation)
    {
        if (Stage != expected)
        {
            throw new InvalidOperationException(
                $"Relationship materialization transition cannot {operation} while in '{Stage}' state; '{expected}' is required.");
        }
    }
}
