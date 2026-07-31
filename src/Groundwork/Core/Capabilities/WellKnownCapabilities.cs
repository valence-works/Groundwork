namespace Groundwork.Core.Capabilities;

/// <summary>
/// The built-in capabilities owned by Groundwork core, seeded into
/// <see cref="CapabilityRegistry.Default"/>. Core declares only capabilities its own providers
/// actually advertise; modules introduce their own descriptors in their own vendor namespace through
/// <see cref="ICapabilityRegistryBuilder"/>.
/// </summary>
public static class WellKnownCapabilities
{
    /// <summary>
    /// Cross-unit atomic commit, advertised by every shipped provider for <c>IDocumentUnitOfWork</c>.
    /// The <c>operational</c> segment is retained for identifier stability: capability ids reach
    /// executable-route fingerprints, so renaming one would read as physical schema drift.
    /// </summary>
    public static readonly CapabilityId AtomicCommit = new("groundwork.operational.atomic-commit");

    /// <summary>All built-in capability descriptors, owned by the <c>groundwork</c> module.</summary>
    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
        new(AtomicCommit, "Atomic commit", "Cross-unit atomic commit (unit of work) across storage units.", EvidenceGatedByDefault: true)
    ];
}
