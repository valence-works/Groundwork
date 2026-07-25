using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Provider-neutral fields persisted by generated relationship-reference materializations and
/// target-key fences. Lookup keys narrow a seek; comparison keys are always retained so a lookup
/// hash can never be treated as proof of identity.
/// </summary>
public enum PhysicalRelationshipSidecarField
{
    MaterializationGeneration,
    SourceScope,
    SourceLookupKey,
    SourceComparisonKey,
    TargetScope,
    TargetLookupKey,
    TargetComparisonKey
}

/// <summary>
/// An immutable, ordered access path over a generated relationship sidecar store.
/// </summary>
public sealed class PhysicalRelationshipSidecarAccessPath : IEquatable<PhysicalRelationshipSidecarAccessPath>
{
    public PhysicalRelationshipSidecarAccessPath(
        string identity,
        bool isUnique,
        IEnumerable<PhysicalRelationshipSidecarField> fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(fields);

        var orderedFields = fields.ToArray();
        if (orderedFields.Length == 0)
            throw new ArgumentException("A relationship sidecar access path must contain at least one field.", nameof(fields));
        if (orderedFields.Distinct().Count() != orderedFields.Length)
            throw new ArgumentException("A relationship sidecar access path cannot repeat a field.", nameof(fields));

        Identity = identity;
        IsUnique = isUnique;
        Fields = Array.AsReadOnly(orderedFields);
    }

    public string Identity { get; }

    public bool IsUnique { get; }

    public IReadOnlyList<PhysicalRelationshipSidecarField> Fields { get; }

    public bool Equals(PhysicalRelationshipSidecarAccessPath? other) =>
        other is not null &&
        string.Equals(Identity, other.Identity, StringComparison.Ordinal) &&
        IsUnique == other.IsUnique &&
        Fields.SequenceEqual(other.Fields);

    public override bool Equals(object? obj) => Equals(obj as PhysicalRelationshipSidecarAccessPath);

    public override int GetHashCode() => PhysicalRelationshipSidecarSchemaHash.Create(
        [Identity, IsUnique.ToString(System.Globalization.CultureInfo.InvariantCulture)],
        Fields);
}

/// <summary>
/// The logical reference store generated for one relationship materialization generation. One row
/// records the current optional target reference for one source document.
/// </summary>
public sealed class PhysicalRelationshipReferenceMaterializationSchema : IEquatable<PhysicalRelationshipReferenceMaterializationSchema>
{
    private static readonly PhysicalRelationshipSidecarField[] RowFields =
    [
        PhysicalRelationshipSidecarField.MaterializationGeneration,
        PhysicalRelationshipSidecarField.SourceScope,
        PhysicalRelationshipSidecarField.SourceLookupKey,
        PhysicalRelationshipSidecarField.SourceComparisonKey,
        PhysicalRelationshipSidecarField.TargetScope,
        PhysicalRelationshipSidecarField.TargetLookupKey,
        PhysicalRelationshipSidecarField.TargetComparisonKey
    ];

    private static readonly PhysicalRelationshipSidecarField[] SourceKeyFields =
    [
        PhysicalRelationshipSidecarField.MaterializationGeneration,
        PhysicalRelationshipSidecarField.SourceScope,
        PhysicalRelationshipSidecarField.SourceLookupKey,
        PhysicalRelationshipSidecarField.SourceComparisonKey
    ];

    private static readonly PhysicalRelationshipSidecarField[] TargetSeekFields =
    [
        PhysicalRelationshipSidecarField.MaterializationGeneration,
        PhysicalRelationshipSidecarField.TargetScope,
        PhysicalRelationshipSidecarField.TargetLookupKey,
        PhysicalRelationshipSidecarField.TargetComparisonKey
    ];

    public PhysicalRelationshipReferenceMaterializationSchema(
        string storageIdentity,
        string generationIdentity,
        PhysicalRelationshipSidecarAccessPath uniqueSourceAccessPath,
        PhysicalRelationshipSidecarAccessPath targetSeekAccessPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationIdentity);
        ArgumentNullException.ThrowIfNull(uniqueSourceAccessPath);
        ArgumentNullException.ThrowIfNull(targetSeekAccessPath);

        EnsureExactAccessPath(
            uniqueSourceAccessPath,
            isUnique: true,
            SourceKeyFields,
            nameof(uniqueSourceAccessPath));
        EnsureExactAccessPath(
            targetSeekAccessPath,
            isUnique: false,
            TargetSeekFields,
            nameof(targetSeekAccessPath));

        StorageIdentity = storageIdentity;
        GenerationIdentity = generationIdentity;
        UniqueSourceAccessPath = uniqueSourceAccessPath;
        TargetSeekAccessPath = targetSeekAccessPath;
        Fields = Array.AsReadOnly(RowFields);
    }

    public string StorageIdentity { get; }

    /// <summary>
    /// The reference-storage identity is the common semantic generation discriminator shared by
    /// this store and its fence. It is persisted even when a provider maps each generation to a
    /// separate physical object.
    /// </summary>
    public string GenerationIdentity { get; }

    public IReadOnlyList<PhysicalRelationshipSidecarField> Fields { get; }

    public PhysicalRelationshipSidecarAccessPath UniqueSourceAccessPath { get; }

    public PhysicalRelationshipSidecarAccessPath TargetSeekAccessPath { get; }

    internal static void EnsureExactAccessPath(
        PhysicalRelationshipSidecarAccessPath accessPath,
        bool isUnique,
        IReadOnlyList<PhysicalRelationshipSidecarField> expectedFields,
        string parameterName)
    {
        if (accessPath.IsUnique != isUnique ||
            !accessPath.Fields.SequenceEqual(expectedFields))
        {
            throw new ArgumentException(
                "The relationship sidecar access path does not match the provider-neutral contract.",
                parameterName);
        }
    }

    public bool Equals(PhysicalRelationshipReferenceMaterializationSchema? other) =>
        other is not null &&
        string.Equals(StorageIdentity, other.StorageIdentity, StringComparison.Ordinal) &&
        string.Equals(GenerationIdentity, other.GenerationIdentity, StringComparison.Ordinal) &&
        Fields.SequenceEqual(other.Fields) &&
        Equals(UniqueSourceAccessPath, other.UniqueSourceAccessPath) &&
        Equals(TargetSeekAccessPath, other.TargetSeekAccessPath);

    public override bool Equals(object? obj) => Equals(obj as PhysicalRelationshipReferenceMaterializationSchema);

    public override int GetHashCode() => PhysicalRelationshipSidecarSchemaHash.Create(
        [StorageIdentity, GenerationIdentity, UniqueSourceAccessPath.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture), TargetSeekAccessPath.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture)],
        Fields);
}

/// <summary>
/// The logical target-key fence for one relationship materialization generation. Acquiring this
/// unique key serializes reference attachment/removal with a target-side reference check.
/// </summary>
public sealed class PhysicalRelationshipTargetFenceSchema : IEquatable<PhysicalRelationshipTargetFenceSchema>
{
    private static readonly PhysicalRelationshipSidecarField[] FenceFields =
    [
        PhysicalRelationshipSidecarField.MaterializationGeneration,
        PhysicalRelationshipSidecarField.TargetScope,
        PhysicalRelationshipSidecarField.TargetLookupKey,
        PhysicalRelationshipSidecarField.TargetComparisonKey
    ];

    public PhysicalRelationshipTargetFenceSchema(
        string storageIdentity,
        string generationIdentity,
        PhysicalRelationshipSidecarAccessPath uniqueTargetFenceAccessPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationIdentity);
        ArgumentNullException.ThrowIfNull(uniqueTargetFenceAccessPath);
        PhysicalRelationshipReferenceMaterializationSchema.EnsureExactAccessPath(
            uniqueTargetFenceAccessPath,
            isUnique: true,
            FenceFields,
            nameof(uniqueTargetFenceAccessPath));

        StorageIdentity = storageIdentity;
        GenerationIdentity = generationIdentity;
        UniqueTargetFenceAccessPath = uniqueTargetFenceAccessPath;
        Fields = Array.AsReadOnly(FenceFields);
    }

    public string StorageIdentity { get; }

    public string GenerationIdentity { get; }

    public IReadOnlyList<PhysicalRelationshipSidecarField> Fields { get; }

    public PhysicalRelationshipSidecarAccessPath UniqueTargetFenceAccessPath { get; }

    public bool Equals(PhysicalRelationshipTargetFenceSchema? other) =>
        other is not null &&
        string.Equals(StorageIdentity, other.StorageIdentity, StringComparison.Ordinal) &&
        string.Equals(GenerationIdentity, other.GenerationIdentity, StringComparison.Ordinal) &&
        Fields.SequenceEqual(other.Fields) &&
        Equals(UniqueTargetFenceAccessPath, other.UniqueTargetFenceAccessPath);

    public override bool Equals(object? obj) => Equals(obj as PhysicalRelationshipTargetFenceSchema);

    public override int GetHashCode() => PhysicalRelationshipSidecarSchemaHash.Create(
        [StorageIdentity, GenerationIdentity, UniqueTargetFenceAccessPath.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture)],
        Fields);
}

/// <summary>
/// Immutable provider-neutral desired schema for the generated relationship reference and
/// target-key fence sidecars. This is a contract only: it deliberately does not admit a provider,
/// create physical objects, or make relationship manifests executable.
/// </summary>
public sealed class PhysicalRelationshipMaterializationSchema : IEquatable<PhysicalRelationshipMaterializationSchema>
{
    public PhysicalRelationshipMaterializationSchema(
        string relationshipIdentity,
        string generationIdentity,
        PhysicalRelationshipReferenceMaterializationSchema reference,
        PhysicalRelationshipTargetFenceSchema fence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationshipIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationIdentity);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(fence);
        if (!string.Equals(reference.GenerationIdentity, generationIdentity, StringComparison.Ordinal) ||
            !string.Equals(fence.GenerationIdentity, generationIdentity, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Relationship reference and fence schemas must use one materialization generation.");
        }

        RelationshipIdentity = relationshipIdentity;
        GenerationIdentity = generationIdentity;
        Reference = reference;
        Fence = fence;
        Fingerprint = PhysicalRelationshipMaterializationSchemaSerializer.CreateFingerprint(this);
    }

    public string RelationshipIdentity { get; }

    public string GenerationIdentity { get; }

    public PhysicalRelationshipReferenceMaterializationSchema Reference { get; }

    public PhysicalRelationshipTargetFenceSchema Fence { get; }

    /// <summary>SHA-256 over the canonical schema serialization excluding this property.</summary>
    public string Fingerprint { get; }

    public string CanonicalJson => PhysicalRelationshipMaterializationSchemaSerializer.Serialize(this);

    public bool Equals(PhysicalRelationshipMaterializationSchema? other) =>
        other is not null &&
        string.Equals(RelationshipIdentity, other.RelationshipIdentity, StringComparison.Ordinal) &&
        string.Equals(GenerationIdentity, other.GenerationIdentity, StringComparison.Ordinal) &&
        Equals(Reference, other.Reference) &&
        Equals(Fence, other.Fence) &&
        string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as PhysicalRelationshipMaterializationSchema);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Fingerprint);

    internal static PhysicalRelationshipMaterializationSchema Create(PhysicalRelationshipPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var generationIdentity = plan.Materialization.ReferenceStorageIdentity;
        return new(
            plan.Identity,
            generationIdentity,
            new PhysicalRelationshipReferenceMaterializationSchema(
                plan.Materialization.ReferenceStorageIdentity,
                generationIdentity,
                new PhysicalRelationshipSidecarAccessPath(
                    plan.Materialization.ReferenceBySourceIndexIdentity,
                    isUnique: true,
                    [
                        PhysicalRelationshipSidecarField.MaterializationGeneration,
                        PhysicalRelationshipSidecarField.SourceScope,
                        PhysicalRelationshipSidecarField.SourceLookupKey,
                        PhysicalRelationshipSidecarField.SourceComparisonKey
                    ]),
                new PhysicalRelationshipSidecarAccessPath(
                    plan.Materialization.ReferenceByTargetIndexIdentity,
                    isUnique: false,
                    [
                        PhysicalRelationshipSidecarField.MaterializationGeneration,
                        PhysicalRelationshipSidecarField.TargetScope,
                        PhysicalRelationshipSidecarField.TargetLookupKey,
                        PhysicalRelationshipSidecarField.TargetComparisonKey
                    ])),
            new PhysicalRelationshipTargetFenceSchema(
                plan.Materialization.FenceStorageIdentity,
                generationIdentity,
                new PhysicalRelationshipSidecarAccessPath(
                    plan.Materialization.FenceByTargetIndexIdentity,
                    isUnique: true,
                    [
                        PhysicalRelationshipSidecarField.MaterializationGeneration,
                        PhysicalRelationshipSidecarField.TargetScope,
                        PhysicalRelationshipSidecarField.TargetLookupKey,
                        PhysicalRelationshipSidecarField.TargetComparisonKey
                    ])));
    }
}

internal static class PhysicalRelationshipSidecarSchemaHash
{
    public static int Create(
        IReadOnlyList<string> strings,
        IReadOnlyList<PhysicalRelationshipSidecarField> fields)
    {
        var hash = new HashCode();
        foreach (var value in strings)
            hash.Add(value, StringComparer.Ordinal);
        foreach (var field in fields)
            hash.Add(field);
        return hash.ToHashCode();
    }
}

/// <summary>Canonical serialization and fingerprinting for relationship sidecar schemas.</summary>
public static class PhysicalRelationshipMaterializationSchemaSerializer
{
    public static string Serialize(PhysicalRelationshipMaterializationSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return Encoding.UTF8.GetString(SerializeCore(schema, includeFingerprint: true));
    }

    internal static string CreateFingerprint(PhysicalRelationshipMaterializationSchema schema) =>
        Convert.ToHexString(SHA256.HashData(SerializeCore(schema, includeFingerprint: false))).ToLowerInvariant();

    private static byte[] SerializeCore(PhysicalRelationshipMaterializationSchema schema, bool includeFingerprint)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("relationship", schema.RelationshipIdentity);
            writer.WriteString("generation", schema.GenerationIdentity);
            WriteReference(writer, schema.Reference);
            WriteFence(writer, schema.Fence);
            if (includeFingerprint)
                writer.WriteString("fingerprint", schema.Fingerprint);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteReference(
        Utf8JsonWriter writer,
        PhysicalRelationshipReferenceMaterializationSchema reference)
    {
        writer.WritePropertyName("reference");
        writer.WriteStartObject();
        writer.WriteString("storageIdentity", reference.StorageIdentity);
        writer.WriteString("generation", reference.GenerationIdentity);
        WriteFields(writer, "fields", reference.Fields);
        WriteAccessPath(writer, "uniqueSourceAccessPath", reference.UniqueSourceAccessPath);
        WriteAccessPath(writer, "targetSeekAccessPath", reference.TargetSeekAccessPath);
        writer.WriteEndObject();
    }

    private static void WriteFence(Utf8JsonWriter writer, PhysicalRelationshipTargetFenceSchema fence)
    {
        writer.WritePropertyName("fence");
        writer.WriteStartObject();
        writer.WriteString("storageIdentity", fence.StorageIdentity);
        writer.WriteString("generation", fence.GenerationIdentity);
        WriteFields(writer, "fields", fence.Fields);
        WriteAccessPath(writer, "uniqueTargetFenceAccessPath", fence.UniqueTargetFenceAccessPath);
        writer.WriteEndObject();
    }

    private static void WriteAccessPath(
        Utf8JsonWriter writer,
        string propertyName,
        PhysicalRelationshipSidecarAccessPath accessPath)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WriteString("identity", accessPath.Identity);
        writer.WriteBoolean("unique", accessPath.IsUnique);
        WriteFields(writer, "fields", accessPath.Fields);
        writer.WriteEndObject();
    }

    private static void WriteFields(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<PhysicalRelationshipSidecarField> fields)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var field in fields)
            writer.WriteStringValue(field.ToString());
        writer.WriteEndArray();
    }
}
