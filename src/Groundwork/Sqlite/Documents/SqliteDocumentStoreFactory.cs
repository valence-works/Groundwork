using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Relational.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Documents;

public static class SqliteDocumentStoreFactory
{
    /// <summary>
    /// Opens a physical document store after inspect-only runtime schema admission. Safe pending
    /// operations are applied only when <see cref="GroundworkRuntimeSchemaAdmissionOptions.AutoApplyOnStartup"/>
    /// is enabled.
    /// </summary>
    public static async Task<SqlitePhysicalDocumentStore> OpenPhysicalAsync(
        SqliteConnection connection,
        StorageManifest manifest,
        ProviderIdentity provider,
        DocumentStoreAccess access,
        IPhysicalNamePolicy? namePolicy = null,
        IStorageScopeObserver? scopeObserver = null,
        GroundworkRuntimeSchemaAdmissionOptions? options = null,
        Action<GroundworkRuntimeSchemaAdmissionLogEntry>? schemaAdmissionLog = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        var target = await AdmitPhysicalAsync(
            connection,
            manifest,
            provider,
            namePolicy,
            options,
            schemaAdmissionLog,
            cancellationToken);
        return new SqlitePhysicalDocumentStore(
            connection,
            manifest,
            target.Routes,
            access,
            scopeObserver,
            target.Provider);
    }

    /// <summary>
    /// Opens a file-backed physical document store after inspect-only runtime schema admission.
    /// Safe pending operations are applied only when
    /// <see cref="GroundworkRuntimeSchemaAdmissionOptions.AutoApplyOnStartup"/> is enabled.
    /// </summary>
    public static async Task<SqlitePhysicalDocumentStore> OpenPhysicalAsync(
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
        SqliteRelationalSessions.ValidateStatelessConnectionString(connectionString);
        await using var admissionConnection = SqliteConnectionFactory.Create(connectionString);
        var target = await AdmitPhysicalAsync(
            admissionConnection,
            manifest,
            provider,
            namePolicy,
            options,
            schemaAdmissionLog,
            cancellationToken);
        return new SqlitePhysicalDocumentStore(
            connectionString,
            manifest,
            target.Routes,
            access,
            scopeObserver,
            target.Provider);
    }

    private static Task<PhysicalSchemaTarget> AdmitPhysicalAsync(
        SqliteConnection connection,
        StorageManifest manifest,
        ProviderIdentity provider,
        IPhysicalNamePolicy? namePolicy,
        GroundworkRuntimeSchemaAdmissionOptions? options,
        Action<GroundworkRuntimeSchemaAdmissionLogEntry>? schemaAdmissionLog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return RelationalPhysicalStoreFactory.AdmitPhysicalAsync(
            manifest,
            provider,
            SqliteGroundworkCapabilities.PhysicalNames,
            namePolicy,
            new SqlitePhysicalSchemaExecutor(connection),
            options,
            schemaAdmissionLog,
            cancellationToken);
    }
}
