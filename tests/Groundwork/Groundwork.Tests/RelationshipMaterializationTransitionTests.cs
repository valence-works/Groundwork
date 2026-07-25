using System.Text.Json;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Xunit;

namespace Groundwork.Tests;

public sealed class RelationshipMaterializationTransitionTests
{
    [Fact]
    public void Transition_requirement_binds_exact_before_and_after_generations_without_activating_them()
    {
        var active = Generation("generation-active");
        var candidate = Generation("generation-candidate");

        var requirement = new RelationshipMaterializationTransitionRequirement(active, candidate);

        Assert.Same(active, requirement.ActiveGeneration);
        Assert.Same(candidate, requirement.CandidateGeneration);
        Assert.Throws<ArgumentException>(() => new RelationshipMaterializationTransitionRequirement(
            active,
            Generation("generation-active", targetAccessPathIdentity: "different-shape")));
        Assert.Throws<ArgumentException>(() => new RelationshipMaterializationTransitionRequirement(
            active,
            Generation("generation-candidate", relationshipIdentity: "different-route")));
    }

    [Fact]
    public void Dangling_reference_diagnostic_uses_provider_keyed_identity_and_canonical_framing()
    {
        var candidate = Generation("generation-candidate");
        var targetKey = KeyCorrelationIdentity('a');

        var first = new RelationshipMaterializationDanglingReference(candidate, targetKey);
        var same = new RelationshipMaterializationDanglingReference(
            candidate,
            new RelationshipMaterializationKeyCorrelationIdentity(targetKey.Value));

        Assert.Equal(first, same);
        Assert.Equal(first.CanonicalJson, same.CanonicalJson);
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
    public void Low_entropy_raw_unkeyed_and_delimiter_bearing_key_values_are_rejected()
    {
        var invalid = new[]
        {
            "1",
            "secret",
            "sha256:" + new string('a', 64),
            RelationshipMaterializationKeyCorrelationIdentity.Scheme + new string('a', 63),
            RelationshipMaterializationKeyCorrelationIdentity.Scheme + new string('A', 64),
            RelationshipMaterializationKeyCorrelationIdentity.Scheme + new string('a', 63) + ":",
            RelationshipMaterializationKeyCorrelationIdentity.Scheme + new string('a', 63) + "\u001e",
            RelationshipMaterializationKeyCorrelationIdentity.Scheme + new string('a', 63) + "\n"
        };

        Assert.All(invalid, value =>
            Assert.Throws<ArgumentException>(() =>
                new RelationshipMaterializationKeyCorrelationIdentity(value)));
    }

    [Fact]
    public void Canonical_diagnostic_framing_preserves_route_delimiters_without_aliasing()
    {
        const string delimiterBearingRoute = "token\u001e\"authorization\nroute";
        var delimited = new RelationshipMaterializationDanglingReference(
            Generation("generation-candidate", relationshipIdentity: delimiterBearingRoute),
            KeyCorrelationIdentity('b'));
        var ordinary = new RelationshipMaterializationDanglingReference(
            Generation("generation-candidate", relationshipIdentity: "token"),
            KeyCorrelationIdentity('b'));

        Assert.NotEqual(delimited.CanonicalJson, ordinary.CanonicalJson);
        using var document = JsonDocument.Parse(delimited.CanonicalJson);
        Assert.Equal(
            delimiterBearingRoute,
            document.RootElement.GetProperty("relationshipRoute").GetString());
        Assert.DoesNotContain("\n", delimited.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001e", delimited.CanonicalJson, StringComparison.Ordinal);
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
            type => type.Name.Contains("ValidationEvidence", StringComparison.Ordinal) ||
                    type.Name.Contains("TransitionStage", StringComparison.Ordinal));
    }

    private static RelationshipMaterializationKeyCorrelationIdentity KeyCorrelationIdentity(char value) =>
        new(RelationshipMaterializationKeyCorrelationIdentity.Scheme + new string(value, 64));

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
