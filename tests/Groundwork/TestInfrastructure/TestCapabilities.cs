using Groundwork.Core.Capabilities;

namespace Groundwork.TestInfrastructure;

/// <summary>
/// Module-owned capabilities used by capability, validation, and planner tests that need concrete
/// requirement values.
///
/// These deliberately live in a test-owned vendor namespace rather than reusing
/// <see cref="WellKnownCapabilities"/>. Core declares only capabilities its own providers advertise,
/// so a requirement no shipped provider supports — the common case in these tests — is exactly the
/// module-defined capability a consuming module would contribute through
/// <see cref="ICapabilityRegistryBuilder"/>. Use <see cref="Registry"/> when constructing a validator
/// so the ids are recognized (an unregistered requirement is a <c>GW-CAP-014</c> error by design).
///
/// Tests asserting evidence-gating use <see cref="WellKnownCapabilities.AtomicCommit"/> instead,
/// since the default <see cref="WorkloadEvidencePolicy"/> derives from the built-in registry.
/// </summary>
public static class TestCapabilities
{
    public static readonly CapabilityId Primary = new("test.workload.primary");
    public static readonly CapabilityId Secondary = new("test.workload.secondary");

    public static readonly CapabilityDescriptor PrimaryDescriptor = new(
        Primary,
        "Primary test workload",
        "Module-defined capability used by capability tests.",
        EvidenceGatedByDefault: false,
        OwningModule: "test.workload");

    public static readonly CapabilityDescriptor SecondaryDescriptor = new(
        Secondary,
        "Secondary test workload",
        "Second module-defined capability used by capability tests.",
        EvidenceGatedByDefault: false,
        OwningModule: "test.workload");

    /// <summary>The built-in capabilities plus both module-defined test capabilities.</summary>
    public static CapabilityRegistry Registry { get; } = BuildRegistry();

    private static CapabilityRegistry BuildRegistry()
    {
        var builder = CapabilityRegistry.CreateBuilder();
        builder.Add(PrimaryDescriptor);
        builder.Add(SecondaryDescriptor);
        return builder.Build();
    }
}
