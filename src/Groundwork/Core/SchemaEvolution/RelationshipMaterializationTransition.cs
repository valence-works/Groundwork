using System.Buffers.Binary;
using System.Security.Cryptography;
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
/// An opaque HMAC-SHA-256 identity used to correlate one target key across deterministic
/// dangling-reference diagnostics. Only <see cref="Create"/> can construct an identity: callers
/// cannot relabel a raw value or an unkeyed digest as a keyed correlation identity.
/// </summary>
public sealed class RelationshipMaterializationKeyCorrelationIdentity :
    IEquatable<RelationshipMaterializationKeyCorrelationIdentity>
{
    public const string Scheme = "hmac-sha256-v1:";
    private const int MinimumProviderOwnedKeyLength = 32;
    private const string DomainSeparator =
        "groundwork.relationship-materialization.key-correlation.hmac-sha256.v1";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private RelationshipMaterializationKeyCorrelationIdentity(
        string value,
        RelationshipMaterializationGeneration candidateGeneration)
    {
        Value = value;
        CandidateGeneration = candidateGeneration;
    }

    /// <summary>
    /// Derives the opaque identity from a provider-owned key and the exact candidate generation.
    /// The HMAC input is UTF-8 with the following length-prefixed fields, in order: the literal
    /// <c>groundwork.relationship-materialization.key-correlation.hmac-sha256.v1</c>, relationship
    /// route identity, candidate generation identity, candidate materialization fingerprint, target
    /// scope, and target comparison key. Every field is prefixed with its UTF-8 byte length as one
    /// unsigned big-endian 32-bit integer. The result is lowercase hexadecimal under
    /// <see cref="Scheme"/>. The key, scope, and comparison key are used only while deriving the
    /// HMAC and are never retained or exposed by this type.
    /// </summary>
    public static RelationshipMaterializationKeyCorrelationIdentity Create(
        ReadOnlySpan<byte> providerOwnedKey,
        RelationshipMaterializationTransitionRequirement transitionRequirement,
        string targetScope,
        string targetComparisonKey)
    {
        if (providerOwnedKey.Length < MinimumProviderOwnedKeyLength)
        {
            throw new ArgumentException(
                $"A provider-owned relationship correlation key must contain at least {MinimumProviderOwnedKeyLength} bytes.",
                nameof(providerOwnedKey));
        }

        ArgumentNullException.ThrowIfNull(transitionRequirement);
        PhysicalRelationshipMaterializationIdentityValidator.Validate(
            targetScope,
            nameof(targetScope));
        PhysicalRelationshipMaterializationIdentityValidator.Validate(
            targetComparisonKey,
            nameof(targetComparisonKey));

        var candidate = transitionRequirement.CandidateGeneration;
        var framedInput = CreateFramedInput(
            DomainSeparator,
            candidate.RelationshipIdentity,
            candidate.GenerationIdentity,
            candidate.MaterializationFingerprint,
            targetScope,
            targetComparisonKey);
        try
        {
            var digest = HMACSHA256.HashData(providerOwnedKey, framedInput);
            try
            {
                return new($"{Scheme}{Convert.ToHexString(digest).ToLowerInvariant()}", candidate);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(framedInput);
        }
    }

    public string Value { get; }

    internal RelationshipMaterializationGeneration CandidateGeneration { get; }

    public bool Equals(RelationshipMaterializationKeyCorrelationIdentity? other) =>
        other is not null &&
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as RelationshipMaterializationKeyCorrelationIdentity);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    private static byte[] CreateFramedInput(params string[] fields)
    {
        var encodedFields = fields.Select(StrictUtf8.GetBytes).ToArray();
        try
        {
            var length = checked(encodedFields.Sum(field => checked(sizeof(uint) + field.Length)));
            var framed = new byte[length];
            var offset = 0;
            foreach (var field in encodedFields)
            {
                BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(offset, sizeof(uint)), (uint)field.Length);
                offset += sizeof(uint);
                field.CopyTo(framed, offset);
                offset += field.Length;
            }

            return framed;
        }
        finally
        {
            foreach (var field in encodedFields)
                CryptographicOperations.ZeroMemory(field);
        }
    }
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
        RelationshipMaterializationTransitionRequirement transitionRequirement,
        RelationshipMaterializationKeyCorrelationIdentity targetKeyCorrelationIdentity)
    {
        TransitionRequirement = transitionRequirement ?? throw new ArgumentNullException(nameof(transitionRequirement));
        TargetKeyCorrelationIdentity = targetKeyCorrelationIdentity ??
            throw new ArgumentNullException(nameof(targetKeyCorrelationIdentity));
        CandidateGeneration = TransitionRequirement.CandidateGeneration;
        if (!Equals(TargetKeyCorrelationIdentity.CandidateGeneration, CandidateGeneration))
        {
            throw new ArgumentException(
                "The relationship key correlation identity must be derived for the transition candidate generation.",
                nameof(targetKeyCorrelationIdentity));
        }

        RelationshipRouteIdentity = CandidateGeneration.RelationshipIdentity;
        CanonicalJson = SerializeCanonical(this);
    }

    /// <summary>
    /// The exact non-authoritative transition requirement that supplies this diagnostic's candidate
    /// generation. This diagnostic neither validates nor activates that transition.
    /// </summary>
    public RelationshipMaterializationTransitionRequirement TransitionRequirement { get; }

    public RelationshipMaterializationGeneration CandidateGeneration { get; }

    public string RelationshipRouteIdentity { get; }

    public RelationshipMaterializationKeyCorrelationIdentity TargetKeyCorrelationIdentity { get; }

    /// <summary>Canonical, unambiguously framed diagnostic payload for durable evidence.</summary>
    public string CanonicalJson { get; }

    public string Message => $"{DiagnosticCode}: {CanonicalJson}";

    public bool Equals(RelationshipMaterializationDanglingReference? other) =>
        other is not null &&
        Equals(CandidateGeneration, other.CandidateGeneration) &&
        Equals(TargetKeyCorrelationIdentity, other.TargetKeyCorrelationIdentity);

    public override bool Equals(object? obj) => Equals(obj as RelationshipMaterializationDanglingReference);

    public override int GetHashCode() => HashCode.Combine(CandidateGeneration, TargetKeyCorrelationIdentity);

    private static string SerializeCanonical(RelationshipMaterializationDanglingReference diagnostic)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("code", DiagnosticCode);
            writer.WriteString("relationshipRoute", diagnostic.RelationshipRouteIdentity);
            writer.WriteString("generation", diagnostic.CandidateGeneration.GenerationIdentity);
            writer.WriteString(
                "materializationFingerprint",
                diagnostic.CandidateGeneration.MaterializationFingerprint);
            writer.WriteString(
                "targetKeyCorrelationIdentity",
                diagnostic.TargetKeyCorrelationIdentity.Value);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
