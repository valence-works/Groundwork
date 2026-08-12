using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;

namespace Groundwork.Relational.Documents;

/// <summary>
/// The provider-neutral physical-open path shared by the relational document-store factories:
/// compiles the physical schema target, runs inspect-only runtime schema admission through the
/// provider's executor, and requires readiness before any store is constructed.
/// </summary>
internal static class RelationalPhysicalStoreFactory
{
    /// <summary>
    /// Compiles and admits the physical schema target for <paramref name="manifest"/>. Safe pending
    /// operations are applied only when
    /// <see cref="GroundworkRuntimeSchemaAdmissionOptions.AutoApplyOnStartup"/> is enabled.
    /// </summary>
    internal static async Task<PhysicalSchemaTarget> AdmitPhysicalAsync(
        StorageManifest manifest,
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer providerNames,
        IPhysicalNamePolicy? namePolicy,
        IPhysicalSchemaExecutor executor,
        GroundworkRuntimeSchemaAdmissionOptions? options,
        Action<GroundworkRuntimeSchemaAdmissionLogEntry>? schemaAdmissionLog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);
        var target = PhysicalSchemaTargetCompiler.Compile(manifest, provider, providerNames, namePolicy);
        var admission = await executor.InspectRuntimeAdmissionAsync(
            target,
            options,
            schemaAdmissionLog,
            cancellationToken);
        admission.EnsureReady();
        return target;
    }
}
