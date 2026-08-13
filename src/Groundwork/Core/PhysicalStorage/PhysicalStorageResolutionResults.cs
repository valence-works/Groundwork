using Groundwork.Core.Manifests;
using Groundwork.Core.Scoping;
using Groundwork.Core.Validation;

namespace Groundwork.Core.PhysicalStorage;

public sealed record ResolvedPhysicalTableDefinition(
    StorageUnitIdentity StorageUnit,
    StorageUnitProvisioningMode ProvisioningMode,
    IdentityPolicy IdentityPolicy,
    PhysicalTableDefinition Definition,
    SharedDocumentStorageDefinition? SharedStorageDefinition,
    IReadOnlyList<ScaleBearingPathDemand> ScaleBearingDemand,
    IReadOnlyList<ResolvedPhysicalObjectName> Names)
{
    public StorageScopePolicy ScopePolicy { get; init; }

    public ResolvedPhysicalObjectName PrimaryName =>
        Names.Single(x => x.ObjectKind == PhysicalObjectKind.PrimaryStorage);

    public bool Equals(ResolvedPhysicalTableDefinition? other) =>
        other is not null &&
        StorageUnit == other.StorageUnit &&
        ProvisioningMode == other.ProvisioningMode &&
        IdentityPolicy == other.IdentityPolicy &&
        Definition.Equals(other.Definition) &&
        Equals(SharedStorageDefinition, other.SharedStorageDefinition) &&
        ScaleBearingDemand.SequenceEqual(other.ScaleBearingDemand) &&
        Names.SequenceEqual(other.Names) &&
        ScopePolicy == other.ScopePolicy;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(StorageUnit);
        hash.Add(ProvisioningMode);
        hash.Add(IdentityPolicy);
        hash.Add(Definition);
        hash.Add(SharedStorageDefinition);
        foreach (var demand in ScaleBearingDemand)
            hash.Add(demand);
        foreach (var name in Names)
            hash.Add(name);
        hash.Add(ScopePolicy);
        return hash.ToHashCode();
    }
}

public sealed record ProviderPhysicalTableDefinition(
    ResolvedPhysicalTableDefinition Resolved,
    IReadOnlyList<ProviderPhysicalObjectName> Names,
    string Fingerprint)
{
    public PhysicalTableDefinition Definition => Resolved.Definition;

    public ProviderPhysicalObjectName PrimaryName =>
        Names.Single(x => x.ObjectKind == PhysicalObjectKind.PrimaryStorage);

    public bool Equals(ProviderPhysicalTableDefinition? other) =>
        other is not null &&
        Resolved.Equals(other.Resolved) &&
        Names.SequenceEqual(other.Names) &&
        Fingerprint == other.Fingerprint;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Resolved);
        foreach (var name in Names)
            hash.Add(name);
        hash.Add(Fingerprint, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

public sealed record PhysicalStorageResolutionResult(
    IReadOnlyList<ProviderPhysicalTableDefinition> Definitions,
    IReadOnlyList<GroundworkDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(x => !x.IsError);

    public bool Equals(PhysicalStorageResolutionResult? other) =>
        other is not null &&
        Definitions.SequenceEqual(other.Definitions) &&
        Diagnostics.SequenceEqual(other.Diagnostics);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var definition in Definitions)
            hash.Add(definition);
        foreach (var diagnostic in Diagnostics)
            hash.Add(diagnostic);
        return hash.ToHashCode();
    }
}
