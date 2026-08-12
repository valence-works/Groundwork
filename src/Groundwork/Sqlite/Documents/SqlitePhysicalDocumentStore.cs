using System.Data.Common;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Provider.Relational;
using Groundwork.Relational.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Documents;

/// <summary>SQLite document store that executes compiled physical storage routes.</summary>
public sealed class SqlitePhysicalDocumentStore : RelationalPhysicalDocumentStore
{
    public SqlitePhysicalDocumentStore(
        SqliteConnection connection,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : base(connection, manifest, routes, new SqlitePhysicalDocumentDialect(), access, scopeObserver)
    {
    }

    /// <summary>
    /// Creates a store bound to an exactly admitted physical-schema target. Mutations fence against the target's
    /// compact durable fingerprint instead of re-materializing the full applied-state snapshot on every write.
    /// </summary>
    public SqlitePhysicalDocumentStore(
        SqliteConnection connection,
        StorageManifest manifest,
        PhysicalSchemaTarget admittedTarget,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : base(
            connection,
            manifest,
            RequireMatchingTarget(manifest, admittedTarget).Routes,
            new SqlitePhysicalDocumentDialect(),
            access,
            scopeObserver,
            admittedTarget.Provider,
            admittedTarget.Fingerprint)
    {
    }

    internal SqlitePhysicalDocumentStore(
        SqliteConnection connection,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver,
        ProviderIdentity physicalSchemaProvider)
        : base(
            connection,
            manifest,
            routes,
            new SqlitePhysicalDocumentDialect(),
            access,
            scopeObserver,
            physicalSchemaProvider)
    {
        ArgumentNullException.ThrowIfNull(physicalSchemaProvider);
    }

    internal SqlitePhysicalDocumentStore(
        SqliteConnection connection,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access,
        Func<DbTransaction, IRelationalPhysicalMutationTransaction> createMutationTransaction,
        IStorageScopeObserver? scopeObserver = null)
        : base(
            connection,
            manifest,
            routes,
            new SqlitePhysicalDocumentDialect(),
            access,
            createMutationTransaction,
            scopeObserver)
    {
    }

    internal SqlitePhysicalDocumentStore(
        RelationalSessionFactory sessions,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : base(sessions, manifest, routes, new SqlitePhysicalDocumentDialect(), access, scopeObserver)
    {
    }

    public SqlitePhysicalDocumentStore(
        string connectionString,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : base(
            SqliteRelationalSessions.CreateSerializedImmediate(connectionString),
            manifest,
            routes,
            new SqlitePhysicalDocumentDialect(),
            access,
            scopeObserver)
    {
    }

    /// <summary>
    /// Creates one reusable, concurrently callable store whose operations own independent SQLite connections.
    /// The supplied target is the exact startup-admitted schema proof used to fence every mutation.
    /// </summary>
    public static SqlitePhysicalDocumentStore CreateConcurrent(
        string connectionString,
        StorageManifest manifest,
        PhysicalSchemaTarget admittedTarget,
        DocumentStoreAccess access,
        SqliteConnectionPragmaOptions? connectionPragmas = null,
        IStorageScopeObserver? scopeObserver = null) =>
        new(
            SqliteRelationalSessions.CreateConcurrentImmediate(connectionString, connectionPragmas),
            manifest,
            RequireMatchingTarget(manifest, admittedTarget),
            access,
            scopeObserver);

    /// <summary>
    /// Creates a reusable store bound to an exactly admitted physical-schema target.
    /// </summary>
    public SqlitePhysicalDocumentStore(
        string connectionString,
        StorageManifest manifest,
        PhysicalSchemaTarget admittedTarget,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : base(
            SqliteRelationalSessions.CreateSerializedImmediate(connectionString),
            manifest,
            RequireMatchingTarget(manifest, admittedTarget).Routes,
            new SqlitePhysicalDocumentDialect(),
            access,
            scopeObserver,
            admittedTarget.Provider,
            admittedTarget.Fingerprint)
    {
    }

    internal SqlitePhysicalDocumentStore(
        string connectionString,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver,
        ProviderIdentity physicalSchemaProvider)
        : base(
            SqliteRelationalSessions.CreateSerializedImmediate(connectionString),
            manifest,
            routes,
            new SqlitePhysicalDocumentDialect(),
            access,
            scopeObserver,
            physicalSchemaProvider)
    {
        ArgumentNullException.ThrowIfNull(physicalSchemaProvider);
    }

    private SqlitePhysicalDocumentStore(
        RelationalSessionFactory sessions,
        StorageManifest manifest,
        PhysicalSchemaTarget admittedTarget,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver)
        : base(
            sessions,
            manifest,
            admittedTarget.Routes,
            new SqlitePhysicalDocumentDialect(),
            access,
            scopeObserver,
            admittedTarget.Provider,
            admittedTarget.Fingerprint)
    {
    }

    private static PhysicalSchemaTarget RequireMatchingTarget(
        StorageManifest manifest,
        PhysicalSchemaTarget admittedTarget)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(admittedTarget);
        if (admittedTarget.ManifestIdentity != manifest.Identity ||
            admittedTarget.ManifestVersion != manifest.Version)
        {
            throw new ArgumentException(
                "The admitted physical-schema target must belong to the supplied storage manifest.",
                nameof(admittedTarget));
        }

        return admittedTarget;
    }
}

internal sealed class SqlitePhysicalDocumentDialect : RelationalPhysicalDocumentDialect
{
    public override bool SupportsAtomicCollectionMutationMaintenance => true;

    private const int ConstraintPrimaryKey = 1555;
    private const int ConstraintUnique = 2067;

    public override int MaxParameters => 999;

    public override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public override bool IsUniqueConstraintException(DbException exception) =>
        exception is SqliteException
        {
            SqliteExtendedErrorCode: ConstraintPrimaryKey or ConstraintUnique
        };
    public override bool IsMissingPhysicalSchemaStateException(DbException exception) =>
        exception is SqliteException { SqliteErrorCode: 1 } sqlite &&
        sqlite.Message.Contains("no such table", StringComparison.Ordinal);

    public override string JsonValue(string canonicalJsonExpression, string stablePath)
    {
        var path = "$." + string.Join('.', stablePath.Split('.').Select(segment => $"\"{segment.Replace("\"", "\\\"")}\""));
        return $"json_extract({canonicalJsonExpression}, '{path.Replace("'", "''")}')";
    }

    public override string SetJsonValue(
        string canonicalJsonExpression,
        string jsonPathParameter,
        string jsonValueParameter) =>
        $"json_set({canonicalJsonExpression}, {jsonPathParameter}, json({jsonValueParameter}))";

    public override string NormalizeQueryExpression(
        string expression,
        PhysicalQueryFieldSource source,
        IndexValueKind valueKind) => valueKind switch
        {
            IndexValueKind.Boolean when source == PhysicalQueryFieldSource.CanonicalJsonPath =>
                $"CAST({expression} AS INTEGER)",
            _ => expression
        };

    public override object? ConvertProjectionValue(object? value, ProjectedColumnDefinition definition) =>
        SqlitePhysicalValueConverter.ToStorage(value, definition);

    public override object ConvertQueryValue(
        string value,
        IndexValueKind valueKind,
        ProjectedColumnDefinition definition) =>
        SqlitePhysicalValueConverter.FromQuery(value, valueKind, definition);

    public override string Contains(string fieldExpression, string parameterExpression) =>
        $"LOWER({fieldExpression}) LIKE LOWER({parameterExpression}) ESCAPE '\\'";

    public override string StartsWith(string fieldExpression, string parameterExpression) =>
        $"LOWER({fieldExpression}) LIKE LOWER({parameterExpression}) ESCAPE '\\'";

    public override string ApplyOffsetPage(string selectSql, string takeParameter, string skipParameter) =>
        $"{selectSql} LIMIT {takeParameter} OFFSET {skipParameter};";

    public override string ApplyFirst(string selectSql) => $"{selectSql} LIMIT 1;";

    public override string QuerySource(string tableIdentifier, string alias, string? indexIdentifier) =>
        indexIdentifier is null
            ? $"{QuoteIdentifier(tableIdentifier)} {alias}"
            : $"{QuoteIdentifier(tableIdentifier)} AS {alias} INDEXED BY {QuoteIdentifier(indexIdentifier)}";

    /// <summary>
    /// SQLite pins an index with <c>INDEXED BY</c>, and an index declared
    /// <see cref="MissingValueBehavior.Excluded"/> is emitted as a partial index. SQLite only uses a
    /// partial index for a query whose own predicate implies the index predicate, so the same soundness
    /// rule as SQL Server applies here.
    /// </summary>
    public override IReadOnlyList<string> HintedIndexNullExcludedColumns(
        ExecutableStorageRoute route,
        ExecutablePhysicalIndexRoute index) =>
        PhysicalIndexNullExclusion.Columns(route, index);

    public override string CreateMutationSelectionTable(
        string tableExpression,
        string documentKindColumn,
        string storageScopeColumn,
        string documentIdColumn,
        string documentIdComparisonColumn,
        string documentIdLookupColumn,
        string documentVersionColumn,
        string documentIncarnationColumn) =>
        $"CREATE TEMP TABLE {tableExpression} (" +
        $"{QuoteIdentifier(documentKindColumn)} TEXT NOT NULL, " +
        $"{QuoteIdentifier(storageScopeColumn)} TEXT NOT NULL, " +
        $"{QuoteIdentifier(documentIdColumn)} TEXT NOT NULL, " +
        $"{QuoteIdentifier(documentIdComparisonColumn)} TEXT NOT NULL, " +
        $"{QuoteIdentifier(documentIdLookupColumn)} TEXT NOT NULL, " +
        $"{QuoteIdentifier(documentVersionColumn)} INTEGER NOT NULL, " +
        $"{QuoteIdentifier(documentIncarnationColumn)} TEXT NOT NULL, " +
        $"PRIMARY KEY ({QuoteIdentifier(documentKindColumn)}, {QuoteIdentifier(storageScopeColumn)}, {QuoteIdentifier(documentIdLookupColumn)})) WITHOUT ROWID;";

    public override string DeleteCollectionByMutationSelection(
        string tableExpression,
        string alias,
        string selectionTableExpression,
        IReadOnlyList<RelationalPhysicalIdentityJoinPart> exactIdentity,
        IReadOnlyList<RelationalPhysicalIdentityJoinPart> ownerKeyPrefix)
    {
        var ownerPrefix = string.Join(
            ", ",
            ownerKeyPrefix.Select(part =>
                $"{part.LeftAlias}.{QuoteIdentifier(part.LeftColumnIdentifier)}"));
        var selectionPrefix = string.Join(
            ", ",
            ownerKeyPrefix.Select(part =>
                $"{part.RightAlias}.{QuoteIdentifier(part.RightColumnIdentifier)}"));
        return $"DELETE FROM {tableExpression} AS {alias} " +
               $"WHERE ({ownerPrefix}) IN (SELECT {selectionPrefix} FROM {selectionTableExpression} AS s) " +
               $"AND EXISTS (SELECT 1 FROM {selectionTableExpression} AS s WHERE {RenderIdentityJoin(exactIdentity)});";
    }

    public override ValueTask<DbTransaction> BeginMutationTransactionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<DbTransaction>(
            ((SqliteConnection)connection).BeginTransaction(deferred: false));
    }

    public override ProviderIdentity PhysicalSchemaProvider => SqliteGroundworkCapabilities.Provider;

    public override ValueTask<IAsyncDisposable> AcquireSchemaTransitionLeaseAsync(
        DbConnection connection,
        PhysicalSchemaTargetIdentity target,
        CancellationToken cancellationToken) =>
        SqlitePhysicalSchemaTransitionLock.AcquireSharedAsync(
            connection.ConnectionString,
            target,
            cancellationToken);
}
