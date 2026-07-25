using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Xunit;

namespace Groundwork.Tests;

public sealed class RelationshipMaterializationTransitionTests
{
    [Fact]
    public void Complete_validation_keeps_the_previous_generation_active_until_atomic_cutover()
    {
        var active = Generation("generation-active");
        var candidate = Generation("generation-candidate");

        var prepared = RelationshipMaterializationTransition.Prepare(active, candidate);
        var validated = prepared.CompleteValidation(
            RelationshipMaterializationValidationEvidence.Succeeded(candidate));
        var activated = validated.CutOver();

        Assert.Equal(RelationshipMaterializationTransitionStage.Prepared, prepared.Stage);
        Assert.Same(active, prepared.ActiveGeneration);
        Assert.Equal(RelationshipMaterializationTransitionStage.Validated, validated.Stage);
        Assert.Same(active, validated.ActiveGeneration);
        Assert.Null(validated.PreviousGeneration);
        Assert.Equal(RelationshipMaterializationTransitionStage.Activated, activated.Stage);
        Assert.Same(candidate, activated.ActiveGeneration);
        Assert.Same(active, activated.PreviousGeneration);
        Assert.Same(candidate, activated.CandidateGeneration);
    }

    [Fact]
    public void Failed_or_cancelled_validation_never_activates_the_candidate_generation()
    {
        var active = Generation("generation-active");
        var candidate = Generation("generation-candidate");
        var prepared = RelationshipMaterializationTransition.Prepare(active, candidate);
        var dangling = RelationshipMaterializationDanglingReference.Create(
            candidate,
            "tenant-a",
            "comparison-key-for-secret-reference");

        var rejected = prepared.CompleteValidation(
            RelationshipMaterializationValidationEvidence.Failed(candidate, [dangling]));
        var cancelled = RelationshipMaterializationTransition.Prepare(active, candidate).Cancel();

        Assert.Equal(RelationshipMaterializationTransitionStage.Rejected, rejected.Stage);
        Assert.Same(active, rejected.ActiveGeneration);
        Assert.Same(candidate, rejected.CandidateGeneration);
        var cutover = Assert.Throws<RelationshipMaterializationTransitionRejectedException>(() => rejected.CutOver());
        Assert.Same(rejected.ValidationEvidence, cutover.Evidence);
        Assert.Contains(RelationshipMaterializationDanglingReference.DiagnosticCode, cutover.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-a", cutover.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("comparison-key-for-secret-reference", cutover.Message, StringComparison.Ordinal);
        Assert.Equal(RelationshipMaterializationTransitionStage.Cancelled, cancelled.Stage);
        Assert.Same(active, cancelled.ActiveGeneration);
        Assert.Throws<InvalidOperationException>(() => cancelled.CutOver());
    }

    [Fact]
    public void Dangling_reference_diagnostic_is_stable_and_redacts_stored_values()
    {
        var candidate = Generation("generation-candidate");
        const string targetScope = "tenant-a";
        const string storedReference = "top-secret-reference-value";

        var first = RelationshipMaterializationDanglingReference.Create(
            candidate,
            targetScope,
            storedReference);
        var same = RelationshipMaterializationDanglingReference.Create(
            candidate,
            targetScope,
            storedReference);

        Assert.Equal(RelationshipMaterializationDanglingReference.DiagnosticCode, first.Message[..19]);
        Assert.Equal(candidate.RelationshipIdentity, first.RelationshipRouteIdentity);
        Assert.Equal(first, same);
        Assert.StartsWith("sha256:", first.TargetKeyIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain(targetScope, first.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(storedReference, first.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(targetScope, first.TargetKeyIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain(storedReference, first.TargetKeyIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_evidence_with_a_different_generation_or_fingerprint_is_rejected_before_cutover()
    {
        var active = Generation("generation-active");
        var candidate = Generation("generation-candidate");
        var prepared = RelationshipMaterializationTransition.Prepare(active, candidate);
        var differentGeneration = Generation("generation-other");
        var sameGenerationDifferentFingerprint = Generation(
            "generation-candidate",
            targetAccessPathIdentity: "reference-by-target-altered");
        var wrongGenerationDiagnostic = RelationshipMaterializationDanglingReference.Create(
            differentGeneration,
            "tenant-a",
            "comparison-key-for-secret-reference");

        Assert.Throws<ArgumentException>(() => RelationshipMaterializationValidationEvidence.Failed(
            candidate,
            [wrongGenerationDiagnostic]));
        Assert.Throws<ArgumentException>(() => prepared.CompleteValidation(
            RelationshipMaterializationValidationEvidence.Succeeded(differentGeneration)));
        Assert.Throws<ArgumentException>(() => prepared.CompleteValidation(
            RelationshipMaterializationValidationEvidence.Succeeded(sameGenerationDifferentFingerprint)));
        Assert.Same(active, prepared.ActiveGeneration);
        Assert.Equal(RelationshipMaterializationTransitionStage.Prepared, prepared.Stage);
    }

    [Fact]
    public void Illegal_lifecycle_transitions_are_rejected_and_candidate_generation_must_change()
    {
        var active = Generation("generation-active");
        var candidate = Generation("generation-candidate");
        var prepared = RelationshipMaterializationTransition.Prepare(active, candidate);

        Assert.Throws<InvalidOperationException>(() => prepared.CutOver());
        Assert.Throws<ArgumentException>(() => RelationshipMaterializationTransition.Prepare(
            active,
            Generation("generation-active", targetAccessPathIdentity: "different-shape")));
    }

    private static RelationshipMaterializationGeneration Generation(
        string generationIdentity,
        string targetAccessPathIdentity = "reference-by-target")
    {
        var reference = new PhysicalRelationshipReferenceMaterializationSchema(
            generationIdentity,
            generationIdentity,
            new PhysicalRelationshipSidecarAccessPath(
                "reference-by-source",
                isUnique: true,
                [
                    PhysicalRelationshipSidecarField.MaterializationGeneration,
                    PhysicalRelationshipSidecarField.SourceScope,
                    PhysicalRelationshipSidecarField.SourceLookupKey,
                    PhysicalRelationshipSidecarField.SourceComparisonKey
                ]),
            new PhysicalRelationshipSidecarAccessPath(
                targetAccessPathIdentity,
                isUnique: false,
                [
                    PhysicalRelationshipSidecarField.MaterializationGeneration,
                    PhysicalRelationshipSidecarField.TargetScope,
                    PhysicalRelationshipSidecarField.TargetLookupKey,
                    PhysicalRelationshipSidecarField.TargetComparisonKey
                ]));
        var fence = new PhysicalRelationshipTargetFenceSchema(
            $"fence:{generationIdentity}",
            generationIdentity,
            new PhysicalRelationshipSidecarAccessPath(
                "fence-by-target",
                isUnique: true,
                [
                    PhysicalRelationshipSidecarField.MaterializationGeneration,
                    PhysicalRelationshipSidecarField.TargetScope,
                    PhysicalRelationshipSidecarField.TargetLookupKey,
                    PhysicalRelationshipSidecarField.TargetComparisonKey
                ]));
        return new RelationshipMaterializationGeneration(new PhysicalRelationshipMaterializationSchema(
            "token-authorization",
            generationIdentity,
            reference,
            fence));
    }
}
