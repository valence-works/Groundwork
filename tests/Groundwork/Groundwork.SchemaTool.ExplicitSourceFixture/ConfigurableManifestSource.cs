using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Scoping;

namespace Groundwork.SchemaTool.ExplicitSourceFixture;

/// <summary>
/// Stands in for an application that deploys more than one schema from a single assembly, selecting
/// between them per invocation. It refuses rather than defaulting when the selection is absent, which is
/// the behavior the option channel exists to make possible.
/// </summary>
public sealed class ConfigurableManifestSource : IConfigurablePhysicalSchemaManifestSource
{
    private string? partition;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.TryGetValue("partition", out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Manifest option 'partition' is required.");

        partition = value;
    }

    public StorageManifest CreateManifest()
    {
        if (partition is null)
            throw new InvalidOperationException("Manifest option 'partition' is required.");

        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            $"{partition}_documents",
            [
                new ProjectedColumnDefinition(
                    "category",
                    "category",
                    PortablePhysicalType.String,
                    IsNullable: false)
            ]);
        var unit = new StorageUnit(
            new StorageUnitIdentity("documents"),
            "document",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition)));

        return new StorageManifest(
            new StorageManifestIdentity($"configurable-fixture.{partition}"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            []);
    }
}
