using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Relational.Documents;
using Groundwork.SqlServer.PhysicalStorage;
using Microsoft.Data.SqlClient;

namespace Groundwork.SqlServer.Documents;

public static class SqlServerDocumentStoreFactory
{
    /// <summary>
    /// Opens a physical document store after inspect-only runtime schema admission. Safe pending
    /// operations are applied only when <see cref="GroundworkRuntimeSchemaAdmissionOptions.AutoApplyOnStartup"/>
    /// is enabled.
    /// </summary>
    public static async Task<SqlServerPhysicalDocumentStore> OpenPhysicalAsync(
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
            SqlServerGroundworkCapabilities.PhysicalNames,
            namePolicy,
            new SqlServerPhysicalSchemaExecutor(connectionString),
            options,
            schemaAdmissionLog,
            cancellationToken);
        return new SqlServerPhysicalDocumentStore(
            connectionString,
            manifest,
            target.Routes,
            access,
            scopeObserver,
            target.Provider);
    }
}
