using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;
using Groundwork.Core.Intents;
using Groundwork.TestInfrastructure;

namespace Groundwork.Tests;

internal static class SampleManifests
{
    public static StorageManifest MetadataManifest() =>
        TestManifests.MetadataManifest(
            tenancy: TenancyPolicy.Scoped,
            startsWithCategory: true,
            description: "Sample generic manifest for Groundwork contract tests.");

    public static ProviderCapabilityReport PortableCapabilities() =>
        PortableCapabilities(new ProviderIdentity("portable-test-provider", "1.0.0"));

    public static ProviderCapabilityReport PortableCapabilities(ProviderIdentity provider) =>
        new(
            provider,
            new HashSet<CapabilityId>(),
            new HashSet<CapabilityId>(),
            IndexCapabilities.All,
            Enum.GetValues<PortableQueryOperation>().ToHashSet(),
            Enum.GetValues<ConcurrencyKind>().ToHashSet(),
            []);

    public static ProviderCapabilityReport OperationalCapabilities(ProviderIdentity provider)
    {
        var capabilities = WellKnownCapabilities.All.Select(descriptor => descriptor.Id).ToHashSet();
        return new(
            provider,
            capabilities,
            capabilities.ToHashSet(),
            IndexCapabilities.All,
            Enum.GetValues<PortableQueryOperation>().ToHashSet(),
            Enum.GetValues<ConcurrencyKind>().ToHashSet(),
            []);
    }
}
