using System.Text;
using System.Text.Json;
using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Core.SchemaEvolution;

/// <summary>
/// One exact generated relationship materialization generation. The generation and fingerprint are
/// captured together from the generated schema so provider executors can bind durable transition
/// state to one physical shape.
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
/// Describes the exact before/after generations an authoritative provider executor must use for a
/// transition. This is not durable state, validation evidence, an activation receipt, or admission
/// authority. The executor that eventually consumes it must own durable revision checks and atomic
/// compare-and-swap activation.
/// </summary>
public sealed class RelationshipMaterializationTransitionRequirement
{
    public RelationshipMaterializationTransitionRequirement(
        RelationshipMaterializationGeneration activeGeneration,
        RelationshipMaterializationGeneration candidateGeneration)
    {
        ActiveGeneration = activeGeneration ?? throw new ArgumentNullException(nameof(activeGeneration));
        CandidateGeneration = candidateGeneration ?? throw new ArgumentNullException(nameof(candidateGeneration));
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
    }

    public RelationshipMaterializationGeneration ActiveGeneration { get; }

    public RelationshipMaterializationGeneration CandidateGeneration { get; }
}

/// <summary>
/// A provider-supplied opaque identity used to correlate one target key across deterministic
/// dangling-reference diagnostics. The closed format carries an HMAC-SHA-256 result only; providers
/// must derive it with a stable provider-owned secret over a domain-separated, unambiguously framed
/// tuple of relationship route, generation, materialization fingerprint, target scope, and target
/// comparison key. Providers must never pass raw values or unkeyed digests. Core validates the
/// closed format only; the authoritative provider executor owns derivation and secret handling.
/// </summary>
public sealed class RelationshipMaterializationKeyCorrelationIdentity :
    IEquatable<RelationshipMaterializationKeyCorrelationIdentity>
{
    public const string Scheme = "hmac-sha256-v1:";
    private const int DigestLength = 64;

    public RelationshipMaterializationKeyCorrelationIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != Scheme.Length + DigestLength ||
            !value.StartsWith(Scheme, StringComparison.Ordinal) ||
            !value.AsSpan(Scheme.Length).ContainsOnlyLowercaseHex())
        {
            throw new ArgumentException(
                $"A relationship key correlation identity must use '{Scheme}' followed by exactly {DigestLength} lowercase hexadecimal characters.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public bool Equals(RelationshipMaterializationKeyCorrelationIdentity? other) =>
        other is not null &&
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as RelationshipMaterializationKeyCorrelationIdentity);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}

/// <summary>
/// A stable diagnostic for a legacy reference whose target does not exist. It exposes only the
/// manifest-owned relationship route, exact candidate generation/fingerprint, and a provider-owned
/// keyed correlation identity. Stored reference values, scopes, and comparison keys are absent.
/// </summary>
public sealed class RelationshipMaterializationDanglingReference :
    IEquatable<RelationshipMaterializationDanglingReference>
{
    public const string DiagnosticCode = "GW-RELATIONSHIP-013";

    public RelationshipMaterializationDanglingReference(
        RelationshipMaterializationGeneration generation,
        RelationshipMaterializationKeyCorrelationIdentity targetKeyCorrelationIdentity)
    {
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        TargetKeyCorrelationIdentity = targetKeyCorrelationIdentity ??
            throw new ArgumentNullException(nameof(targetKeyCorrelationIdentity));
        RelationshipRouteIdentity = generation.RelationshipIdentity;
        CanonicalJson = SerializeCanonical(this);
    }

    public RelationshipMaterializationGeneration Generation { get; }

    public string RelationshipRouteIdentity { get; }

    public RelationshipMaterializationKeyCorrelationIdentity TargetKeyCorrelationIdentity { get; }

    /// <summary>Canonical, unambiguously framed diagnostic payload for durable evidence.</summary>
    public string CanonicalJson { get; }

    public string Message => $"{DiagnosticCode}: {CanonicalJson}";

    public bool Equals(RelationshipMaterializationDanglingReference? other) =>
        other is not null &&
        Equals(Generation, other.Generation) &&
        Equals(TargetKeyCorrelationIdentity, other.TargetKeyCorrelationIdentity);

    public override bool Equals(object? obj) => Equals(obj as RelationshipMaterializationDanglingReference);

    public override int GetHashCode() => HashCode.Combine(Generation, TargetKeyCorrelationIdentity);

    private static string SerializeCanonical(RelationshipMaterializationDanglingReference diagnostic)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("code", DiagnosticCode);
            writer.WriteString("relationshipRoute", diagnostic.RelationshipRouteIdentity);
            writer.WriteString("generation", diagnostic.Generation.GenerationIdentity);
            writer.WriteString(
                "materializationFingerprint",
                diagnostic.Generation.MaterializationFingerprint);
            writer.WriteString(
                "targetKeyCorrelationIdentity",
                diagnostic.TargetKeyCorrelationIdentity.Value);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

internal static class RelationshipMaterializationKeyCorrelationIdentityFormat
{
    public static bool ContainsOnlyLowercaseHex(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        return true;
    }
}
