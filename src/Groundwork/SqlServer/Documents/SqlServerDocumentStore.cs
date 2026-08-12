using Groundwork.Core.Manifests;
using Groundwork.Provider.Relational;
using Groundwork.Documents.Scoping;
using Groundwork.Relational.Documents;
using Microsoft.Data.SqlClient;

namespace Groundwork.SqlServer.Documents;

[Obsolete(
    "The portable document model is retired (ADR 0006). Use SqlServerDocumentStoreFactory.OpenPhysicalAsync "
    + "and execute declared bounded DocumentQuery plans; removal follows with the announced breaking cleanup.",
    DiagnosticId = "GW0005")]
public sealed class SqlServerDocumentStore : RelationalDocumentStore
{
    internal SqlServerDocumentStore(SqlConnection connection, StorageManifest manifest, DocumentStoreAccess access, IStorageScopeObserver? scopeObserver = null)
        : base(connection, manifest, new SqlServerDocumentStoreDialect(), access, scopeObserver)
    {
    }

    internal SqlServerDocumentStore(RelationalSessionFactory sessions, StorageManifest manifest, DocumentStoreAccess access, IStorageScopeObserver? scopeObserver = null)
        : base(sessions, manifest, new SqlServerDocumentStoreDialect(), access, scopeObserver)
    {
    }

    internal SqlServerDocumentStore(string connectionString, StorageManifest manifest, DocumentStoreAccess access, IStorageScopeObserver? scopeObserver = null)
        : base(
            RelationalSessionFactory.Concurrent(() => new SqlConnection(connectionString)),
            manifest,
            new SqlServerDocumentStoreDialect(),
            access,
            scopeObserver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    }
}
