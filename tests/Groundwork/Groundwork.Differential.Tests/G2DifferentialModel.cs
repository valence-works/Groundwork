using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;

namespace Groundwork.Differential.Tests;

internal static class G2DifferentialModel
{
    public static (StorageManifest Manifest, PhysicalSchemaTarget Target) CompileRelational(
        string instance,
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer)
    {
        var manifest = G2DifferentialCorpus.CreateManifest(instance);
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            new DelegatePhysicalNamePolicy(context => $"gw_{instance}_{context.FeatureDefaultLogicalName}"),
            normalizer);
        if (!resolution.IsValid)
            throw new InvalidOperationException(string.Join("; ", resolution.Diagnostics.Select(item => item.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        if (!compilation.IsValid)
            throw new InvalidOperationException(string.Join("; ", compilation.Diagnostics.Select(item => item.Message)));
        return (manifest, new PhysicalSchemaTarget(manifest.Identity, manifest.Version, provider, compilation.Routes));
    }
}
