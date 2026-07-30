using System.Text.Json;
using System.Reflection;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Xunit;

namespace Groundwork.Tests;

public sealed class RelationshipMaterializationTransitionTests
{
    private static readonly byte[] ProviderOwnedCorrelationKey =
        Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public void Transition_requirement_binds_a_closed_expected_active_state_and_candidate_without_activating_them()
    {
        var active = Generation("generation-active");
        var candidate = Generation("generation-candidate");

        var requirement = new RelationshipMaterializationTransitionRequirement(
            RelationshipMaterializationExpectedActive.Exact(active),
            candidate);

        Assert.Same(active, requirement.ExpectedActive.ExactGeneration);
        Assert.False(requirement.ExpectedActive.IsAbsent);
        Assert.Same(candidate, requirement.CandidateGeneration);
        Assert.Throws<ArgumentException>(() => new RelationshipMaterializationTransitionRequirement(
            RelationshipMaterializationExpectedActive.Exact(active),
            Generation("generation-active", targetAccessPathIdentity: "different-shape")));
        Assert.Throws<ArgumentException>(() => new RelationshipMaterializationTransitionRequirement(
            RelationshipMaterializationExpectedActive.Exact(active),
            Generation("generation-candidate", relationshipIdentity: "different-route")));
    }

    [Fact]
    public void Inaugural_transition_requires_an_explicit_expected_absent_state()
    {
        var candidate = Generation("generation-candidate");

        var transition = new RelationshipMaterializationTransitionRequirement(
            RelationshipMaterializationExpectedActive.Absent,
            candidate);

        Assert.True(transition.ExpectedActive.IsAbsent);
        Assert.Null(transition.ExpectedActive.ExactGeneration);
        Assert.Same(candidate, transition.CandidateGeneration);
    }

    [Fact]
    public void Dangling_reference_diagnostic_binds_the_transition_candidate_and_uses_canonical_framing()
    {
        var active = Generation("generation-active");
        var candidate = Generation("generation-candidate");
        var transition = new RelationshipMaterializationTransitionRequirement(
            RelationshipMaterializationExpectedActive.Exact(active), candidate);
        var targetKey = KeyCorrelationIdentity(transition, 'a');

        var first = new RelationshipMaterializationDanglingReference(transition, targetKey);
        var same = new RelationshipMaterializationDanglingReference(
            transition,
            KeyCorrelationIdentity(transition, 'a'));

        Assert.Equal(first, same);
        Assert.Equal(first.CanonicalJson, same.CanonicalJson);
        Assert.Same(transition, first.TransitionRequirement);
        Assert.Same(candidate, first.CandidateGeneration);
        Assert.Equal(
            typeof(RelationshipMaterializationTransitionRequirement),
            Assert.Single(typeof(RelationshipMaterializationDanglingReference).GetConstructors(
                    BindingFlags.Instance | BindingFlags.NonPublic))
                .GetParameters()[0]
                .ParameterType);
        Assert.Equal(candidate.RelationshipIdentity, first.RelationshipRouteIdentity);
        using var document = JsonDocument.Parse(first.CanonicalJson);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(RelationshipMaterializationDanglingReference.DiagnosticCode, root.GetProperty("code").GetString());
        Assert.Equal(candidate.RelationshipIdentity, root.GetProperty("relationshipRoute").GetString());
        Assert.Equal(candidate.GenerationIdentity, root.GetProperty("generation").GetString());
        Assert.Equal(candidate.MaterializationFingerprint, root.GetProperty("materializationFingerprint").GetString());
        Assert.Equal(targetKey.Value, root.GetProperty("targetKeyCorrelationIdentity").GetString());
    }

    [Fact]
    public void Key_correlation_identity_uses_the_normative_bound_hmac_derivation()
    {
        var transition = new RelationshipMaterializationTransitionRequirement(
            RelationshipMaterializationExpectedActive.Exact(Generation("generation-active")),
            Generation("generation-candidate"));

        var correlation = RelationshipMaterializationKeyCorrelationIdentity.Create(
            ProviderOwnedCorrelationKey,
            transition,
            "scope:tenant-a",
            "comparison-key:authorization-42");

        Assert.Equal(
            "hmac-sha256-v1:369f8d0d430a4acc8779749445e5a928a4ea0f439f51223a0ed7456e235cf464",
            correlation.Value);
        Assert.Empty(typeof(RelationshipMaterializationKeyCorrelationIdentity).GetConstructors());
        Assert.DoesNotContain(
            typeof(RelationshipMaterializationKeyCorrelationIdentity)
                .GetMethods(BindingFlags.Public | BindingFlags.Static),
            method => method.Name == "Create");
        Assert.Empty(typeof(RelationshipMaterializationDanglingReference).GetConstructors());
        Assert.Throws<ArgumentException>(() =>
            RelationshipMaterializationKeyCorrelationIdentity.Create(
                new byte[31],
                transition,
                "scope:tenant-a",
                "comparison-key:authorization-42"));
        Assert.Throws<ArgumentException>(() =>
            RelationshipMaterializationKeyCorrelationIdentity.Create(
                ProviderOwnedCorrelationKey,
                transition,
                " ",
                "comparison-key:authorization-42"));
        Assert.Throws<ArgumentException>(() =>
            RelationshipMaterializationKeyCorrelationIdentity.Create(
                ProviderOwnedCorrelationKey,
                transition,
                "scope:tenant-a",
                string.Empty));

        var otherCandidateTransition = new RelationshipMaterializationTransitionRequirement(
            RelationshipMaterializationExpectedActive.Exact(Generation("generation-active")),
            Generation("generation-other-candidate"));
        var relabelled = RelationshipMaterializationKeyCorrelationIdentity.Create(
            ProviderOwnedCorrelationKey,
            otherCandidateTransition,
            "scope:tenant-a",
            "comparison-key:authorization-42");

        Assert.NotEqual(correlation, relabelled);
        Assert.Throws<ArgumentException>(() =>
            new RelationshipMaterializationDanglingReference(transition, relabelled));

        var diagnostic = new RelationshipMaterializationDanglingReference(transition, correlation);
        Assert.DoesNotContain("scope:tenant-a", correlation.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("comparison-key:authorization-42", correlation.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("scope:tenant-a", diagnostic.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("comparison-key:authorization-42", diagnostic.CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_diagnostic_framing_preserves_route_delimiters_without_aliasing()
    {
        const string delimiterBearingRoute = "token\u001e\"authorization\nroute";
        var delimitedTransition = new RelationshipMaterializationTransitionRequirement(
            RelationshipMaterializationExpectedActive.Exact(Generation("generation-active", relationshipIdentity: delimiterBearingRoute)),
            Generation("generation-candidate", relationshipIdentity: delimiterBearingRoute));
        var delimited = new RelationshipMaterializationDanglingReference(
            delimitedTransition,
            KeyCorrelationIdentity(delimitedTransition, 'b'));
        var ordinaryTransition = new RelationshipMaterializationTransitionRequirement(
            RelationshipMaterializationExpectedActive.Exact(Generation("generation-active", relationshipIdentity: "token")),
            Generation("generation-candidate", relationshipIdentity: "token"));
        var ordinary = new RelationshipMaterializationDanglingReference(
            ordinaryTransition,
            KeyCorrelationIdentity(ordinaryTransition, 'b'));

        Assert.NotEqual(delimited.CanonicalJson, ordinary.CanonicalJson);
        using var document = JsonDocument.Parse(delimited.CanonicalJson);
        Assert.Equal(
            delimiterBearingRoute,
            document.RootElement.GetProperty("relationshipRoute").GetString());
        Assert.DoesNotContain("\n", delimited.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001e", delimited.CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_identity_construction_rejects_lone_surrogates_before_fingerprinting_or_serialization()
    {
        AssertSchemaIdentityRejected(new string('\uD800', 1));
        AssertSchemaIdentityRejected(new string('\uDC00', 1));
    }

    private static void AssertSchemaIdentityRejected(string invalidIdentity)
    {
        var valid = Generation("generation-valid");
        var reference = valid.Schema.Reference;
        var fence = valid.Schema.Fence;

        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipSidecarAccessPath(
            invalidIdentity,
            isUnique: true,
            reference.UniqueSourceAccessPath.Fields));
        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipReferenceMaterializationSchema(
            invalidIdentity,
            reference.GenerationIdentity,
            reference.UniqueSourceAccessPath,
            reference.TargetSeekAccessPath));
        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipTargetFenceSchema(
            invalidIdentity,
            fence.GenerationIdentity,
            fence.UniqueTargetFenceAccessPath));
        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipReferenceMaterializationSchema(
            reference.StorageIdentity,
            invalidIdentity,
            reference.UniqueSourceAccessPath,
            reference.TargetSeekAccessPath));
        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipTargetFenceSchema(
            fence.StorageIdentity,
            invalidIdentity,
            fence.UniqueTargetFenceAccessPath));
        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipMaterializationSchema(
            invalidIdentity,
            valid.GenerationIdentity,
            reference,
            fence));
        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipMaterializationSchema(
            valid.RelationshipIdentity,
            invalidIdentity,
            reference,
            fence));
    }

    [Fact]
    public void Schema_identity_serialization_preserves_valid_unicode_without_normalization()
    {
        const string relationshipIdentity = "relationship-🧱-e\u0301";
        const string generationIdentity = "generation-🧬-e\u0301";
        var schema = Generation(
            generationIdentity,
            relationshipIdentity: relationshipIdentity).Schema;

        using var document = JsonDocument.Parse(schema.CanonicalJson);
        Assert.Equal(relationshipIdentity, document.RootElement.GetProperty("relationship").GetString());
        Assert.Equal(generationIdentity, document.RootElement.GetProperty("generation").GetString());
        Assert.Equal(generationIdentity, document.RootElement
            .GetProperty("reference")
            .GetProperty("storageIdentity")
            .GetString());
        Assert.DoesNotContain("\uFFFD", schema.CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_transition_contract_exposes_no_success_receipt_or_activation_authority()
    {
        var transitionTypes = typeof(RelationshipMaterializationTransitionRequirement).Assembly
            .GetExportedTypes()
            .Where(type =>
                type.Namespace == typeof(RelationshipMaterializationTransitionRequirement).Namespace &&
                type.Name.StartsWith("RelationshipMaterialization", StringComparison.Ordinal))
            .ToArray();
        var forbiddenMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "Succeeded",
            "CompleteValidation",
            "Activate",
            "CutOver",
            "Cancel"
        };

        Assert.DoesNotContain(
            transitionTypes.SelectMany(type => type.GetMethods()),
            method => forbiddenMethods.Contains(method.Name));
        Assert.DoesNotContain(
            transitionTypes,
            type => type.Name.Contains("Success", StringComparison.Ordinal) ||
                    type.Name.Contains("Validation", StringComparison.Ordinal) ||
                    type.Name.Contains("Activation", StringComparison.Ordinal) ||
                    type.Name.Contains("Cutover", StringComparison.Ordinal) ||
                    type.Name.Contains("TransitionStage", StringComparison.Ordinal));
    }

    private static RelationshipMaterializationKeyCorrelationIdentity KeyCorrelationIdentity(
        RelationshipMaterializationTransitionRequirement transition,
        char value) =>
        RelationshipMaterializationKeyCorrelationIdentity.Create(
            ProviderOwnedCorrelationKey,
            transition,
            "scope:tenant-a",
            $"comparison-key:{value}");

    private static RelationshipMaterializationGeneration Generation(
        string generationIdentity,
        string targetAccessPathIdentity = "reference-by-target",
        string relationshipIdentity = "token-authorization")
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
            relationshipIdentity,
            generationIdentity,
            reference,
            fence));
    }
}
