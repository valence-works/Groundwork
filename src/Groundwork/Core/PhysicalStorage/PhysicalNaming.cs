using Groundwork.Core.Manifests;

namespace Groundwork.Core.PhysicalStorage;

public enum PhysicalObjectKind
{
    PrimaryStorage,
    LinkedIndexStorage,
    CollectionElementStorage,
    CollectionElementField,
    EnvelopeField,
    LinkedIndexField,
    ProjectedField,
    LinkedProjectedField,
    PhysicalIndex,
    SchemaHistory
}

public sealed record PhysicalNameContext(
    StorageUnitIdentity StorageUnit,
    PhysicalObjectKind ObjectKind,
    string FeatureDefaultLogicalName);

public interface IPhysicalNamePolicy
{
    string ResolveName(PhysicalNameContext context);
}

public sealed class DelegatePhysicalNamePolicy(Func<PhysicalNameContext, string> resolver) : IPhysicalNamePolicy
{
    private readonly Func<PhysicalNameContext, string> _resolver =
        resolver ?? throw new ArgumentNullException(nameof(resolver));

    public string ResolveName(PhysicalNameContext context) => _resolver(context);
}

public static class PhysicalNamePolicy
{
    public static IPhysicalNamePolicy Identity { get; } =
        new DelegatePhysicalNamePolicy(context => context.FeatureDefaultLogicalName);
}

public sealed record PhysicalObjectNameOverride(
    PhysicalObjectKind ObjectKind,
    string FeatureDefaultLogicalName,
    string LogicalName);

public sealed record ProviderPhysicalNameContext(
    StorageUnitIdentity StorageUnit,
    PhysicalObjectKind ObjectKind,
    string LogicalName);

/// <summary>
/// Provider seam for identifier casing, reserved words, quoting rules, length limits, and
/// deterministic truncation. Business naming remains provider-agnostic.
/// </summary>
public interface IProviderPhysicalNameNormalizer
{
    string Normalize(ProviderPhysicalNameContext context);

    string GetCollisionScope(ProviderPhysicalNameContext context);
}

public sealed class DelegateProviderPhysicalNameNormalizer(
    Func<ProviderPhysicalNameContext, string> normalizer,
    Func<ProviderPhysicalNameContext, string>? collisionScope = null) : IProviderPhysicalNameNormalizer
{
    private readonly Func<ProviderPhysicalNameContext, string> _normalizer =
        normalizer ?? throw new ArgumentNullException(nameof(normalizer));
    private readonly Func<ProviderPhysicalNameContext, string>? _collisionScope = collisionScope;

    public string Normalize(ProviderPhysicalNameContext context) => _normalizer(context);

    public string GetCollisionScope(ProviderPhysicalNameContext context) =>
        _collisionScope?.Invoke(context) ?? ProviderPhysicalNameNormalizerDefaults.GetCollisionScope(context);
}

/// <summary>
/// Canonical collision-scope arms shared by every provider normalizer. Field scopes are always
/// per storage unit; the relation-level scopes come in two namespace models:
/// <list type="bullet">
/// <item><description>Per-table (no argument): storage relations share "primary-storage", schema
/// history is its own scope, and indexes are scoped per storage unit — for engines whose index
/// names live inside their table's namespace.</description></item>
/// <item><description>Flat (pass <c>flatRelationNamespace</c>): storage relations, physical
/// indexes, and schema history all share one provider-labeled scope — for engines such as
/// PostgreSQL and SQLite whose relations and indexes share a schema-global namespace.</description></item>
/// </list>
/// Public so provider assemblies declare only their namespace model instead of copying the arms.
/// </summary>
public static class ProviderPhysicalNameNormalizerDefaults
{
    public static string GetCollisionScope(ProviderPhysicalNameContext context) => context.ObjectKind switch
    {
        PhysicalObjectKind.PrimaryStorage or PhysicalObjectKind.LinkedIndexStorage or PhysicalObjectKind.CollectionElementStorage => "primary-storage",
        PhysicalObjectKind.PhysicalIndex => $"{context.StorageUnit.Value}:physical-indexes",
        PhysicalObjectKind.SchemaHistory => "schema-history",
        _ => GetFieldCollisionScope(context)
    };

    /// <summary>Flat relation namespace: all relations, indexes, and schema history collide within one provider-chosen scope.</summary>
    public static string GetCollisionScope(ProviderPhysicalNameContext context, string flatRelationNamespace) => context.ObjectKind switch
    {
        PhysicalObjectKind.PrimaryStorage or PhysicalObjectKind.LinkedIndexStorage or PhysicalObjectKind.CollectionElementStorage or
        PhysicalObjectKind.PhysicalIndex or PhysicalObjectKind.SchemaHistory => flatRelationNamespace,
        _ => GetFieldCollisionScope(context)
    };

    private static string GetFieldCollisionScope(ProviderPhysicalNameContext context) => context.ObjectKind switch
    {
        PhysicalObjectKind.EnvelopeField or PhysicalObjectKind.ProjectedField => $"{context.StorageUnit.Value}:columns",
        PhysicalObjectKind.LinkedIndexField or PhysicalObjectKind.LinkedProjectedField => $"{context.StorageUnit.Value}:linked-columns",
        PhysicalObjectKind.CollectionElementField => $"{context.StorageUnit.Value}:collection-element-columns",
        _ => throw new ArgumentOutOfRangeException(nameof(context), context.ObjectKind, null)
    };
}

public static class ProviderPhysicalNameNormalizer
{
    public static IProviderPhysicalNameNormalizer Identity { get; } =
        new DelegateProviderPhysicalNameNormalizer(context => context.LogicalName);
}

public sealed record ResolvedPhysicalObjectName(
    PhysicalObjectKind ObjectKind,
    string FeatureDefaultLogicalName,
    string LogicalName,
    StorageUnitIdentity NamingOwner);

public sealed record ProviderPhysicalObjectName(
    PhysicalObjectKind ObjectKind,
    string FeatureDefaultLogicalName,
    string LogicalName,
    string Identifier,
    string CollisionScope,
    StorageUnitIdentity NamingOwner);
