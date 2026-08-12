using Groundwork.Core.Manifests;
using Groundwork.Relational.Documents;
using Groundwork.Provider.Relational;
using Groundwork.Documents.Scoping;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Documents;

[Obsolete(
    "The portable document model is retired (ADR 0006). Use SqliteDocumentStoreFactory.OpenPhysicalAsync "
    + "and execute declared bounded DocumentQuery plans; removal follows with the announced breaking cleanup.",
    DiagnosticId = "GW0005")]
public sealed class SqliteDocumentStore : RelationalDocumentStore
{
    internal SqliteDocumentStore(SqliteConnection connection, StorageManifest manifest, DocumentStoreAccess access, IStorageScopeObserver? scopeObserver = null)
        : base(connection, manifest, new SqliteDocumentStoreDialect(), access, scopeObserver)
    {
    }

    internal SqliteDocumentStore(RelationalSessionFactory sessions, StorageManifest manifest, DocumentStoreAccess access, IStorageScopeObserver? scopeObserver = null)
        : base(sessions, manifest, new SqliteDocumentStoreDialect(), access, scopeObserver)
    {
    }

    internal SqliteDocumentStore(string connectionString, StorageManifest manifest, DocumentStoreAccess access, IStorageScopeObserver? scopeObserver = null)
        : base(
            SqliteRelationalSessions.CreateSerialized(connectionString),
            manifest,
            new SqliteDocumentStoreDialect(),
            access,
            scopeObserver)
    {
    }
}
