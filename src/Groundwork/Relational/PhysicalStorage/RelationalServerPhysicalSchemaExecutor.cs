using System.Data;
using System.Data.Common;
using System.Globalization;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Text;
using Groundwork.Documents.Store;
using Groundwork.Provider.Relational;
using Groundwork.Relational.Documents;
using Groundwork.Relational.Physicalization;
using static Groundwork.Relational.PhysicalStorage.RelationalServerPhysicalSchemaDialect;

namespace Groundwork.Relational.PhysicalStorage;

/// <summary>
/// Shared server-relational executor for the physical-schema protocol. Provider dialects own DDL,
/// metadata inspection, advisory locks, and native value adaptation; operation ordering, durable
/// acknowledgements, CAS state, backfill, and exact compatibility checks remain common.
/// </summary>
public class RelationalServerPhysicalSchemaExecutor : IPhysicalSchemaExecutor, IPhysicalSchemaHistoryInspector
{
    private const string BootstrapLockResource = "groundwork:physical:bootstrap";
    private readonly Func<DbConnection> createLockConnection;
    private readonly RelationalServerPhysicalSchemaDialect dialect;
    private readonly Func<PhysicalSchemaOperation, CancellationToken, Task>? beforeOperationEvidence;
    private readonly Func<PhysicalSchemaAppliedState, CancellationToken, Task>? beforeAppliedStateFence;

    public RelationalServerPhysicalSchemaExecutor(
        Func<DbConnection> createLockConnection,
        RelationalServerPhysicalSchemaDialect dialect)
        : this(createLockConnection, dialect, null, null)
    {
    }

    protected RelationalServerPhysicalSchemaExecutor(
        Func<DbConnection> createLockConnection,
        RelationalServerPhysicalSchemaDialect dialect,
        Func<PhysicalSchemaOperation, CancellationToken, Task>? beforeOperationEvidence,
        Func<PhysicalSchemaAppliedState, CancellationToken, Task>? beforeAppliedStateFence)
    {
        this.createLockConnection = createLockConnection ?? throw new ArgumentNullException(nameof(createLockConnection));
        this.dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        this.beforeOperationEvidence = beforeOperationEvidence;
        this.beforeAppliedStateFence = beforeAppliedStateFence;
    }

    public async ValueTask<IPhysicalSchemaApplicationLock> AcquireApplicationLockAsync(
        PhysicalSchemaTargetIdentity target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var connection = createLockConnection()
            ?? throw new InvalidOperationException("The physical-schema lock connection factory returned null.");
        string? acquiredResource = null;
        try
        {
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken);
            await dialect.AcquireApplicationLockAsync(connection, BootstrapLockResource, cancellationToken);
            try
            {
                await dialect.EnsureInfrastructureAsync(connection, cancellationToken);
            }
            catch (Exception exception)
            {
                try
                {
                    await dialect.ReleaseApplicationLockAsync(connection, BootstrapLockResource, CancellationToken.None);
                }
                catch (Exception cleanupFailure)
                {
                    RelationalCleanupFailures.Attach(exception, cleanupFailure);
                }
                throw;
            }
            await dialect.ReleaseApplicationLockAsync(connection, BootstrapLockResource, CancellationToken.None);
            var resource = RelationalPhysicalSchemaLockResource.For(target);
            await dialect.AcquireApplicationLockAsync(connection, resource, cancellationToken);
            acquiredResource = resource;
            var owner = Guid.NewGuid().ToString("N");
            var fence = await dialect.AcquireFenceAsync(connection, target, owner, cancellationToken);
            var sessionId = await dialect.ReadServerSessionIdAsync(connection, cancellationToken);
            return new ApplicationLock(target, connection, resource, owner, fence, sessionId, dialect);
        }
        catch (Exception exception)
        {
            if (acquiredResource is not null && connection.State == ConnectionState.Open)
            {
                try
                {
                    await dialect.ReleaseApplicationLockAsync(connection, acquiredResource, CancellationToken.None);
                }
                catch (Exception cleanupFailure)
                {
                    RelationalCleanupFailures.Attach(exception, cleanupFailure);
                }
            }
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                RelationalCleanupFailures.Attach(exception, cleanupFailure);
            }
            if (cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
            {
                throw new OperationCanceledException(
                    "Physical-schema application-lock acquisition was canceled.",
                    exception,
                    cancellationToken);
            }
            throw;
        }
    }

    public async ValueTask<PhysicalSchemaHistoryState> ReadHistoryAsync(
        PhysicalSchemaTargetIdentity target,
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken)
    {
        var lease = RequireApplicationLock(applicationLock, target);
        return await lease.ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await dialect.AssertFenceAsync(connection, transaction, target, lease.Owner, lease.Fence, ct);
            await using var command = Command(connection, transaction, """
                SELECT applied_state_json
                FROM groundwork_physical_schema_state
                WHERE manifest_id = @manifestId AND provider_name = @providerName;
                """);
            Add(command, "manifestId", target.ManifestIdentity.Value);
            Add(command, "providerName", target.ProviderName);
            var json = await command.ExecuteScalarAsync(ct) as string;
            await transaction.CommitAsync(ct);
            return json is null
                ? PhysicalSchemaHistoryState.Empty
                : PhysicalSchemaHistoryState.FromApplied(PhysicalSchemaAppliedStateSerializer.Deserialize(json));
        }, cancellationToken);
    }

    public async ValueTask<PhysicalSchemaInspectionResult> InspectHistoryAsync(
        PhysicalSchemaTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var connection = createLockConnection()
            ?? throw new InvalidOperationException("The physical-schema inspection connection factory returned null.");
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await dialect.TableExistsAsync(
                connection,
                transaction,
                "groundwork_physical_schema_state",
                cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new PhysicalSchemaInspectionResult(PhysicalSchemaHistoryState.Empty, IsAppliedSchemaValid: true);
        }

        await using var command = Command(connection, transaction, """
            SELECT applied_state_json
            FROM groundwork_physical_schema_state
            WHERE manifest_id = @manifestId AND provider_name = @providerName;
            """);
        Add(command, "manifestId", target.ManifestIdentity.Value);
        Add(command, "providerName", target.Provider.Name);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        var history = json is null
            ? PhysicalSchemaHistoryState.Empty
            : PhysicalSchemaHistoryState.FromApplied(PhysicalSchemaAppliedStateSerializer.Deserialize(json));
        var isAppliedSchemaValid = true;
        if (history.AppliedState is { } appliedState)
        {
            try
            {
                await ValidateAsync(
                    connection,
                    transaction,
                    ValidatePhysicalSchemaOperation.ForAppliedState(appliedState),
                    cancellationToken);
            }
            catch (InvalidOperationException)
            {
                isAppliedSchemaValid = false;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new PhysicalSchemaInspectionResult(history, isAppliedSchemaValid);
    }

    public async ValueTask<PhysicalSchemaOperationAcknowledgement> ApplyOperationAsync(
        PhysicalSchemaTargetIdentity target,
        PhysicalSchemaOperation operation,
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var lease = RequireApplicationLock(applicationLock, target);
        return await lease.ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var prior = await ReadOperationAsync(connection, transaction, target, operation.Identity, ct);
            if (prior is not null)
            {
                if (!string.Equals(prior.Value.Fingerprint, operation.Fingerprint, StringComparison.Ordinal))
                    throw new PhysicalSchemaFingerprintConflictException(operation.Identity, operation.Fingerprint, prior.Value.Fingerprint);
                if (operation is ValidatePhysicalSchemaOperation ||
                    operation is BackfillCanonicalJsonOperation &&
                    !await IsOperationPublishedAsync(connection, transaction, target, operation, ct))
                    await ApplyOperationCoreAsync(connection, transaction, operation, ct);
                await dialect.AssertFenceAsync(connection, transaction, target, lease.Owner, lease.Fence, ct);
                await transaction.CommitAsync(ct);
                return new PhysicalSchemaOperationAcknowledgement(operation.Identity, prior.Value.Fingerprint, prior.Value.AppliedAt);
            }

            await ApplyOperationCoreAsync(connection, transaction, operation, ct);
            if (beforeOperationEvidence is not null)
                await beforeOperationEvidence(operation, ct);
            await dialect.AssertFenceAsync(connection, transaction, target, lease.Owner, lease.Fence, ct);
            var appliedAt = DateTimeOffset.UtcNow;
            await using (var command = Command(connection, transaction, """
                INSERT INTO groundwork_physical_schema_operations
                    (manifest_id, provider_name, operation_id, operation_fingerprint, applied_utc)
                VALUES (@manifestId, @providerName, @identity, @fingerprint, @appliedUtc);
                """))
            {
                Add(command, "manifestId", target.ManifestIdentity.Value);
                Add(command, "providerName", target.ProviderName);
                Add(command, "identity", operation.Identity);
                Add(command, "fingerprint", operation.Fingerprint);
                Add(command, "appliedUtc", appliedAt);
                await command.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            var durable = await ReadOperationAsync(connection, null, target, operation.Identity, ct)
                ?? throw new InvalidOperationException($"Physical operation '{operation.Identity}' was not durably recorded.");
            if (!string.Equals(durable.Fingerprint, operation.Fingerprint, StringComparison.Ordinal))
                throw new PhysicalSchemaFingerprintConflictException(operation.Identity, operation.Fingerprint, durable.Fingerprint);
            return new PhysicalSchemaOperationAcknowledgement(operation.Identity, durable.Fingerprint, durable.AppliedAt);
        }, cancellationToken);
    }

    public async ValueTask RecordAppliedStateAsync(
        PhysicalSchemaAppliedState state,
        string? expectedAppliedTargetFingerprint,
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var target = new PhysicalSchemaTargetIdentity(state.ManifestIdentity, state.Provider.Name);
        var lease = RequireApplicationLock(applicationLock, target);
        await lease.ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            if (beforeAppliedStateFence is not null)
                await beforeAppliedStateFence(state, ct);
            await dialect.AssertFenceAsync(
                connection,
                transaction,
                target,
                lease.Owner,
                lease.Fence,
                ct);
            var current = await ReadTargetFingerprintAsync(connection, transaction, state.ManifestIdentity.Value, state.Provider.Name, ct);
            if (current == state.TargetFingerprint)
            {
                await transaction.CommitAsync(ct);
                return true;
            }
            if (!string.Equals(current, expectedAppliedTargetFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Physical schema applied-state compare-and-swap failed. Expected '{expectedAppliedTargetFingerprint ?? "<empty>"}', found '{current ?? "<empty>"}'.");
            }

            var sql = current is null
                ? """
                  INSERT INTO groundwork_physical_schema_state
                      (manifest_id, provider_name, target_fingerprint, applied_state_json)
                  VALUES (@manifestId, @providerName, @fingerprint, @json);
                  """
                : """
                  UPDATE groundwork_physical_schema_state
                  SET target_fingerprint = @fingerprint, applied_state_json = @json
                  WHERE manifest_id = @manifestId AND provider_name = @providerName
                    AND target_fingerprint = @expected;
                  """;
            await using var command = Command(connection, transaction, sql);
            Add(command, "manifestId", state.ManifestIdentity.Value);
            Add(command, "providerName", state.Provider.Name);
            Add(command, "fingerprint", state.TargetFingerprint);
            Add(command, "json", PhysicalSchemaAppliedStateSerializer.Serialize(state));
            if (current is not null)
                Add(command, "expected", expectedAppliedTargetFingerprint!);
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Physical schema applied-state compare-and-swap lost a concurrent update.");
            await transaction.CommitAsync(ct);
            return true;
        }, cancellationToken);
    }

    private async Task ApplyOperationCoreAsync(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaOperation operation,
        CancellationToken ct)
    {
        switch (operation)
        {
            case CreatePrimaryStorageOperation create:
                await CreatePrimaryAsync(connection, transaction, create.Route, ct);
                break;
            case CreatePhysicalEntityStorageOperation create:
                await CreatePrimaryAsync(connection, transaction, create.Route, ct);
                break;
            case CreateLinkedStorageOperation create:
                await CreateLinkedAsync(connection, transaction, create.Route, ct);
                break;
            case CreateCollectionElementStorageOperation create:
                await CreateCollectionElementAsync(connection, transaction, create.Storage, ct);
                break;
            case AddProjectedColumnOperation add:
                ValidateRoute(add.Route);
                await AddColumnAsync(connection, transaction, add.Storage.Name.Identifier, add.Column, ct);
                break;
            case FinalizeProjectedColumnOperation finalize:
                ValidateRoute(finalize.Route);
                await FinalizeColumnAsync(connection, transaction, finalize.Storage.Name.Identifier, finalize.Column, ct);
                break;
            case CreatePhysicalIndexOperation create:
                ValidateRoute(create.Route);
                await CreateIndexAsync(connection, transaction, create.Route, create.Storage.Name.Identifier, create.Index, ct);
                break;
            case RebuildPhysicalIndexOperation rebuild:
                ValidateRoute(rebuild.Route);
                await RebuildIndexAsync(connection, transaction, rebuild.Route, rebuild.Storage.Name.Identifier, rebuild.Index, ct);
                break;
            case BackfillCanonicalJsonOperation backfill:
                if (backfill.Route is not null)
                    ValidateRoute(backfill.Route);
                await BackfillAsync(connection, transaction, backfill, ct);
                break;
            case ValidatePhysicalSchemaOperation validate:
                await ValidateAsync(connection, transaction, validate, ct);
                break;
            default:
                throw new InvalidOperationException($"{dialect.ProviderDisplayName} cannot execute physical schema operation '{operation.Kind}'.");
        }
    }

    private async Task CreatePrimaryAsync(DbConnection connection, DbTransaction transaction, ExecutableStorageRoute route, CancellationToken ct)
    {
        ValidateRoute(route);
        var envelope = route.Envelope;
        var identity = dialect.IdentityLayout(
            PrimaryIdentityColumns(route),
            route.PrimaryKey.Columns.Select(column => column.Identifier).ToArray());
        var columns = new[]
        {
            dialect.EnvelopeColumn(envelope.DocumentKind.Identifier, RelationalEnvelopeColumnKind.DocumentKind),
            dialect.EnvelopeColumn(envelope.StorageScope.Identifier, RelationalEnvelopeColumnKind.StorageScope),
            dialect.EnvelopeColumn(envelope.Identity.OriginalId.Identifier, RelationalEnvelopeColumnKind.Id),
            dialect.EnvelopeColumn(envelope.Identity.ComparisonKey.Identifier, RelationalEnvelopeColumnKind.IdentityComparison),
            dialect.EnvelopeColumn(envelope.Identity.LookupKey.Identifier, RelationalEnvelopeColumnKind.IdentityLookup),
            dialect.EnvelopeColumn(envelope.SchemaVersion.Identifier, RelationalEnvelopeColumnKind.SchemaVersion),
            dialect.EnvelopeColumn(envelope.Version.Identifier, RelationalEnvelopeColumnKind.Version),
            dialect.EnvelopeColumn(envelope.CanonicalJson.Identifier, RelationalEnvelopeColumnKind.CanonicalJson),
            dialect.EnvelopeColumn(RelationalPhysicalStorageColumns.CreatedUtc, RelationalEnvelopeColumnKind.Timestamp),
            dialect.EnvelopeColumn(RelationalPhysicalStorageColumns.UpdatedUtc, RelationalEnvelopeColumnKind.Timestamp)
        }.Concat(identity.ProviderColumns.Select(column => column.Definition)).ToArray();
        if (!await dialect.TableExistsAsync(connection, transaction, route.PrimaryStorage.Name.Identifier, ct))
        {
            await ExecuteAsync(connection, transaction, dialect.CreateTableSql(
                route.PrimaryStorage.Name.Identifier,
                columns,
                identity.PrimaryKey), ct);
        }
        await ValidatePrimaryAsync(connection, transaction, route, ct);
    }

    private async Task CreateLinkedAsync(DbConnection connection, DbTransaction transaction, ExecutableStorageRoute route, CancellationToken ct)
    {
        ValidateRoute(route);
        var relationship = route.LinkedRelationship!;
        var key = route.AuxiliaryKey ?? throw new InvalidOperationException("Linked storage requires an auxiliary key.");
        var identity = dialect.IdentityLayout(
            LinkedIdentityColumns(route),
            key.Columns.Select(column => column.Identifier).ToArray());
        var columns = new[]
        {
            dialect.EnvelopeColumn(relationship.DocumentKind.Identifier, RelationalEnvelopeColumnKind.DocumentKind),
            dialect.EnvelopeColumn(relationship.StorageScope.Identifier, RelationalEnvelopeColumnKind.StorageScope),
            dialect.EnvelopeColumn(relationship.Identity.OriginalId.Identifier, RelationalEnvelopeColumnKind.Id),
            dialect.EnvelopeColumn(relationship.Identity.ComparisonKey.Identifier, RelationalEnvelopeColumnKind.IdentityComparison),
            dialect.EnvelopeColumn(relationship.Identity.LookupKey.Identifier, RelationalEnvelopeColumnKind.IdentityLookup)
        }.Concat(identity.ProviderColumns.Select(column => column.Definition)).ToArray();
        var table = route.LinkedIndexStorage!.Name.Identifier;
        if (!await dialect.TableExistsAsync(connection, transaction, table, ct))
            await ExecuteAsync(connection, transaction, dialect.CreateTableSql(table, columns, identity.PrimaryKey), ct);
        await ValidateLinkedAsync(connection, transaction, route, ct);
    }

    private async Task CreateCollectionElementAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableCollectionElementStorageRoute storage,
        CancellationToken ct)
    {
        dialect.ValidateCollectionElementStorage(storage);
        var table = storage.Storage.Name.Identifier;
        if (!await dialect.TableExistsAsync(connection, transaction, table, ct))
            await ExecuteAsync(connection, transaction, dialect.CreateCollectionElementTableSql(storage), ct);
        await ValidateCollectionElementAsync(connection, transaction, storage, ct);
    }

    private async Task AddColumnAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        ExecutableProjectedColumnRoute column,
        CancellationToken ct)
    {
        dialect.Validate(column.Definition);
        var existing = await dialect.ReadColumnsAsync(connection, transaction, table, ct);
        var staged = column.Definition.IsNullable ? column.Definition : column.Definition with { IsNullable = true };
        if (!existing.ContainsKey(column.Column.Identifier))
            await ExecuteAsync(connection, transaction, dialect.AddColumnSql(table, column.Column.Identifier, staged), ct);
        await ValidateProjectedColumnAsync(connection, transaction, table, column.Column.Identifier, staged, ct);
    }

    private async Task FinalizeColumnAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        ExecutableProjectedColumnRoute column,
        CancellationToken ct)
    {
        if (column.Definition.IsNullable)
        {
            await ValidateProjectedColumnAsync(connection, transaction, table, column.Column.Identifier, column.Definition, ct);
            return;
        }
        var columns = await dialect.ReadColumnsAsync(connection, transaction, table, ct);
        if (!columns.TryGetValue(column.Column.Identifier, out var found))
            throw new InvalidOperationException($"Projected column '{table}.{column.Column.Identifier}' is missing.");
        if (!found.IsNullable)
        {
            await ValidateProjectedColumnAsync(connection, transaction, table, column.Column.Identifier, column.Definition, ct);
            return;
        }
        await using (var count = Command(connection, transaction,
                         $"SELECT COUNT(*) FROM {dialect.QuoteIdentifier(table)} WHERE {dialect.QuoteIdentifier(column.Column.Identifier)} IS NULL;"))
        {
            if (Convert.ToInt64(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 0)
                throw new InvalidDataException($"Projected column '{table}.{column.Column.Identifier}' cannot be made required because canonical backfill left null values.");
        }
        await ExecuteAsync(connection, transaction, dialect.FinalizeColumnSql(table, column.Column.Identifier, column.Definition), ct);
        await ValidateProjectedColumnAsync(connection, transaction, table, column.Column.Identifier, column.Definition, ct);
    }

    private async Task CreateIndexAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableStorageRoute route,
        string table,
        ExecutablePhysicalIndexRoute index,
        CancellationToken ct)
    {
        var excludedColumns = PhysicalIndexNullExclusion.Columns(route, index);
        var existing = await dialect.ReadIndexAsync(connection, transaction, table, index.Name.Identifier, ct);
        if (existing is null)
            await ExecuteAsync(connection, transaction, dialect.CreateIndexSql(table, index, dialect.IndexKeyColumns(route, index), excludedColumns), ct);
        await ValidateIndexAsync(connection, transaction, route, table, index, ct);
    }

    /// <summary>
    /// Replaces the applied null-excluding index with the widened definition that supersedes it.
    /// </summary>
    /// <remarks>
    /// Row exclusion is a filtered index, so widening it means dropping the object and creating it again;
    /// creation alone cannot reach it. The live index has to be exactly the shape Groundwork emitted for
    /// the narrower definition before it is dropped — one carrying this name but a shape Groundwork never
    /// wrote is drift, and dropping it would destroy something nobody asked to replace. Anything else is
    /// left alone for creation and validation to reject in their own words, which also makes a retry after
    /// a lost acknowledgement a no-op: the index is already the widened shape by then. Both dialects run
    /// DDL inside this operation's transaction, so a failure after the drop restores the original index
    /// and no reader ever observes the index missing.
    /// </remarks>
    private async Task RebuildIndexAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableStorageRoute route,
        string table,
        ExecutablePhysicalIndexRoute index,
        CancellationToken ct)
    {
        var superseded = dialect.IndexFilter(
            index,
            PhysicalIndexNullExclusion.Columns(route, index, MissingValueBehavior.Excluded));
        var existing = await dialect.ReadIndexAsync(connection, transaction, table, index.Name.Identifier, ct);
        if (existing is not null && IndexMatches(existing, index, dialect.IndexKeyColumns(route, index), superseded))
            await ExecuteAsync(connection, transaction, dialect.DropIndexSql(table, index.Name.Identifier), ct);
        await CreateIndexAsync(connection, transaction, route, table, index, ct);
    }

    /// <summary>
    /// Whether the live index is exactly <paramref name="expected"/> carrying <paramref name="expectedFilter"/>.
    /// </summary>
    private static bool IndexMatches(
        RelationalPhysicalIndexMetadata actual,
        ExecutablePhysicalIndexRoute expected,
        IReadOnlyList<RelationalPhysicalIndexKeyColumn> expectedColumns,
        string? expectedFilter) =>
        IndexShapeMatches(actual, expected, expectedColumns, expectedFilter) &&
        actual.Columns.Zip(expectedColumns).All(pair =>
            pair.First.Name == pair.Second.Identifier &&
            pair.First.Direction == pair.Second.Direction);

    /// <summary>
    /// Whether the live index agrees with <paramref name="expected"/> on everything but the identity and
    /// order of its columns, which validation reports one ordinal at a time.
    /// </summary>
    private static bool IndexShapeMatches(
        RelationalPhysicalIndexMetadata actual,
        ExecutablePhysicalIndexRoute expected,
        IReadOnlyList<RelationalPhysicalIndexKeyColumn> expectedColumns,
        string? expectedFilter) =>
        actual.IsUnique == expected.IsUnique &&
        actual.Columns.Count == expectedColumns.Count &&
        string.Equals(
            NormalizeIndexFilter(actual.Filter),
            NormalizeIndexFilter(expectedFilter),
            StringComparison.OrdinalIgnoreCase);

    private async Task BackfillAsync(
        DbConnection connection,
        DbTransaction transaction,
        BackfillCanonicalJsonOperation operation,
        CancellationToken ct)
    {
        var route = operation.Route ?? throw new InvalidOperationException($"{dialect.ProviderDisplayName} physical backfill requires an executable route.");
        if (operation.CollectionStorage is { } collection)
        {
            await ForEachCanonicalDocumentBatchAsync(connection, transaction, route, ct, async document =>
            {
                var identity = route.Envelope.Identity.Project(document.Id);
                var elements = RelationalPhysicalProjectionValues.ReadCollection(
                    document.CanonicalJson,
                    collection.Projection);
                await using (var delete = Command(
                                 connection,
                                 transaction,
                                 $"DELETE FROM {dialect.QuoteIdentifier(collection.Storage.Name.Identifier)} WHERE " +
                                 $"{dialect.QuoteIdentifier(collection.DocumentKind.Column.Identifier)} = @kind AND " +
                                 $"{dialect.QuoteIdentifier(collection.StorageScope.Column.Identifier)} = @scope AND " +
                                 $"{dialect.QuoteIdentifier(collection.IdLookupKey.Column.Identifier)} = @idLookup AND " +
                                 $"{dialect.QuoteIdentifier(collection.IdComparisonKey.Column.Identifier)} = @idComparison;"))
                {
                    Add(delete, "kind", route.Discriminator.Value);
                    Add(delete, "scope", document.Scope);
                    Add(delete, "idLookup", dialect.ConvertDocumentIdentityLookup(identity.LookupKey));
                    Add(delete, "idComparison", dialect.ConvertDocumentIdentityComparison(identity.ComparisonKey));
                    await delete.ExecuteNonQueryAsync(ct);
                }
                var columns = new[]
                {
                    collection.DocumentKind.Column.Identifier,
                    collection.StorageScope.Column.Identifier,
                    collection.IdComparisonKey.Column.Identifier,
                    collection.IdLookupKey.Column.Identifier,
                    collection.Ordinal.Column.Identifier,
                    collection.Value.Column.Identifier
                };
                foreach (var element in elements)
                {
                    await using var command = Command(
                        connection,
                        transaction,
                        $"INSERT INTO {dialect.QuoteIdentifier(collection.Storage.Name.Identifier)} " +
                        $"({string.Join(", ", columns.Select(dialect.QuoteIdentifier))}) VALUES " +
                        $"({string.Join(", ", Enumerable.Range(0, columns.Length).Select(index => $"@v{index}"))});");
                    Add(command, "v0", route.Discriminator.Value);
                    Add(command, "v1", document.Scope);
                    Add(command, "v2", dialect.ConvertDocumentIdentityComparison(identity.ComparisonKey));
                    Add(command, "v3", dialect.ConvertDocumentIdentityLookup(identity.LookupKey));
                    Add(command, "v4", element.Ordinal);
                    Add(command, "v5", dialect.ConvertStorageValue(element.Value, collection.Value.Definition));
                    await command.ExecuteNonQueryAsync(ct);
                }
            });
            return;
        }
        if (operation.Target == ExecutableStorageObjectRole.PrimaryStorage)
        {
            var selected = SelectBackfillColumns(route, operation, ExecutableStorageObjectRole.PrimaryStorage);
            await ForEachCanonicalDocumentBatchAsync(connection, transaction, route, ct, async document =>
            {
                if (selected.Length == 0)
                    return;
                var values = RelationalPhysicalProjectionValues.Read(document.CanonicalJson, selected);
                var identity = route.Envelope.Identity.Project(document.Id);
                var assignments = string.Join(", ", selected.Select((column, index) => $"{dialect.QuoteIdentifier(column.Column.Identifier)} = @v{index}"));
                await using var command = Command(connection, transaction,
                    $"UPDATE {dialect.QuoteIdentifier(route.PrimaryStorage.Name.Identifier)} SET {assignments} WHERE " +
                    dialect.ExactIdentityPredicate(
                    [
                        new(route.Discriminator.Column.Identifier, null, "@kind"),
                        new(route.ScopeKey.Column.Identifier, null, "@scope"),
                        new(route.Envelope.Identity.LookupKey.Identifier, null, "@idLookup"),
                        new(route.Envelope.Identity.ComparisonKey.Identifier, null, "@idComparison")
                    ]) + ";");
                for (var index = 0; index < selected.Length; index++)
                    Add(command, $"v{index}", dialect.ConvertStorageValue(values[selected[index].Definition.LogicalName], selected[index].Definition));
                Add(command, "kind", route.Discriminator.Value);
                Add(command, "scope", document.Scope);
                Add(command, "idLookup", dialect.ConvertDocumentIdentityLookup(identity.LookupKey));
                Add(command, "idComparison", dialect.ConvertDocumentIdentityComparison(identity.ComparisonKey));
                if (await command.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException($"Canonical backfill lost document '{document.Id}' in scope '{document.Scope}'.");
            });
            return;
        }

        var relationship = route.LinkedRelationship!;
        var linked = route.ProjectedColumns.Where(column => column.Target == ExecutableStorageObjectRole.LinkedIndexStorage).ToArray();
        await ForEachCanonicalDocumentBatchAsync(connection, transaction, route, ct, async document =>
        {
            var values = RelationalPhysicalProjectionValues.Read(document.CanonicalJson, linked);
            var identity = relationship.Identity.Project(document.Id);
            await ThrowIfLinkedIdentityCollisionAsync(
                connection,
                transaction,
                route,
                document.Scope,
                identity,
                ct);
            var relationColumns = new[]
            {
                relationship.DocumentKind.Identifier,
                relationship.StorageScope.Identifier,
                relationship.Identity.OriginalId.Identifier,
                relationship.Identity.ComparisonKey.Identifier,
                relationship.Identity.LookupKey.Identifier
            };
            var insertColumns = relationColumns.Concat(linked.Select(column => column.Column.Identifier)).ToArray();
            await using var command = Command(connection, transaction, dialect.UpsertLinkedSql(
                route.LinkedIndexStorage!.Name.Identifier,
                insertColumns,
                route.AuxiliaryKey!.Columns.Select(column => column.Identifier).ToArray(),
                linked.Select(column => column.Column.Identifier).ToArray()));
            Add(command, "v0", route.Discriminator.Value);
            Add(command, "v1", document.Scope);
            Add(command, "v2", dialect.ConvertDocumentIdentityOriginal(identity.OriginalValue));
            Add(command, "v3", dialect.ConvertDocumentIdentityComparison(identity.ComparisonKey));
            Add(command, "v4", dialect.ConvertDocumentIdentityLookup(identity.LookupKey));
            for (var index = 0; index < linked.Length; index++)
                Add(command, $"v{index + 5}", dialect.ConvertStorageValue(values[linked[index].Definition.LogicalName], linked[index].Definition));
            try
            {
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (DbException exception) when (dialect.IsUniqueConstraintException(exception))
            {
                await ThrowIfIdentityHashCollisionAsync(
                    connection,
                    transaction,
                    route.LinkedIndexStorage.Name.Identifier,
                    [
                        (relationship.DocumentKind.Identifier, route.Discriminator.Value, "collisionKind"),
                        (relationship.StorageScope.Identifier, document.Scope, "collisionScope"),
                        (relationship.Identity.LookupKey.Identifier,
                            dialect.ConvertDocumentIdentityLookup(identity.LookupKey),
                            "collisionLookup")
                    ],
                    ct);
                throw;
            }
        });
    }

    private async Task ThrowIfLinkedIdentityCollisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableStorageRoute route,
        string scope,
        PortableStringIdentityProjection identity,
        CancellationToken ct)
    {
        var relationship = route.LinkedRelationship!;
        await using var command = Command(
            connection,
            transaction,
            $"SELECT {dialect.QuoteIdentifier(relationship.DocumentId.Identifier)}, " +
            $"{dialect.QuoteIdentifier(relationship.Identity.ComparisonKey.Identifier)} " +
            $"FROM {dialect.QuoteIdentifier(route.LinkedIndexStorage!.Name.Identifier)} WHERE " +
            dialect.ExactIdentityPredicate(
            [
                new(relationship.DocumentKind.Identifier, null, "@collisionKind"),
                new(relationship.StorageScope.Identifier, null, "@collisionScope"),
                new(relationship.Identity.LookupKey.Identifier, null, "@collisionLookup")
            ]) + ";");
        Add(command, "collisionKind", route.Discriminator.Value);
        Add(command, "collisionScope", scope);
        Add(command, "collisionLookup", dialect.ConvertDocumentIdentityLookup(identity.LookupKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return;
        var retainedId = reader.GetString(0);
        if (!string.Equals(
                dialect.ReadDocumentIdentityComparison(reader, 1),
                identity.ComparisonKey,
                StringComparison.Ordinal))
        {
            throw new DocumentIdentityLookupCollisionException(
                route.Discriminator.Value,
                identity.OriginalValue,
                retainedId,
                identity.LookupKey);
        }
    }

    private async Task ThrowIfIdentityHashCollisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        IReadOnlyList<(string Column, object Value, string Parameter)> identity,
        CancellationToken ct)
    {
        var parts = identity.Select(item => new RelationalPhysicalIdentityPredicatePart(
            item.Column,
            null,
            $"@{item.Parameter}")).ToArray();
        var predicate = dialect.HashOnlyIdentityPredicate(parts);
        if (predicate is null)
            return;
        await using var command = Command(
            connection,
            transaction,
            $"SELECT {string.Join(", ", identity.Select(item => dialect.QuoteIdentifier(item.Column)))} FROM {dialect.QuoteIdentifier(table)} WHERE {predicate};");
        foreach (var item in identity)
            Add(command, item.Parameter, item.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return;
        var matches = identity.Select((item, index) =>
            dialect.PhysicalIdentityValueEquals(reader.GetValue(index), item.Value));
        if (!matches.All(match => match))
            throw new PhysicalIdentityHashCollisionException(table, identity.Select(item => item.Column).ToArray());
    }

    private async Task ForEachCanonicalDocumentBatchAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableStorageRoute route,
        CancellationToken ct,
        Func<CanonicalDocument, Task> action)
    {
        const int batchSize = 256;
        string? afterScope = null;
        string? afterId = null;
        while (true)
        {
            var batch = new List<CanonicalDocument>(batchSize);
            await using (var command = Command(connection, transaction, dialect.SelectCanonicalBatchSql(route, batchSize, afterScope is not null)))
            {
                Add(command, "kind", route.Discriminator.Value);
                if (afterScope is not null)
                {
                    Add(command, "afterScope", afterScope);
                    Add(command, "afterId", afterId!);
                }
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    batch.Add(new CanonicalDocument(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            foreach (var document in batch)
                await action(document);
            if (batch.Count < batchSize)
                return;
            afterScope = batch[^1].Scope;
            afterId = batch[^1].Id;
        }
    }

    private async Task ValidateAsync(DbConnection connection, DbTransaction transaction, ValidatePhysicalSchemaOperation operation, CancellationToken ct)
    {
        foreach (var route in operation.Routes)
        {
            ValidateRoute(route);
            await ValidatePrimaryAsync(connection, transaction, route, ct);
            if (route.LinkedIndexStorage is not null)
                await ValidateLinkedAsync(connection, transaction, route, ct);
            foreach (var storage in route.CollectionElementStorages)
                await ValidateCollectionElementAsync(connection, transaction, storage, ct);
            foreach (var column in route.ProjectedColumns)
            {
                if (column.Definition.Cardinality == ProjectionCardinality.CollectionElements)
                    continue;
                var table = column.Target == ExecutableStorageObjectRole.PrimaryStorage
                    ? route.PrimaryStorage.Name.Identifier
                    : route.LinkedIndexStorage!.Name.Identifier;
                await ValidateProjectedColumnAsync(connection, transaction, table, column.Column.Identifier, column.Definition, ct);
            }
            foreach (var index in route.Indexes)
            {
                var table = index.Target == ExecutableStorageObjectRole.PrimaryStorage
                    ? route.PrimaryStorage.Name.Identifier
                    : route.LinkedIndexStorage!.Name.Identifier;
                await ValidateIndexAsync(connection, transaction, route, table, index, ct);
            }
        }
    }

    private Task ValidatePrimaryAsync(DbConnection connection, DbTransaction transaction, ExecutableStorageRoute route, CancellationToken ct)
    {
        var envelope = route.Envelope;
        var identity = dialect.IdentityLayout(
            PrimaryIdentityColumns(route),
            route.PrimaryKey.Columns.Select(column => column.Identifier).ToArray());
        return ValidateTableAsync(connection, transaction, route.PrimaryStorage.Name.Identifier,
        [
            Envelope(envelope.DocumentKind.Identifier, RelationalEnvelopeColumnKind.DocumentKind),
            Envelope(envelope.StorageScope.Identifier, RelationalEnvelopeColumnKind.StorageScope),
            Envelope(envelope.Identity.OriginalId.Identifier, RelationalEnvelopeColumnKind.Id),
            Envelope(envelope.Identity.ComparisonKey.Identifier, RelationalEnvelopeColumnKind.IdentityComparison),
            Envelope(envelope.Identity.LookupKey.Identifier, RelationalEnvelopeColumnKind.IdentityLookup),
            Envelope(envelope.SchemaVersion.Identifier, RelationalEnvelopeColumnKind.SchemaVersion),
            Envelope(envelope.Version.Identifier, RelationalEnvelopeColumnKind.Version),
            Envelope(envelope.CanonicalJson.Identifier, RelationalEnvelopeColumnKind.CanonicalJson),
            Envelope(RelationalPhysicalStorageColumns.CreatedUtc, RelationalEnvelopeColumnKind.Timestamp),
            Envelope(RelationalPhysicalStorageColumns.UpdatedUtc, RelationalEnvelopeColumnKind.Timestamp),
            .. identity.ProviderColumns.Select(ProviderColumn)
        ], identity.PrimaryKey, ct);

        ExpectedColumn Envelope(string name, RelationalEnvelopeColumnKind kind) =>
            new(name, dialect.EnvelopeType(kind), false, null, dialect.EnvelopeCollation(kind));
        static ExpectedColumn ProviderColumn(RelationalProviderOwnedPhysicalColumn column) =>
            new(
                column.Name,
                column.Type,
                column.IsNullable,
                column.DefaultValue,
                column.Collation,
                column.IsComputed,
                column.IsPersisted,
                column.ComputedDefinition);
    }

    private Task ValidateLinkedAsync(DbConnection connection, DbTransaction transaction, ExecutableStorageRoute route, CancellationToken ct)
    {
        var relationship = route.LinkedRelationship!;
        var identity = dialect.IdentityLayout(
            LinkedIdentityColumns(route),
            route.AuxiliaryKey!.Columns.Select(column => column.Identifier).ToArray());
        return ValidateTableAsync(connection, transaction, route.LinkedIndexStorage!.Name.Identifier,
        [
            Envelope(relationship.DocumentKind.Identifier, RelationalEnvelopeColumnKind.DocumentKind),
            Envelope(relationship.StorageScope.Identifier, RelationalEnvelopeColumnKind.StorageScope),
            Envelope(relationship.Identity.OriginalId.Identifier, RelationalEnvelopeColumnKind.Id),
            Envelope(relationship.Identity.ComparisonKey.Identifier, RelationalEnvelopeColumnKind.IdentityComparison),
            Envelope(relationship.Identity.LookupKey.Identifier, RelationalEnvelopeColumnKind.IdentityLookup),
            .. identity.ProviderColumns.Select(ProviderColumn)
        ], identity.PrimaryKey, ct);

        ExpectedColumn Envelope(string name, RelationalEnvelopeColumnKind kind) =>
            new(name, dialect.EnvelopeType(kind), false, null, dialect.EnvelopeCollation(kind));
        static ExpectedColumn ProviderColumn(RelationalProviderOwnedPhysicalColumn column) =>
            new(
                column.Name,
                column.Type,
                column.IsNullable,
                column.DefaultValue,
                column.Collation,
                column.IsComputed,
                column.IsPersisted,
                column.ComputedDefinition);
    }

    private async Task ValidateCollectionElementAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableCollectionElementStorageRoute storage,
        CancellationToken ct)
    {
        dialect.ValidateCollectionElementStorage(storage);
        var value = storage.Value.Definition with { IsNullable = false };
        await ValidateTableAsync(connection, transaction, storage.Storage.Name.Identifier,
        [
            Envelope(storage.DocumentKind.Column.Identifier, RelationalEnvelopeColumnKind.DocumentKind),
            Envelope(storage.StorageScope.Column.Identifier, RelationalEnvelopeColumnKind.StorageScope),
            Envelope(storage.IdComparisonKey.Column.Identifier, RelationalEnvelopeColumnKind.IdentityComparison),
            Envelope(storage.IdLookupKey.Column.Identifier, RelationalEnvelopeColumnKind.IdentityLookup),
            Projected(storage.Ordinal.Column.Identifier, RelationalServerPhysicalSchemaDialect.CollectionOrdinalDefinition),
            Projected(storage.Value.Column.Identifier, value)
        ], storage.OwnerOrdinalKey.Columns.Select(column => column.Column.Identifier).ToArray(), ct);
        var membership = await dialect.ReadIndexAsync(
            connection,
            transaction,
            storage.Storage.Name.Identifier,
            storage.MembershipKey.Name.Identifier,
            ct) ?? throw new InvalidOperationException(
            $"Collection membership index '{storage.MembershipKey.Name.Identifier}' is missing from '{storage.Storage.Name.Identifier}'.");
        var expectedColumns = new[] { storage.MembershipKey.Value.Column.Identifier }
            .Concat(storage.MembershipKey.OwnerColumns.Select(column => column.Column.Identifier))
            .ToArray();
        if (membership.IsUnique || membership.Filter is not null ||
            !membership.Columns.Select(column => column.Name).SequenceEqual(expectedColumns) ||
            membership.Columns.Any(column => column.Direction != PhysicalSortDirection.Ascending))
        {
            throw new InvalidOperationException(
                $"Collection membership index '{storage.MembershipKey.Name.Identifier}' does not match the compiled value-led route.");
        }

        ExpectedColumn Envelope(string name, RelationalEnvelopeColumnKind kind) =>
            new(name, dialect.EnvelopeType(kind), false, null, dialect.EnvelopeCollation(kind));
        ExpectedColumn Projected(string name, ProjectedColumnDefinition definition) =>
            new(name, dialect.ProjectedType(definition), definition.IsNullable, dialect.NormalizeDefault(definition),
                dialect.ProjectedCollation(definition));
    }

    private async Task ValidateTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        IReadOnlyList<ExpectedColumn> expected,
        IReadOnlyList<string> primaryKey,
        CancellationToken ct)
    {
        var actual = await dialect.ReadColumnsAsync(connection, transaction, table, ct);
        foreach (var desired in expected)
            EnsureColumnCompatible(table, desired, actual.GetValueOrDefault(desired.Name));
        var actualKey = actual.Values.Where(column => column.PrimaryKeyOrder > 0)
            .OrderBy(column => column.PrimaryKeyOrder).Select(column => column.Name).ToArray();
        if (!actualKey.SequenceEqual(primaryKey, StringComparer.Ordinal))
            throw new InvalidOperationException($"Physical table '{table}' has primary key ({string.Join(", ", actualKey)}) but requires ({string.Join(", ", primaryKey)}).");
    }

    private async Task ValidateProjectedColumnAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string column,
        ProjectedColumnDefinition definition,
        CancellationToken ct)
    {
        dialect.Validate(definition);
        var actual = await dialect.ReadColumnsAsync(connection, transaction, table, ct);
        EnsureColumnCompatible(table, new ExpectedColumn(
            column,
            dialect.ProjectedType(definition),
            definition.IsNullable,
            dialect.NormalizeDefault(definition),
            dialect.ProjectedCollation(definition)), actual.GetValueOrDefault(column));
    }

    /// <summary>
    /// Reduces an index filter to a form that survives the round trip through a provider's catalog.
    /// </summary>
    /// <remarks>
    /// Providers re-render the predicate they stored rather than echoing what was submitted: SQL Server
    /// keeps <c>([name] IS NOT NULL)</c>, while PostgreSQL's <c>pg_get_expr</c> quotes nothing and
    /// parenthesises every conjunct, turning one submitted pair into
    /// <c>((name IS NOT NULL) AND (description IS NOT NULL))</c>. Comparing the raw text would report
    /// drift that does not exist, so identifier quoting, grouping and whitespace are dropped first.
    /// Filters are only ever conjunctions of <c>IS NOT NULL</c>, so dropping grouping cannot make two
    /// meaningfully different predicates compare equal — a changed column set still shows up.
    /// </remarks>
    private static string? NormalizeIndexFilter(string? filter) =>
        filter is null
            ? null
            : new string(filter.Where(character =>
                character is not ('"' or '[' or ']' or '`' or '(' or ')') &&
                !char.IsWhiteSpace(character)).ToArray());

    private async Task ValidateIndexAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableStorageRoute route,
        string table,
        ExecutablePhysicalIndexRoute expected,
        CancellationToken ct)
    {
        var actual = await dialect.ReadIndexAsync(connection, transaction, table, expected.Name.Identifier, ct)
            ?? throw new InvalidOperationException($"Physical index '{expected.Name.Identifier}' is missing from '{table}'.");
        var expectedFilter = dialect.IndexFilter(
            expected,
            PhysicalIndexNullExclusion.Columns(route, expected));
        var expectedColumns = dialect.IndexKeyColumns(route, expected);
        if (!IndexShapeMatches(actual, expected, expectedColumns, expectedFilter))
            throw new InvalidOperationException($"Physical index '{expected.Name.Identifier}' has incompatible uniqueness, filter, or column count.");
        for (var index = 0; index < expectedColumns.Count; index++)
        {
            if (actual.Columns[index].Name != expectedColumns[index].Identifier ||
                actual.Columns[index].Direction != expectedColumns[index].Direction)
                throw new InvalidOperationException($"Physical index '{expected.Name.Identifier}' column {index} does not match the compiled route.");
        }
        var providerOwned = expectedColumns
            .Where(column => column.ProviderOwnedColumn is not null)
            .Select(column => column.ProviderOwnedColumn!)
            .ToArray();
        if (providerOwned.Length != 0)
        {
            var tableColumns = await dialect.ReadColumnsAsync(connection, transaction, table, ct);
            foreach (var column in providerOwned)
            {
                EnsureColumnCompatible(table, new ExpectedColumn(
                    column.Name,
                    column.Type,
                    column.IsNullable,
                    column.DefaultValue,
                    column.Collation,
                    column.IsComputed,
                    column.IsPersisted,
                    column.ComputedDefinition), tableColumns.GetValueOrDefault(column.Name));
            }
        }
    }

    private void EnsureColumnCompatible(string table, ExpectedColumn expected, RelationalPhysicalColumnMetadata? actual)
    {
        if (actual is null)
            throw new InvalidOperationException($"Physical column '{table}.{expected.Name}' is missing.");
        if (!string.Equals(actual.Type, expected.Type, StringComparison.OrdinalIgnoreCase) ||
            actual.IsNullable != expected.IsNullable ||
            !string.Equals(actual.DefaultValue, expected.DefaultValue, StringComparison.Ordinal) ||
            !string.Equals(
                dialect.NormalizeCollationIdentity(actual.Collation),
                dialect.NormalizeCollationIdentity(expected.Collation),
                StringComparison.OrdinalIgnoreCase) ||
            actual.IsComputed != expected.IsComputed ||
            actual.IsPersisted != expected.IsPersisted ||
            !string.Equals(
                dialect.NormalizeComputedDefinition(actual.ComputedDefinition),
                dialect.NormalizeComputedDefinition(expected.ComputedDefinition),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Physical column '{table}.{expected.Name}' is incompatible with the compiled route " +
                $"(type '{actual.Type}', nullable '{actual.IsNullable}', default '{actual.DefaultValue ?? "<none>"}', " +
                $"collation '{actual.Collation ?? "<default>"}', computed '{actual.IsComputed}', persisted '{actual.IsPersisted}', " +
                $"expression '{actual.ComputedDefinition ?? "<none>"}').");
        }
    }

    private async Task<(string Fingerprint, DateTimeOffset AppliedAt)?> ReadOperationAsync(
        DbConnection connection,
        DbTransaction? transaction,
        PhysicalSchemaTargetIdentity target,
        string identity,
        CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT operation_fingerprint, applied_utc
            FROM groundwork_physical_schema_operations
            WHERE manifest_id = @manifestId AND provider_name = @providerName AND operation_id = @identity;
            """);
        Add(command, "manifestId", target.ManifestIdentity.Value);
        Add(command, "providerName", target.ProviderName);
        Add(command, "identity", identity);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        var applied = reader.GetValue(1) switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            var value => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture)
        };
        return (reader.GetString(0), applied);
    }

    private static async Task<bool> IsOperationPublishedAsync(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaTargetIdentity target,
        PhysicalSchemaOperation operation,
        CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT applied_state_json
            FROM groundwork_physical_schema_state
            WHERE manifest_id = @manifestId AND provider_name = @providerName;
            """);
        Add(command, "manifestId", target.ManifestIdentity.Value);
        Add(command, "providerName", target.ProviderName);
        var json = await command.ExecuteScalarAsync(ct) as string;
        if (json is null)
            return false;
        var state = PhysicalSchemaAppliedStateSerializer.Deserialize(json);
        return state.Snapshot.SemanticOperations.Any(applied =>
            applied.Identity == operation.Identity &&
            applied.Fingerprint == operation.Fingerprint);
    }

    private static async Task<string?> ReadTargetFingerprintAsync(
        DbConnection connection,
        DbTransaction transaction,
        string manifestId,
        string providerName,
        CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT target_fingerprint FROM groundwork_physical_schema_state WHERE manifest_id = @manifestId AND provider_name = @providerName;");
        Add(command, "manifestId", manifestId);
        Add(command, "providerName", providerName);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static ExecutableProjectedColumnRoute[] SelectBackfillColumns(
        ExecutableStorageRoute route,
        BackfillCanonicalJsonOperation operation,
        ExecutableStorageObjectRole target) =>
        route.ProjectedColumns.Where(column =>
                column.Target == target &&
                (operation.SubjectKind != CanonicalJsonBackfillSubjectKind.ProjectedColumn ||
                 column.Definition.LogicalName == operation.SubjectIdentity))
            .ToArray();

    private void ValidateRoute(ExecutableStorageRoute route)
    {
        RelationalPhysicalStorageColumns.Validate(route);
        dialect.ValidateRoute(route);
    }

    private static RelationalPhysicalIdentityColumn[] PrimaryIdentityColumns(ExecutableStorageRoute route) =>
    [
        new(route.Envelope.DocumentKind.Identifier, RelationalEnvelopeColumnKind.DocumentKind),
        new(route.Envelope.StorageScope.Identifier, RelationalEnvelopeColumnKind.StorageScope),
        new(route.Envelope.Identity.OriginalId.Identifier, RelationalEnvelopeColumnKind.Id),
        new(route.Envelope.Identity.ComparisonKey.Identifier, RelationalEnvelopeColumnKind.IdentityComparison),
        new(route.Envelope.Identity.LookupKey.Identifier, RelationalEnvelopeColumnKind.IdentityLookup)
    ];

    private static RelationalPhysicalIdentityColumn[] LinkedIdentityColumns(ExecutableStorageRoute route) =>
    [
        new(route.LinkedRelationship!.DocumentKind.Identifier, RelationalEnvelopeColumnKind.DocumentKind),
        new(route.LinkedRelationship.StorageScope.Identifier, RelationalEnvelopeColumnKind.StorageScope),
        new(route.LinkedRelationship.Identity.OriginalId.Identifier, RelationalEnvelopeColumnKind.Id),
        new(route.LinkedRelationship.Identity.ComparisonKey.Identifier, RelationalEnvelopeColumnKind.IdentityComparison),
        new(route.LinkedRelationship.Identity.LookupKey.Identifier, RelationalEnvelopeColumnKind.IdentityLookup)
    ];

    protected static long ReadLockSessionId(IPhysicalSchemaApplicationLock applicationLock) =>
        applicationLock is ApplicationLock relationalLock
            ? relationalLock.ServerSessionId
            : throw new ArgumentException("The lock was not created by a relational server physical-schema executor.", nameof(applicationLock));

    private static ApplicationLock RequireApplicationLock(
        IPhysicalSchemaApplicationLock applicationLock,
        PhysicalSchemaTargetIdentity expectedTarget)
    {
        ArgumentNullException.ThrowIfNull(applicationLock);
        if (applicationLock is not ApplicationLock relationalLock ||
            relationalLock.Target != expectedTarget)
        {
            throw new InvalidOperationException(
                $"Relational physical schema execution requires its active application lock for target '{expectedTarget}'.");
        }
        return relationalLock;
    }

    private sealed class ApplicationLock : IPhysicalSchemaApplicationLock
    {
        private static readonly TimeSpan OwnershipVerificationTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(100);
        private const int MaxConsecutiveHeartbeatVerificationFailures = 3;
        private readonly DbConnection connection;
        private readonly string resource;
        private readonly CancellationTokenSource heartbeatStop = new();
        private readonly RelationalServerPhysicalSchemaDialect dialect;
        private readonly SemaphoreSlim sessionGate = new(1, 1);
        private readonly CancellationTokenSource ownershipLost = new();
        private readonly Task heartbeat;
        private Exception? heartbeatFailure;
        private int disposed;

        public ApplicationLock(
            PhysicalSchemaTargetIdentity target,
            DbConnection connection,
            string resource,
            string owner,
            long fence,
            long serverSessionId,
            RelationalServerPhysicalSchemaDialect dialect)
        {
            Target = target;
            this.connection = connection;
            this.resource = resource;
            this.dialect = dialect;
            Owner = owner;
            Fence = fence;
            ServerSessionId = serverSessionId;
            // Captured once so the token stays readable after disposal disposes the source.
            OwnershipLost = ownershipLost.Token;
            connection.StateChange += OnConnectionStateChanged;
            heartbeat = HeartbeatAsync();
        }

        public PhysicalSchemaTargetIdentity Target { get; }
        public string Owner { get; }
        public long Fence { get; }
        public long ServerSessionId { get; }
        public CancellationToken OwnershipLost { get; }

        public async Task<T> ExecuteAsync<T>(
            Func<DbConnection, CancellationToken, Task<T>> action,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(action);
            await sessionGate.WaitAsync(cancellationToken);
            try
            {
                if (!SessionUsable)
                {
                    MarkOwnershipLost();
                    throw OwnershipLostException();
                }

                try
                {
                    return await action(connection, cancellationToken);
                }
                catch (Exception exception) when (ClassifiesAsPotentialLockLoss(exception, cancellationToken))
                {
                    var verificationFailure = default(Exception);
                    var isOwned = false;
                    if (SessionUsable)
                    {
                        using var verificationTimeout = new CancellationTokenSource(OwnershipVerificationTimeout);
                        try
                        {
                            isOwned = await dialect.VerifyApplicationLockAsync(
                                connection,
                                resource,
                                verificationTimeout.Token);
                        }
                        catch (Exception ownershipException) when (ownershipException is not OutOfMemoryException)
                        {
                            verificationFailure = ownershipException;
                        }
                    }

                    if (isOwned)
                        throw;
                    MarkOwnershipLost();
                    throw OwnershipLostException(exception, verificationFailure);
                }
            }
            finally
            {
                sessionGate.Release();
            }
        }

        /// <summary>
        /// Best-effort teardown. Any step that cannot prove a clean release signals
        /// <see cref="OwnershipLost"/>. The release failure, the session-close failure and the
        /// heartbeat's last probe failure are all retained, so a report can say what went wrong
        /// rather than only that something did.
        /// <para>
        /// Disposal throws an <see cref="AggregateException"/> carrying those failures only when the
        /// lock was never released <em>and</em> closing its session also failed. Failing to release
        /// does not leak on its own: the lock is session-scoped, so closing the connection ends the
        /// session and the server drops the lock with it. Only when that fallback also fails can the
        /// lock outlive this object and block the next acquirer, and a killed session -- whose
        /// release fails precisely because the session is already gone -- must dispose quietly.
        /// <em>Never released</em> covers the release throwing and the release being skipped because
        /// the connection was no longer open; neither proves the lock was let go.
        /// </para>
        /// <para>
        /// Throwing from disposal masks an exception already propagating out of an
        /// <c>await using</c> block. That is accepted deliberately: the alternative used by
        /// <c>AcquireApplicationLockAsync</c>, attaching via <c>RelationalCleanupFailures</c>, needs
        /// the in-flight exception, which disposal cannot see, and a silently leaked lock blocks the
        /// next acquirer with no diagnostic at all. Requiring both failures is what bounds the
        /// masking to genuine leaks.
        /// </para>
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            connection.StateChange -= OnConnectionStateChanged;
            await heartbeatStop.CancelAsync();
            var cleanupFailures = new List<Exception>();
            var lockReleased = false;
            var sessionCloseFailed = false;
            try
            {
                try
                {
                    await heartbeat;
                }
                catch (Exception failure)
                {
                    // Defensive: HeartbeatAsync retains its own failures rather than faulting.
                    SignalOwnershipLost();
                    heartbeatFailure ??= failure;
                }

                await sessionGate.WaitAsync();
                try
                {
                    if (connection.State == ConnectionState.Open)
                    {
                        await dialect.ReleaseApplicationLockAsync(connection, resource, CancellationToken.None);
                        lockReleased = true;
                    }
                    else
                    {
                        // Skipping the release is not the same as having released: it leaves the
                        // lock's fate resting entirely on the session close below.
                        SignalOwnershipLost();
                    }
                }
                catch (Exception failure)
                {
                    SignalOwnershipLost();
                    cleanupFailures.Add(failure);
                }
                finally
                {
                    sessionGate.Release();
                }
            }
            finally
            {
                try
                {
                    await connection.DisposeAsync();
                }
                catch (Exception failure)
                {
                    SignalOwnershipLost();
                    cleanupFailures.Add(failure);
                    sessionCloseFailed = true;
                }
                heartbeatStop.Dispose();
                sessionGate.Dispose();
                ownershipLost.Dispose();
            }
            if (!lockReleased && sessionCloseFailed)
            {
                // The heartbeat's last probe failure explains why the session went bad, so it rides
                // along as context. It never triggers the throw on its own.
                if (heartbeatFailure is { } heartbeatContext)
                    cleanupFailures.Add(heartbeatContext);
                throw new AggregateException(
                    $"Physical-schema application lock for target '{Target}' could not be released and its session could not be closed. " +
                    "The lock may still be held, blocking the next acquirer until the server ends that session.",
                    cleanupFailures);
            }
        }

        private void OnConnectionStateChanged(object sender, StateChangeEventArgs args)
        {
            if (args.CurrentState != ConnectionState.Open && Volatile.Read(ref disposed) == 0)
                MarkOwnershipLost();
        }

        private async Task HeartbeatAsync()
        {
            var consecutiveVerificationFailures = 0;
            try
            {
                while (!heartbeatStop.IsCancellationRequested)
                {
                    await Task.Delay(HeartbeatInterval, heartbeatStop.Token);
                    await sessionGate.WaitAsync(heartbeatStop.Token);
                    try
                    {
                        bool isOwned;
                        try
                        {
                            isOwned = await dialect.VerifyApplicationLockAsync(connection, resource, heartbeatStop.Token);
                        }
                        catch (Exception exception) when (
                            exception is DbException or InvalidOperationException or OperationCanceledException &&
                            !heartbeatStop.IsCancellationRequested)
                        {
                            // A failed probe is inconclusive: the server-side lock may still be held. Only a
                            // bounded run of consecutive failures forfeits ownership; a definitive "not owned"
                            // verification below loses it immediately.
                            // Retained so a forfeited lease can name the probe failure behind it. The
                            // outer catch retains its own, and wins: it ends the heartbeat outright.
                            heartbeatFailure = exception;
                            if (++consecutiveVerificationFailures >= MaxConsecutiveHeartbeatVerificationFailures)
                            {
                                SignalOwnershipLost();
                                return;
                            }
                            continue;
                        }

                        if (!isOwned)
                        {
                            // A definitive not-owned answer is not an error, so nothing explains this
                            // forfeiture. Clearing first keeps an earlier, already-recovered probe
                            // failure from being reported as its cause.
                            heartbeatFailure = null;
                            SignalOwnershipLost();
                            return;
                        }
                        consecutiveVerificationFailures = 0;
                        heartbeatFailure = null;
                    }
                    finally
                    {
                        sessionGate.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested)
            {
            }
            catch (Exception failure)
            {
                // Overwrites rather than defers: this exception is the one that ended the heartbeat,
                // so it explains the forfeiture better than any probe failure already recovered from.
                heartbeatFailure = failure;
                SignalOwnershipLost();
            }
        }

        private bool SessionUsable =>
            Volatile.Read(ref disposed) == 0 && !ownershipLost.IsCancellationRequested &&
            connection.State == ConnectionState.Open;

        // A session killed mid-operation surfaces as whatever the driver's protocol state permits
        // (SqlClient can throw NullReferenceException), so lock loss is classified by verifying
        // ownership rather than by exception type. Exempt are out-of-memory, which must never be
        // swallowed, and cancellation requested through the caller's token. That token may be
        // linked to OwnershipLost (PhysicalSchemaApplication links them), and lease loss observed
        // as cancellation surfaces as OperationCanceledException by contract — the same way the
        // application layer reports it when it checks the token between operations.
        private static bool ClassifiesAsPotentialLockLoss(Exception exception, CancellationToken cancellationToken) =>
            exception is not OutOfMemoryException &&
            (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested);

        private void MarkOwnershipLost()
        {
            if (Volatile.Read(ref disposed) == 0)
                SignalOwnershipLost();
        }

        /// <summary>
        /// Cancels ownership without the disposed guard, for callers that provably run before
        /// <see cref="ownershipLost"/> is disposed: the winning disposer's own body and the
        /// heartbeat it awaits first. <see cref="MarkOwnershipLost"/> keeps the guard for everyone
        /// else, but that check cannot be atomic with the cancel, so a caller racing disposal can
        /// still reach a disposed source. That is absorbed here rather than thrown, because
        /// <see cref="OnConnectionStateChanged"/> runs on a connection event thread where nothing
        /// would catch it.
        /// </summary>
        private void SignalOwnershipLost()
        {
            if (ownershipLost.IsCancellationRequested)
                return;
            try
            {
                ownershipLost.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Disposal won the race and already tore this lock down; ownership is moot.
            }
            catch (AggregateException)
            {
                // A subscriber's cancellation callback threw. OwnershipLost is public, so those
                // callbacks are arbitrary consumer code, and the token is already cancelled by the
                // time they run -- the signal landed. Letting the fault escape would abort the
                // caller's teardown midway, skipping the remaining Dispose calls, or surface on a
                // connection event thread with nothing to catch it.
            }
        }

        private InvalidOperationException OwnershipLostException(
            Exception? executionFailure = null,
            Exception? verificationFailure = null)
        {
            Exception? inner = executionFailure;
            if (verificationFailure is not null)
                inner = new AggregateException(executionFailure!, verificationFailure);
            var state = verificationFailure is null ? "was lost" : "was lost or could not be verified";
            return new InvalidOperationException(
                $"The relational physical-schema lock session for target '{Target}' {state} during schema execution.",
                inner);
        }
    }

    private sealed record CanonicalDocument(string Scope, string Id, string CanonicalJson);
    private sealed record ExpectedColumn(
        string Name,
        string Type,
        bool IsNullable,
        string? DefaultValue,
        string? Collation,
        bool IsComputed = false,
        bool IsPersisted = false,
        string? ComputedDefinition = null);
}


public enum RelationalEnvelopeColumnKind
{
    DocumentKind,
    StorageScope,
    Id,
    IdentityComparison,
    IdentityLookup,
    SchemaVersion,
    Version,
    CanonicalJson,
    Timestamp
}

public sealed record RelationalPhysicalColumnMetadata(
    string Name,
    string Type,
    bool IsNullable,
    string? DefaultValue,
    string? Collation,
    int PrimaryKeyOrder,
    bool IsComputed = false,
    bool IsPersisted = false,
    string? ComputedDefinition = null);

public sealed record RelationalPhysicalIndexColumnMetadata(string Name, PhysicalSortDirection Direction);

/// <summary>
/// One key column of a physical index as the provider emits it. Route-declared columns carry no
/// backing definition; a provider-owned entry carries the column the provider must add so the key
/// can exist (e.g. a persisted hash restoring byte-exact uniqueness).
/// </summary>
public sealed record RelationalPhysicalIndexKeyColumn(
    string Identifier,
    PhysicalSortDirection Direction,
    RelationalProviderOwnedPhysicalColumn? ProviderOwnedColumn = null);

public sealed record RelationalPhysicalIndexMetadata(
    bool IsUnique,
    IReadOnlyList<RelationalPhysicalIndexColumnMetadata> Columns,
    string? Filter);

/// <summary>A retained provider column that participates in a document identity.</summary>
public sealed record RelationalPhysicalIdentityColumn(string Name, RelationalEnvelopeColumnKind Kind);

/// <summary>A provider-owned column added behind the portable physical-storage interface.</summary>
public sealed record RelationalProviderOwnedPhysicalColumn(
    string Name,
    string Definition,
    string Type,
    bool IsNullable,
    string? DefaultValue = null,
    string? Collation = null,
    bool IsComputed = false,
    bool IsPersisted = false,
    string? ComputedDefinition = null);

/// <summary>Maps the retained logical identity to the provider's physical key representation.</summary>
public sealed record RelationalPhysicalIdentityLayout(
    IReadOnlyList<RelationalProviderOwnedPhysicalColumn> ProviderColumns,
    IReadOnlyList<string> PrimaryKey);

/// <summary>Provider-owned SQL and metadata behavior behind the shared physical-schema executor.</summary>
public abstract class RelationalServerPhysicalSchemaDialect
{
    internal static readonly ProjectedColumnDefinition CollectionOrdinalDefinition = new(
        "ordinal",
        "ordinal",
        PortablePhysicalType.Int32,
        IsNullable: false);

    protected sealed record InfrastructureColumn(
        string Name,
        string Type,
        bool IsNullable,
        string? Collation,
        int PrimaryKeyOrder = 0,
        bool IsComputed = false,
        bool IsPersisted = false,
        string? ComputedDefinition = null);

    public abstract string ProviderDisplayName { get; }
    public abstract string QuoteIdentifier(string identifier);
    public abstract string EnvelopeType(RelationalEnvelopeColumnKind kind);
    public abstract string? EnvelopeCollation(RelationalEnvelopeColumnKind kind);
    public abstract string ProjectedType(ProjectedColumnDefinition definition);
    public abstract string? Collation(string? portableCollation);
    public virtual string? NormalizeCollationIdentity(string? collation) => collation;
    public virtual string? ProjectedCollation(ProjectedColumnDefinition definition) =>
        definition.Type is PortablePhysicalType.String or PortablePhysicalType.Json
            ? Collation(definition.Collation)
            : null;
    public virtual string? NormalizeDefault(ProjectedColumnDefinition definition) =>
        definition.DefaultValue is null ? null : DefaultSql(definition);
    /// <summary>Renders a declared default value as provider DDL. Only called when one is declared.</summary>
    protected abstract string? DefaultSql(ProjectedColumnDefinition definition);
    /// <summary>Renders a provider collation as the token DDL emits after <c>COLLATE</c>.</summary>
    protected abstract string CollationToken(string value);
    protected string CollationSql(ProjectedColumnDefinition definition) =>
        ProjectedCollation(definition) is { } value ? $" COLLATE {CollationToken(value)}" : string.Empty;
    public virtual string? NormalizeComputedDefinition(string? definition) => definition?.Trim();
    public virtual bool IsProviderOwnedColumnCompatible(
        RelationalProviderOwnedPhysicalColumn expected,
        RelationalPhysicalColumnMetadata actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        return string.Equals(actual.Type, expected.Type, StringComparison.OrdinalIgnoreCase) &&
               actual.IsNullable == expected.IsNullable &&
               actual.IsComputed == expected.IsComputed &&
               actual.IsPersisted == expected.IsPersisted &&
               string.Equals(
                   NormalizeComputedDefinition(actual.ComputedDefinition),
                   NormalizeComputedDefinition(expected.ComputedDefinition),
                   StringComparison.Ordinal);
    }
    public virtual void ValidateRoute(ExecutableStorageRoute route) => ArgumentNullException.ThrowIfNull(route);
    public virtual string ExactIdentityPredicate(IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return string.Join(" AND ", parts.Select(part =>
        {
            var column = part.Alias is null ? QuoteIdentifier(part.ColumnIdentifier) : $"{part.Alias}.{QuoteIdentifier(part.ColumnIdentifier)}";
            return $"{column} = {part.ValueExpression}";
        }));
    }
    public virtual string? HashOnlyIdentityPredicate(IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts) => null;
    public virtual bool IsUniqueConstraintException(DbException exception) => false;
    public virtual string EnvelopeColumn(string name, RelationalEnvelopeColumnKind kind) =>
        $"{QuoteIdentifier(name)} {EnvelopeType(kind)}" +
        (EnvelopeCollation(kind) is { } collation ? $" COLLATE {CollationToken(collation)}" : string.Empty) +
        " NOT NULL";
    public virtual RelationalPhysicalIdentityLayout IdentityLayout(
        IReadOnlyList<RelationalPhysicalIdentityColumn> identityColumns,
        IReadOnlyList<string> logicalPrimaryKey)
    {
        ArgumentNullException.ThrowIfNull(identityColumns);
        ArgumentNullException.ThrowIfNull(logicalPrimaryKey);
        var identityNames = identityColumns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        if (logicalPrimaryKey.Any(column => !identityNames.Contains(column)))
            throw new ArgumentException("Every logical primary-key column must be a retained identity column.", nameof(logicalPrimaryKey));
        return new RelationalPhysicalIdentityLayout([], Array.AsReadOnly(logicalPrimaryKey.ToArray()));
    }
    public abstract string CreateTableSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> primaryKey);
    public virtual void ValidateCollectionElementStorage(ExecutableCollectionElementStorageRoute storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        Validate(storage.Value.Definition with { IsNullable = false });
    }
    public virtual string CreateCollectionElementTableSql(ExecutableCollectionElementStorageRoute storage)
    {
        ValidateCollectionElementStorage(storage);
        var table = CreateTableSql(storage.Storage.Name.Identifier,
        [
            EnvelopeColumn(storage.DocumentKind.Column.Identifier, RelationalEnvelopeColumnKind.DocumentKind),
            EnvelopeColumn(storage.StorageScope.Column.Identifier, RelationalEnvelopeColumnKind.StorageScope),
            EnvelopeColumn(storage.IdComparisonKey.Column.Identifier, RelationalEnvelopeColumnKind.IdentityComparison),
            EnvelopeColumn(storage.IdLookupKey.Column.Identifier, RelationalEnvelopeColumnKind.IdentityLookup),
            ProjectedColumnSql(storage.Ordinal.Column.Identifier, CollectionOrdinalDefinition),
            ProjectedColumnSql(storage.Value.Column.Identifier, storage.Value.Definition with { IsNullable = false })
        ], storage.OwnerOrdinalKey.Columns.Select(column => column.Column.Identifier).ToArray());
        var membershipColumns = new[] { storage.MembershipKey.Value.Column.Identifier }
            .Concat(storage.MembershipKey.OwnerColumns.Select(column => column.Column.Identifier));
        return $"{table}; CREATE INDEX {QuoteIdentifier(storage.MembershipKey.Name.Identifier)} ON " +
               $"{QuoteIdentifier(storage.Storage.Name.Identifier)} ({string.Join(", ", membershipColumns.Select(QuoteIdentifier))})";
    }
    public virtual string ProjectedColumnSql(string column, ProjectedColumnDefinition definition) =>
        $"{QuoteIdentifier(column)} {ProjectedType(definition)}{CollationSql(definition)} {(definition.IsNullable ? "NULL" : "NOT NULL")}" +
        (DefaultSql(definition) is { } value ? $" DEFAULT {value}" : string.Empty);
    public abstract string AddColumnSql(string table, string column, ProjectedColumnDefinition definition);
    public abstract string FinalizeColumnSql(string table, string column, ProjectedColumnDefinition definition);
    /// <summary>
    /// The index filter that realises <see cref="MissingValueBehavior.Excluded"/>, or <see langword="null"/>
    /// when <paramref name="excludedColumns"/> is empty and the index therefore keeps every row.
    /// </summary>
    public virtual string? IndexFilter(ExecutablePhysicalIndexRoute index, IReadOnlyList<string> excludedColumns) =>
        excludedColumns.Count > 0
            ? $"({string.Join(" AND ", excludedColumns.Select(column => $"{QuoteIdentifier(column)} IS NOT NULL"))})"
            : null;
    /// <summary>
    /// The key columns this provider emits for <paramref name="index"/>. The default is the compiled
    /// route's columns verbatim; a provider whose native string equality cannot enforce the portable
    /// exact-match contract may append provider-owned key columns carrying their backing definitions.
    /// Creation and validation both consume this, so the emitted and expected shapes cannot diverge.
    /// </summary>
    public virtual IReadOnlyList<RelationalPhysicalIndexKeyColumn> IndexKeyColumns(
        ExecutableStorageRoute route,
        ExecutablePhysicalIndexRoute index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return index.Columns
            .Select(column => new RelationalPhysicalIndexKeyColumn(column.Column.Identifier, column.Direction))
            .ToArray();
    }
    public abstract string CreateIndexSql(
        string table,
        ExecutablePhysicalIndexRoute index,
        IReadOnlyList<RelationalPhysicalIndexKeyColumn> keyColumns,
        IReadOnlyList<string> excludedColumns);
    /// <summary>
    /// Drops an index so a widened definition can replace it. Only ever issued for an index this executor
    /// has just proved it emitted itself.
    /// </summary>
    public abstract string DropIndexSql(string table, string index);
    public abstract string UpsertLinkedSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns, IReadOnlyList<string> updateColumns);
    public abstract string SelectCanonicalBatchSql(ExecutableStorageRoute route, int batchSize, bool hasCursor);
    public abstract object? ConvertStorageValue(object? value, ProjectedColumnDefinition definition);
    public virtual object ConvertDocumentIdentityOriginal(string value) => value;
    public virtual object ConvertDocumentIdentityComparison(string value) => value;
    public virtual object ConvertDocumentIdentityLookup(string value) => value;
    public virtual string ReadDocumentIdentityComparison(DbDataReader reader, int ordinal) =>
        reader.GetString(ordinal);
    public virtual bool PhysicalIdentityValueEquals(object retained, object expected) =>
        Equals(retained, expected);
    public abstract void Validate(ProjectedColumnDefinition definition);
    public abstract Task AcquireApplicationLockAsync(DbConnection connection, string resource, CancellationToken cancellationToken);
    public abstract Task ReleaseApplicationLockAsync(DbConnection connection, string resource, CancellationToken cancellationToken);
    public abstract Task<bool> VerifyApplicationLockAsync(DbConnection connection, string resource, CancellationToken cancellationToken);
    public abstract Task<long> ReadServerSessionIdAsync(DbConnection connection, CancellationToken cancellationToken);
    public abstract Task<long> AcquireFenceAsync(
        DbConnection connection,
        PhysicalSchemaTargetIdentity target,
        string owner,
        CancellationToken cancellationToken);
    public abstract Task AssertFenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaTargetIdentity target,
        string owner,
        long fence,
        CancellationToken cancellationToken);
    /// <summary>
    /// Ensures and validates the shared physical-schema infrastructure tables inside one transaction.
    /// Providers declare the tables through <see cref="InfrastructureTables"/> and may create
    /// prerequisites (such as helper functions) through
    /// <see cref="EnsureInfrastructurePrerequisitesAsync"/>.
    /// </summary>
    public virtual async Task EnsureInfrastructureAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureInfrastructurePrerequisitesAsync(connection, transaction, cancellationToken);
        var tables = InfrastructureTables;
        foreach (var table in tables)
            await EnsureInfrastructureTableAsync(connection, transaction, table.Name, table.CreateSql, cancellationToken);
        foreach (var table in tables)
            await ValidateInfrastructureTableAsync(connection, transaction, table.Name, table.Columns, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    public abstract Task<bool> TableExistsAsync(DbConnection connection, DbTransaction transaction, string table, CancellationToken cancellationToken);
    public abstract Task<IReadOnlyDictionary<string, RelationalPhysicalColumnMetadata>> ReadColumnsAsync(DbConnection connection, DbTransaction transaction, string table, CancellationToken cancellationToken);
    public virtual async Task<RelationalPhysicalIndexMetadata?> ReadIndexAsync(DbConnection connection, DbTransaction transaction, string table, string index, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, IndexMetadataSql);
        Add(command, "table", table);
        Add(command, "index", index);
        bool? unique = null;
        string? filter = null;
        var columns = new List<RelationalPhysicalIndexColumnMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            unique ??= reader.GetBoolean(0);
            filter ??= reader.IsDBNull(3) ? null : reader.GetString(3);
            columns.Add(new RelationalPhysicalIndexColumnMetadata(
                reader.GetString(1),
                reader.GetBoolean(2) ? PhysicalSortDirection.Descending : PhysicalSortDirection.Ascending));
        }
        return unique is null ? null : new RelationalPhysicalIndexMetadata(unique.Value, columns, filter);
    }

    /// <summary>
    /// The catalog query behind <see cref="ReadIndexAsync"/>. Selects, per key column in ordinal
    /// order for the <c>@table</c>/<c>@index</c> parameters: uniqueness, column name, whether the
    /// column sorts descending, and the index filter predicate (or null).
    /// </summary>
    protected abstract string IndexMetadataSql { get; }

    /// <summary>One shared infrastructure table: its name, provider DDL, and exact expected columns.</summary>
    protected sealed record InfrastructureTable(
        string Name,
        string CreateSql,
        IReadOnlyList<InfrastructureColumn> Columns);

    /// <summary>
    /// The shared infrastructure tables in creation order. <see cref="EnsureInfrastructureAsync"/>
    /// ensures every table before validating any of them.
    /// </summary>
    protected abstract IReadOnlyList<InfrastructureTable> InfrastructureTables { get; }

    /// <summary>Creates provider prerequisites required before any infrastructure table exists.</summary>
    protected virtual Task EnsureInfrastructurePrerequisitesAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Creates <paramref name="table"/> when absent; rejects a non-table object of that name.</summary>
    protected abstract Task EnsureInfrastructureTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string createSql,
        CancellationToken cancellationToken);

    protected internal static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    protected internal static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = $"@{name}";
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    protected async Task ValidateInfrastructureTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        IReadOnlyList<InfrastructureColumn> expectedColumns,
        CancellationToken cancellationToken)
    {
        var actualColumns = await ReadColumnsAsync(connection, transaction, table, cancellationToken);
        if (actualColumns.Count != expectedColumns.Count ||
            expectedColumns.Any(expected => !actualColumns.ContainsKey(expected.Name)))
        {
            throw new InvalidOperationException(
                $"Physical-schema infrastructure table '{table}' does not contain the exact required column set.");
        }

        foreach (var expected in expectedColumns)
        {
            var actual = actualColumns[expected.Name];
            if (!string.Equals(actual.Type, expected.Type, StringComparison.OrdinalIgnoreCase) ||
                actual.IsNullable != expected.IsNullable ||
                actual.DefaultValue is not null ||
                !string.Equals(
                    NormalizeCollationIdentity(actual.Collation),
                    NormalizeCollationIdentity(expected.Collation),
                    StringComparison.Ordinal) ||
                actual.PrimaryKeyOrder != expected.PrimaryKeyOrder ||
                actual.IsComputed != expected.IsComputed ||
                actual.IsPersisted != expected.IsPersisted ||
                !string.Equals(
                    NormalizeComputedDefinition(actual.ComputedDefinition),
                    NormalizeComputedDefinition(expected.ComputedDefinition),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Physical-schema infrastructure column '{table}.{expected.Name}' is incompatible " +
                    $"(type '{actual.Type}', nullable '{actual.IsNullable}', default '{actual.DefaultValue ?? "<none>"}', " +
                    $"collation '{actual.Collation ?? "<none>"}', primary-key order '{actual.PrimaryKeyOrder}', " +
                    $"computed '{actual.IsComputed}', persisted '{actual.IsPersisted}', " +
                    $"expression '{actual.ComputedDefinition ?? "<none>"}').");
            }
        }
    }
}
