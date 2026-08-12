using Groundwork.Core.Manifests;
using Groundwork.Provider.Relational;
using Groundwork.Documents.Scoping;
using Groundwork.Relational.Documents;
using Npgsql;

namespace Groundwork.PostgreSql.Documents;

[Obsolete(
    "The portable document model is retired (ADR 0006). Use PostgreSqlDocumentStoreFactory.OpenPhysicalAsync "
    + "and execute declared bounded DocumentQuery plans; removal follows with the announced breaking cleanup.",
    DiagnosticId = "GW0005")]
public sealed class PostgreSqlDocumentStore : RelationalDocumentStore
{
    internal PostgreSqlDocumentStore(NpgsqlConnection connection, StorageManifest manifest, DocumentStoreAccess access, IStorageScopeObserver? scopeObserver = null)
        : base(connection, manifest, new PostgreSqlDocumentStoreDialect(), access, scopeObserver)
    {
    }

    internal PostgreSqlDocumentStore(RelationalSessionFactory sessions, StorageManifest manifest, DocumentStoreAccess access, IStorageScopeObserver? scopeObserver = null)
        : base(sessions, manifest, new PostgreSqlDocumentStoreDialect(), access, scopeObserver)
    {
    }

    internal PostgreSqlDocumentStore(string connectionString, StorageManifest manifest, DocumentStoreAccess access, IStorageScopeObserver? scopeObserver = null)
        : base(
            RelationalSessionFactory.Concurrent(() => new NpgsqlConnection(connectionString)),
            manifest,
            new PostgreSqlDocumentStoreDialect(),
            access,
            scopeObserver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    }
}
