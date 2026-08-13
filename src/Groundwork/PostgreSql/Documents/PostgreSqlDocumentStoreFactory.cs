using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.PostgreSql.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Relational.Documents;
using Npgsql;

namespace Groundwork.PostgreSql.Documents;

public static class PostgreSqlDocumentStoreFactory
{
    /// <summary>
    /// Opens a physical document store after inspect-only runtime schema admission. Safe pending
    /// operations are applied only when <see cref="GroundworkRuntimeSchemaAdmissionOptions.AutoApplyOnStartup"/>
    /// is enabled.
    /// </summary>
    public static async Task<PostgreSqlPhysicalDocumentStore> OpenPhysicalAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        DocumentStoreAccess access,
        IPhysicalNamePolicy? namePolicy = null,
        IStorageScopeObserver? scopeObserver = null,
        GroundworkRuntimeSchemaAdmissionOptions? options = null,
        Action<GroundworkRuntimeSchemaAdmissionLogEntry>? schemaAdmissionLog = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(access);
        var target = await RelationalPhysicalStoreFactory.AdmitPhysicalAsync(
            manifest,
            provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            namePolicy,
            new PostgreSqlPhysicalSchemaExecutor(connectionString),
            options,
            schemaAdmissionLog,
            cancellationToken);
        return new PostgreSqlPhysicalDocumentStore(
            connectionString,
            manifest,
            target.Routes,
            access,
            scopeObserver,
            target.Provider);
    }
}
