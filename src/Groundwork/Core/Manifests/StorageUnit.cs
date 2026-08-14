using Groundwork.Core.Intents;
using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Core.Manifests;

/// <summary>
/// One provider-neutral storage unit. All routing intent belongs to
/// <see cref="PhysicalStorage"/>, the declaration surface consumed by the three-form resolver.
/// </summary>
public sealed record StorageUnit(
    StorageUnitIdentity Identity,
    string DisplayName,
    StorageIntent Intent,
    LifecyclePolicy Lifecycle,
    IdentityPolicy IdentityPolicy,
    TenancyPolicy Tenancy,
    ConcurrencyPolicy Concurrency,
    SerializationPolicy Serialization,
    StorageUnitPhysicalStorage PhysicalStorage)
{
    /// <summary>
    /// Creates a storage unit, validating that a physical-storage declaration is supplied.
    /// </summary>
    public static StorageUnit Create(
        StorageUnitIdentity identity,
        string displayName,
        StorageIntent intent,
        LifecyclePolicy lifecycle,
        IdentityPolicy identityPolicy,
        TenancyPolicy tenancy,
        ConcurrencyPolicy concurrency,
        SerializationPolicy serialization,
        StorageUnitPhysicalStorage physicalStorage)
    {
        ArgumentNullException.ThrowIfNull(physicalStorage);
        return new StorageUnit(
            identity,
            displayName,
            intent,
            lifecycle,
            identityPolicy,
            tenancy,
            concurrency,
            serialization,
            physicalStorage);
    }
}
