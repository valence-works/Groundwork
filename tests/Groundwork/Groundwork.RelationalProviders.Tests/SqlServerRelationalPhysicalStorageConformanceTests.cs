using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Provider.Relational;
using Groundwork.Relational.Documents;
using Groundwork.SqlServer;
using Groundwork.SqlServer.Documents;
using Groundwork.SqlServer.PhysicalStorage;
using Groundwork.TestInfrastructure;
using Microsoft.Data.SqlClient;
using System.Data.Common;
using System.Text;
using System.Xml.Linq;
using Testcontainers.MsSql;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class SqlServerPhysicalStorageContainer : IAsyncLifetime
{
    public MsSqlContainer Container { get; } =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU21-ubuntu-22.04").Build();

    public Task InitializeAsync() => Container.StartAsync();
    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

public sealed class SqlServerRelationalPhysicalStorageConformanceTests(
    SqlServerPhysicalStorageContainer fixture)
    : RelationalServerPhysicalIdentityConformance, IClassFixture<SqlServerPhysicalStorageContainer>
{
    private readonly MsSqlContainer container = fixture.Container;

    [Fact]
    public async Task Collection_membership_and_contains_all_execute_from_typed_element_storage()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: Guid.NewGuid().ToString("N")[..8],
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
            includeCollection: true,
            includeCollectionMembershipQuery: true);
        var connectionString = container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new SqlServerPhysicalSchemaExecutor(connectionString));
        var store = new SqlServerPhysicalDocumentStore(
            connectionString,
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Global);
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "one", "1", """{"category":"x","permissions":["a","b","b"]}"""));
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "two", "1", """{"category":"x","permissions":["a"]}"""));
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "three", "1", """{"category":"x","permissions":["b","c"]}"""));
        var queries = SqlServerPhysicalQueryRuntime.Create(
            store,
            model.Manifest,
            model.Target.Routes.Single(),
            model.Target.Provider);

        var contains = await queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "list-by-permissions",
            [DocumentQueryClause.Of(DocumentQueryComparison.CollectionContains("permissions", "b"))]));
        var containsAll = await queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "list-by-permissions",
            [DocumentQueryClause.Of(DocumentQueryComparison.CollectionContainsAll(
                "permissions",
                ["b", "a", "b"]))]));

        Assert.Equal(2, contains.TotalCount);
        Assert.Equal(["one", "three"], contains.Documents.Select(document => document.Id).Order());
        Assert.Equal("one", Assert.Single(containsAll.Documents).Id);

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "one", "1", """{"category":"x","permissions":["c"]}""", 1))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Deleted, (await store.DeleteAsync(new DeleteDocumentRequest(
            "configurationDocument", "three", 1))).Status);

        Assert.Equal("one", Assert.Single((await queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "list-by-permissions",
            [DocumentQueryClause.Of(DocumentQueryComparison.CollectionContains("permissions", "c"))]))).Documents).Id);
        Assert.Empty((await queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "list-by-permissions",
            [DocumentQueryClause.Of(DocumentQueryComparison.CollectionContains("permissions", "b"))]))).Documents);
    }

    [Fact]
    public async Task Collection_contains_all_deduplicates_after_typed_conversion()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: Guid.NewGuid().ToString("N")[..8],
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
            includeCollection: true,
            includeCollectionMembershipQuery: true,
            collectionType: PortablePhysicalType.Int32,
            collectionLogicalValueKind: IndexValueKind.Number,
            collectionLength: null,
            collectionCollation: null);
        var connectionString = container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlServerPhysicalSchemaExecutor(connectionString));
        var store = new SqlServerPhysicalDocumentStore(
            connectionString, model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "one", "1", """{"category":"x","permissions":[1,2]}"""));
        var runtime = SqlServerPhysicalQueryRuntime.Create(
            store, model.Manifest, model.Target.Routes.Single(), model.Target.Provider);

        var query = new DocumentQuery(
            "configurationDocument",
            "list-by-permissions",
            [DocumentQueryClause.Of(DocumentQueryComparison.CollectionContainsAll(
                "permissions",
                ["1", "01"]))]);
        var rendered = RelationalPhysicalQueryRuntime.BuildQueryCommand(
            store,
            model.Manifest,
            model.Target.Routes.Single(),
            model.Target.Provider,
            "sqlserver",
            query);
        var membershipParameter = Assert.Single(rendered.Parameters.Where(parameter =>
            parameter.Name.StartsWith("v", StringComparison.Ordinal)));
        Assert.Equal("v0", membershipParameter.Name);
        Assert.Equal(1, Assert.IsType<int>(membershipParameter.Value));
        Assert.Equal(1, rendered.CommandText.Split("@v0", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, rendered.CommandText.Split("EXISTS (SELECT 1 FROM", StringSplitOptions.None).Length - 1);

        var result = await runtime.QueryAsync(query);

        Assert.Equal("one", Assert.Single(result.Documents).Id);
    }

    [Fact]
    public async Task Additive_collection_storage_backfills_preexisting_documents()
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
            includeCollection: true,
            includeCollectionMembershipQuery: true);
        var connectionString = container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, new SqlServerPhysicalSchemaExecutor(connectionString));
        await new SqlServerPhysicalDocumentStore(
                connectionString, initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global)
            .SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "preexisting", "1", """{"category":"x","permissions":["a","b"]}"""));

        var applied = await PhysicalSchemaApplication.ApplyAsync(
            additive.Target, new SqlServerPhysicalSchemaExecutor(connectionString));
        var evolved = new SqlServerPhysicalDocumentStore(
            connectionString, additive.Manifest, additive.Target.Routes, DocumentStoreAccess.Global);
        var result = await SqlServerPhysicalQueryRuntime.Create(
                evolved, additive.Manifest, additive.Target.Routes.Single(), additive.Target.Provider)
            .QueryAsync(new DocumentQuery(
                "configurationDocument",
                "list-by-permissions",
                [DocumentQueryClause.Of(DocumentQueryComparison.CollectionContains("permissions", "b"))]));

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, applied.Outcome);
        Assert.Equal("preexisting", Assert.Single(result.Documents).Id);
    }

    [Fact]
    public async Task Collection_schema_transition_fences_old_route_writers()
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
            mutationOptions: new(IncludeCategoryTransition: true));
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
            mutationOptions: new(IncludeCategoryTransition: true),
            includeCollection: true,
            includeCollectionMembershipQuery: true);
        var connectionString = container.GetConnectionString();

        await RelationalCollectionSchemaTransitionAssertions.SuccessfulTransitionFencesOldWritersAsync(
            initial,
            additive,
            (manifest, routes) => new SqlServerPhysicalDocumentStore(
                connectionString,
                manifest,
                routes,
                DocumentStoreAccess.Global),
            (store, manifest, route) => SqlServerPhysicalMutationRuntime.Create(
                Assert.IsType<SqlServerPhysicalDocumentStore>(store),
                manifest,
                route,
                SqlServerGroundworkCapabilities.Provider),
            hook => new SqlServerPhysicalSchemaExecutor(
                connectionString,
                new SqlServerPhysicalIdentityHash(),
                hook,
                null));
    }

    [Fact]
    public async Task Failed_collection_schema_transition_preserves_old_writer_admission()
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
            includeCollection: true);
        var connectionString = container.GetConnectionString();

        await RelationalCollectionSchemaTransitionAssertions.FailedTransitionPreservesOldWriterAdmissionAsync(
            initial,
            additive,
            (manifest, routes) => new SqlServerPhysicalDocumentStore(
                connectionString,
                manifest,
                routes,
                DocumentStoreAccess.Global),
            hook => new SqlServerPhysicalSchemaExecutor(
                connectionString,
                new SqlServerPhysicalIdentityHash(),
                hook,
                null));
    }

    [Fact]
    public async Task Collection_save_failure_rolls_back_primary_and_element_rows()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: Guid.NewGuid().ToString("N")[..8],
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
            includeCollection: true);
        var connectionString = container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlServerPhysicalSchemaExecutor(connectionString));
        var store = new SqlServerPhysicalDocumentStore(
            connectionString, model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        store.WriteInterceptor = (point, operation, _, _, _) =>
            point == RelationalPhysicalWriteExecutionPoint.AfterPrimaryMutation &&
            operation == RelationalPhysicalWriteOperation.Save
                ? ValueTask.FromException(new InjectedCollectionWriteException())
                : ValueTask.CompletedTask;

        await Assert.ThrowsAsync<InjectedCollectionWriteException>(() => store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "failed", "1", """{"category":"x","permissions":["a"]}""")));
        store.WriteInterceptor = null;

        Assert.Null(await store.LoadAsync("configurationDocument", "failed"));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {Q(Assert.Single(model.Target.Routes.Single().CollectionElementStorages).Storage.Name.Identifier)};";
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Deadlock_victim_unit_of_work_save_returns_a_portable_concurrency_conflict()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            categoryUnique: true,
            instance: Guid.NewGuid().ToString("N")[..8],
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        var connectionString = container.GetConnectionString();
        var probeTable = $"groundwork_deadlock_probe_{Guid.NewGuid():N}";
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlServerPhysicalSchemaExecutor(connectionString));

        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE [{probeTable}] (id int NOT NULL PRIMARY KEY); INSERT INTO [{probeTable}] (id) VALUES (1), (2);";
            await command.ExecuteNonQueryAsync();
        }

        var store = new SqlServerPhysicalDocumentStore(
            connectionString,
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Global);
        var bothRowsLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        store.WriteInterceptor = async (point, operation, connection, transaction, cancellationToken) =>
        {
            if (point != RelationalPhysicalWriteExecutionPoint.BeforePrimaryLock ||
                operation != RelationalPhysicalWriteOperation.Save)
                return;

            var firstRow = Interlocked.Increment(ref arrivals);
            var secondRow = firstRow == 1 ? 2 : 1;
            await LockProbeRowAsync(connection, transaction, firstRow, cancellationToken);
            if (firstRow == 2)
                bothRowsLocked.TrySetResult();
            await bothRowsLocked.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            await LockProbeRowAsync(connection, transaction, secondRow, cancellationToken);
        };

        try
        {
            var results = await Task.WhenAll(
                SaveInUnitOfWorkAsync("deadlock-first"),
                SaveInUnitOfWorkAsync("deadlock-second"));

            Assert.Equal(1, results.Count(result => result.Status == DocumentStoreWriteStatus.Saved));
            Assert.Equal(1, results.Count(result => result.Status == DocumentStoreWriteStatus.ConcurrencyConflict));
            Assert.Single(new[]
            {
                await store.LoadAsync("configurationDocument", "deadlock-first"),
                await store.LoadAsync("configurationDocument", "deadlock-second")
            }.Where(document => document is not null));
        }
        finally
        {
            store.WriteInterceptor = null;
        }

        async Task LockProbeRowAsync(
            System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction,
            int rowId,
            CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT id FROM [{probeTable}] WITH (UPDLOCK, HOLDLOCK) WHERE id = {rowId};";
            await command.ExecuteScalarAsync(cancellationToken);
        }

        async Task<DocumentStoreWriteResult> SaveInUnitOfWorkAsync(string id)
        {
            await using var unitOfWork = await store.BeginAsync(DocumentCommitScope.Of("configurationDocument"));
            var result = await unitOfWork.SaveAsync(new SaveDocumentRequest(
                "configurationDocument", id, "1", "{\"category\":\"shared\"}", 0));
            if (result.Status == DocumentStoreWriteStatus.Saved)
                await unitOfWork.CommitAsync();
            return result;
        }
    }

    [Theory]
    [InlineData(CollectionElementDrift.WrongType)]
    [InlineData(CollectionElementDrift.WrongCollation)]
    [InlineData(CollectionElementDrift.WrongDefault)]
    [InlineData(CollectionElementDrift.WrongPrimaryKeyOrder)]
    public async Task Collection_element_storage_replays_cleanly_and_rejects_live_drift(CollectionElementDrift drift)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: $"collection_{Guid.NewGuid():N}"[..19],
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
            includeCollection: true);
        var storage = Assert.Single(Assert.Single(model.Target.Routes).CollectionElementStorages);
        var connectionString = container.GetConnectionString();

        var applied = await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new SqlServerPhysicalSchemaExecutor(connectionString));
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, applied.Outcome);
        Assert.Contains(applied.AppliedState!.Snapshot.Routes.Single().ResolvedNames, name =>
            name.Identifier == storage.MembershipKey.Name.Identifier);
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges,
            (await PhysicalSchemaApplication.ApplyAsync(
                model.Target,
                new SqlServerPhysicalSchemaExecutor(connectionString))).Outcome);

        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            var table = Q(storage.Storage.Name.Identifier);
            var value = Q(storage.Value.Column.Identifier);
            var key = string.Join(", ", storage.OwnerOrdinalKey.Columns.Reverse().Select(column => Q(column.Column.Identifier)));
            var recreateMembershipIndex =
                $"CREATE INDEX {Q(storage.MembershipKey.Name.Identifier)} ON {table} (" +
                $"{string.Join(", ", new[] { storage.MembershipKey.Value.Column.Identifier }
                    .Concat(storage.MembershipKey.OwnerColumns.Select(column => column.Column.Identifier))
                    .Select(Q))});";
            var sql = drift switch
            {
                CollectionElementDrift.WrongType =>
                    $"DROP INDEX {Q(storage.MembershipKey.Name.Identifier)} ON {table}; " +
                    $"ALTER TABLE {table} ALTER COLUMN {value} int NOT NULL; {recreateMembershipIndex}",
                CollectionElementDrift.WrongCollation =>
                    $"DROP INDEX {Q(storage.MembershipKey.Name.Identifier)} ON {table}; " +
                    $"ALTER TABLE {table} ALTER COLUMN {value} nvarchar(128) COLLATE Latin1_General_100_CI_AS NOT NULL; " +
                    recreateMembershipIndex,
                CollectionElementDrift.WrongDefault =>
                    $"ALTER TABLE {table} ADD CONSTRAINT {Q($"DF_{Guid.NewGuid():N}")} DEFAULT N'unexpected' FOR {value};",
                CollectionElementDrift.WrongPrimaryKeyOrder =>
                    $"ALTER TABLE {table} DROP CONSTRAINT {Q(await PrimaryKeyAsync(connection, storage.Storage.Name.Identifier))}; " +
                    $"ALTER TABLE {table} ADD CONSTRAINT {Q($"PK_{Guid.NewGuid():N}")} PRIMARY KEY NONCLUSTERED ({key});",
                _ => throw new ArgumentOutOfRangeException(nameof(drift), drift, null)
            };
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new SqlServerPhysicalSchemaExecutor(connectionString)));
        Assert.Contains(storage.Storage.Name.Identifier, exception.Message, StringComparison.Ordinal);

        static async Task<string> PrimaryKeyAsync(SqlConnection connection, string table)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(@table) AND type = 'PK';";
            command.Parameters.AddWithValue("@table", table);
            return (string)(await command.ExecuteScalarAsync())!;
        }
    }

    [Fact]
    public async Task Collection_membership_index_drift_is_rejected_from_live_schema()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: $"collection_{Guid.NewGuid():N}"[..19],
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
            includeCollection: true);
        var storage = Assert.Single(model.Target.Routes.Single().CollectionElementStorages);
        var connectionString = container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlServerPhysicalSchemaExecutor(connectionString));
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"DROP INDEX {Q(storage.MembershipKey.Name.Identifier)} ON {Q(storage.Storage.Name.Identifier)}; " +
                $"CREATE INDEX {Q(storage.MembershipKey.Name.Identifier)} ON {Q(storage.Storage.Name.Identifier)} (" +
                $"{string.Join(", ", storage.MembershipKey.OwnerColumns.Select(column => Q(column.Column.Identifier))
                    .Append(Q(storage.MembershipKey.Value.Column.Identifier)))});";
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlServerPhysicalSchemaExecutor(connectionString)));
        Assert.Contains(storage.MembershipKey.Name.Identifier, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(128, false)]
    [InlineData(129, true)]
    public async Task Additive_linked_string_projection_length_is_validated_before_sql_server_backfill(
        int valueLength,
        bool rejects)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.SharedDocuments,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.SharedDocuments,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: true,
            priorityType: PortablePhysicalType.String,
            priorityLength: 128,
            instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
        var documents = new SqlServerPhysicalDocumentStore(
            container.GetConnectionString(), initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global);
        var value = new string('a', valueLength);
        await documents.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "preexisting", "1", $"{{\"category\":\"tools\",\"priority\":\"{value}\"}}", 0));

        if (!rejects)
        {
            await PhysicalSchemaApplication.ApplyAsync(additive.Target, new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
            var route = additive.Target.Routes.Single();
            var evolved = new SqlServerPhysicalDocumentStore(
                container.GetConnectionString(), additive.Manifest, additive.Target.Routes, DocumentStoreAccess.Global);
            var result = await SqlServerPhysicalQueryRuntime.Create(evolved, additive.Manifest, route, additive.Target.Provider)
                .QueryAsync(new DocumentQuery(
                    "configurationDocument",
                    "find-by-category-priority",
                    [
                        DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools")),
                        DocumentQueryClause.Of(DocumentQueryComparison.Equal("priority", value))
                    ]));
            Assert.Equal("preexisting", Assert.Single(result.Documents).Id);
            return;
        }

        var exception = await Assert.ThrowsAsync<PhysicalProjectionValueValidationException>(() =>
            PhysicalSchemaApplication.ApplyAsync(additive.Target, new SqlServerPhysicalSchemaExecutor(container.GetConnectionString())));
        Assert.Equal("GW-PHYSICAL-037", exception.Diagnostic.Code);
        Assert.Contains(value, (await documents.LoadAsync("configurationDocument", "preexisting"))!.ContentJson);
        var inspection = await new SqlServerPhysicalSchemaExecutor(container.GetConnectionString())
            .InspectHistoryAsync(additive.Target, CancellationToken.None);
        Assert.Equal(initial.Target.Fingerprint, inspection.History.AppliedState?.TargetFingerprint);
        Assert.NotEqual(additive.Target.Fingerprint, inspection.History.AppliedState?.TargetFingerprint);
    }

    [Fact]
    public async Task Physical_factory_auto_applies_safe_schema_when_enabled()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var database = $"groundwork_startup_{suffix}";
        await ExecuteAdminAsync($"CREATE DATABASE {Q(database)};");
        var connectionString = new SqlConnectionStringBuilder(container.GetConnectionString())
        {
            InitialCatalog = database
        }.ConnectionString;
        try
        {
            var model = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.PhysicalEntityTable,
                SqlServerGroundworkCapabilities.Provider,
                includePriority: false,
                instance: suffix,
                normalizer: SqlServerGroundworkCapabilities.PhysicalNames);

            var store = await SqlServerDocumentStoreFactory.OpenPhysicalAsync(
                connectionString,
                model.Manifest,
                model.Target.Provider,
                DocumentStoreAccess.Global,
                namePolicy: new DelegatePhysicalNamePolicy(
                    context => $"gw_{suffix}_{context.FeatureDefaultLogicalName}"),
                options: new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true });
            var inspection = await new SqlServerPhysicalSchemaExecutor(connectionString)
                .InspectHistoryAsync(model.Target, CancellationToken.None);

            Assert.NotNull(store);
            Assert.Equal(model.Target.Fingerprint, inspection.History.AppliedState?.TargetFingerprint);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecuteAdminAsync(
                $"ALTER DATABASE {Q(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Q(database)};");
        }
    }

    [Fact]
    public void PrimaryInsertUsesGuardedKeyRangeLockWithoutMerge()
    {
        var sql = new SqlServerPhysicalDocumentDialect().InsertPrimaryIfAbsent(
            "documents",
            ["kind", "scope", "lookup"],
            ["@kind", "@scope", "@lookup"],
            ["kind", "scope", "lookup"],
            [
                new("kind", null, "@kind"),
                new("scope", null, "@scope"),
                new("lookup", null, "@lookup")
            ]);

        Assert.Contains("WHERE NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("UPDLOCK, HOLDLOCK", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("MERGE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public Task Bounded_transition_updates_the_exact_indexed_identity_set() =>
        RelationalBoundedMutationServerAssertions.TransitionUpdatesExactIndexedIdentitySetAsync(MutationHarness());

    [Fact]
    public Task Concurrent_bounded_retry_completes_once_and_replays_the_exact_count() =>
        RelationalBoundedMutationServerAssertions.ConcurrentRetryReplaysExactResultAsync(MutationHarness());

    [Fact]
    public Task Concurrent_distinct_transitions_serialize_the_selected_set() =>
        RelationalBoundedMutationServerAssertions.ConcurrentDistinctTransitionsSerializeSelectedSetAsync(MutationHarness());

    [Fact]
    public Task Direct_connection_mutations_serialize_the_selected_set() =>
        RelationalBoundedMutationServerAssertions.DirectConnectionDistinctTransitionSerializesSelectedSetAsync(MutationHarness());

    [Fact]
    public Task Concurrent_distinct_deletes_serialize_the_selected_set() =>
        RelationalBoundedMutationServerAssertions.ConcurrentDistinctDeletesSerializeSelectedSetAsync(MutationHarness());

    [Fact]
    public Task Collection_bearing_scalar_mutations_keep_element_storage_atomic() =>
        RelationalBoundedMutationServerAssertions.CollectionBearingScalarMutationsMaintainElementsAtomicallyAsync(MutationHarness());

    [Fact]
    public async Task Collection_delete_uses_the_owner_primary_key_inside_the_mutation_transaction()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: true,
            instance: Guid.NewGuid().ToString("N")[..8],
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
            includeCollection: true,
            mutationOptions: new(IncludeRangeDelete: true));
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var collection = Assert.Single(route.CollectionElementStorages);
        var store = new SqlServerPhysicalDocumentStore(
            container.GetConnectionString(), model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "collection-native-plan",
            "1",
            """{"category":"authorization-a","priority":1,"permissions":["read","write","audit"]}"""));

        string? plan = null;
        var runtime = RelationalPhysicalMutationRuntime.CreateWithInterceptor(
            new RelationalPhysicalMutationRuntimeContext(
                store,
                model.Manifest,
                route,
                model.Target.Provider,
                SqlServerGroundworkCapabilities.Provider.Name,
                "sqlserver"),
            async (point, connection, transaction, cancellationToken) =>
            {
                if (point != RelationalPhysicalMutationExecutionPoint.AfterSelection)
                    return;

                var command = RelationalPhysicalDocumentMutationHandler.BuildCollectionDeleteCommand(
                    store,
                    collection,
                    store.MutationSelectionTable(RelationalPhysicalDocumentMutationHandler.SelectionTable));
                plan = await ExplainCollectionDeleteAsync(connection, transaction, command, cancellationToken);
            });

        var result = await runtime.ExecuteAsync(new DocumentMutation(
            "configurationDocument",
            "prune-by-category-cutoff",
            "sqlserver-collection-native-plan",
            [
                DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "authorization-a")),
                DocumentQueryClause.Of(DocumentQueryComparison.LessThan("priority", "10"))
            ]));

        Assert.Equal(1, result.AffectedCount);
        Assert.True(
            UsesCollectionOwnerPrimaryKey(plan, collection),
            $"The exact production collection-delete command did not use the collection owner primary key. Native plan:{Environment.NewLine}{plan}");
    }

    [Fact]
    public Task Ordinary_save_and_delete_serialize_with_the_selected_set() =>
        RelationalBoundedMutationServerAssertions.OrdinaryCrudSerializesWithSelectedSetAsync(MutationHarness());

    [Fact]
    public Task Linked_ordinary_crud_interleavings_serialize_in_pooled_and_direct_sessions() =>
        RelationalBoundedMutationServerAssertions.LinkedOrdinaryCrudInterleavingsSerializeAsync(MutationHarness());

    [Fact]
    public Task Large_selection_uses_constant_set_based_lock_commands() =>
        RelationalBoundedMutationServerAssertions.LargeSelectionUsesConstantSetBasedLockCommandsAsync(MutationHarness());

    [Fact]
    public Task Bounded_transition_and_range_delete_cover_all_relational_storage_forms() =>
        RelationalBoundedMutationServerAssertions.PhysicalFormsExecuteTransitionAndRangeDeleteAsync(MutationHarness());

    [Fact]
    public Task Bounded_typed_transitions_preserve_canonical_and_projected_values() =>
        RelationalBoundedMutationServerAssertions.TypedTransitionsPreserveCanonicalAndProjectedValuesAsync(MutationHarness());

    [Fact]
    public Task Bounded_mutation_scope_is_inherited_from_the_store_session() =>
        RelationalBoundedMutationServerAssertions.MutationScopeIsInheritedFromStoreSessionAsync(MutationHarness());

    [Fact]
    public Task Bounded_mutation_failure_before_commit_rolls_back_and_can_retry() =>
        RelationalBoundedMutationServerAssertions.FailureBeforeCommitRollsBackAndRetryCompletesAsync(MutationHarness());

    [Fact]
    public Task Bounded_mutation_cancellation_rolls_back_and_preserves_the_token() =>
        RelationalBoundedMutationServerAssertions.CancellationBeforeCommitRollsBackAndPreservesTokenAsync(MutationHarness());

    [Fact]
    public Task Bounded_mutation_acknowledgement_loss_restarts_and_replays_across_provider_upgrade() =>
        RelationalBoundedMutationServerAssertions.AcknowledgementLossRestartAndProviderUpgradeReplayAsync(MutationHarness());

    [Fact]
    public Task Cursor_pages_resume_across_a_reopened_store() =>
        RelationalPhysicalServerAssertions.CursorPagesResumeAcrossReopenedStoreAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            () => new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new SqlServerPhysicalDocumentStore(
                container.GetConnectionString(),
                manifest,
                routes,
                DocumentStoreAccess.Global),
            "sqlserver");

    [Fact]
    public async Task Sort_only_index_field_residual_filters_before_cursor_limit_and_binds_continuation()
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var manifest = SortOnlyResidualPredicateConformance.CreateManifest(instance);
        var target = SortOnlyResidualPredicateConformance.CreateTarget(
            manifest,
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            instance);
        await PhysicalSchemaApplication.ApplyAsync(
            target,
            new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = target.Routes.Single();
        var store = new SqlServerPhysicalDocumentStore(
            container.GetConnectionString(),
            manifest,
            target.Routes,
            DocumentStoreAccess.Global);
        var runtime = SqlServerPhysicalQueryRuntime.Create(
            store,
            manifest,
            route,
            target.Provider);

        await SortOnlyResidualPredicateConformance.VerifyAsync(store, runtime);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public Task Latest_per_key_filters_before_grouping_and_pages_deterministic_representatives(
        PhysicalStorageForm form) =>
        RelationalPhysicalServerAssertions.LatestPerKeyFiltersAndPagesAsync(
            form,
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            () => new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new SqlServerPhysicalDocumentStore(
                container.GetConnectionString(),
                manifest,
                routes,
                DocumentStoreAccess.Global),
            (store, manifest, route) => SqlServerPhysicalQueryRuntime.Create(
                Assert.IsType<SqlServerPhysicalDocumentStore>(store),
                manifest,
                route,
                SqlServerGroundworkCapabilities.Provider));

    [Fact]
    public async Task Public_query_explain_executes_exact_parameterized_reads_and_restores_the_pooled_session()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
            categoryPaging: QueryPagingSupport.Cursor);
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var store = new SqlServerPhysicalDocumentStore(
            container.GetConnectionString(), model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "showplan-target", "1", "{\"category\":\"owner's-pending\"}"))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "showplan-target-2", "1", "{\"category\":\"owner's-pending\"}"))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "showplan-noise", "1", "{\"category\":\"tools\"}"))).Status);
        await SeedPlanNoiseAsync(route);
        await ExecuteAdminAsync($"UPDATE STATISTICS {Q(route.PrimaryStorage.Name.Identifier)};");
        var runtime = SqlServerPhysicalQueryRuntime.Create(store, model.Manifest, route, model.Target.Provider);
        var explainer = Assert.IsAssignableFrom<IPhysicalDocumentQueryExplainer>(runtime);
        var query = new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "owner's-pending"))],
            take: 1);

        var first = await runtime.QueryAsync(query);
        Assert.NotNull(first.NextContinuation);
        var continued = new DocumentQuery(
            query.DocumentKind,
            query.QueryIdentity,
            query.Clauses,
            query.Order,
            take: 1,
            continuation: first.NextContinuation);
        var explanation = await explainer.ExplainAsync(continued);
        var result = await runtime.QueryAsync(continued);

        Assert.Equal(["count", "page"], explanation.Commands.Select(command => command.Identity));
        Assert.All(explanation.Commands, command =>
        {
            Assert.Equal("sqlserver-statistics-xml", command.NativePlanFormat);
            Assert.Contains("ShowPlanXML", command.NativePlan, StringComparison.Ordinal);
            Assert.Contains(explanation.Plan.IndexName!.Identifier, command.NativePlan, StringComparison.Ordinal);
            Assert.DoesNotContain("PhysicalOp=\"Table Scan\"", command.NativePlan, StringComparison.Ordinal);
            Assert.DoesNotContain("PhysicalOp=\"Index Scan\"", command.NativePlan, StringComparison.Ordinal);
        });
        var page = explanation.Commands.Single(command =>
            command.Identity == PhysicalDocumentQueryCommandIdentities.Page);
        Assert.Contains("PhysicalOp=\"Top\"", page.NativePlan, StringComparison.Ordinal);
        Assert.DoesNotContain("PhysicalOp=\"Sort\"", page.NativePlan, StringComparison.Ordinal);
        Assert.Single(result.Documents);
        Assert.Null(result.NextContinuation);
    }

    [Fact]
    public async Task Query_explain_preserves_primary_failure_attaches_disable_failure_and_quarantines_the_session()
    {
        var connectionString = new SqlConnectionStringBuilder(container.GetConnectionString()) { MaxPoolSize = 1 }.ConnectionString;
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new SqlServerPhysicalSchemaExecutor(connectionString));
        var route = model.Target.Routes.Single();
        var store = new SqlServerPhysicalDocumentStore(
            connectionString, model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "cleanup-target", "1", "{\"category\":\"pending\"}"))).Status);
        var hooks = new SqlServerPhysicalQueryExplainHooks(
            BeforeRead: static (_, _) => ValueTask.FromException(new InvalidOperationException("primary explain failure")),
            BeforeDisable: static _ => ValueTask.FromException(new InvalidOperationException("disable failure")));
        var runtime = SqlServerPhysicalQueryRuntime.Create(
            store, model.Manifest, route, model.Target.Provider, hooks);
        var query = new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "pending"))],
            take: 1);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Assert.IsAssignableFrom<IPhysicalDocumentQueryExplainer>(runtime).ExplainAsync(query));
        var cleanupFailures = Assert.IsType<List<Exception>>(
            exception.Data["Groundwork.Relational.CleanupFailures"]);
        var result = await runtime.QueryAsync(query);

        Assert.Equal("primary explain failure", exception.Message);
        Assert.Contains(cleanupFailures, failure => failure.Message == "disable failure");
        Assert.Equal("cleanup-target", Assert.Single(result.Documents).Id);
    }

    [Fact]
    public async Task Bounded_mutation_explains_the_exact_execution_stages_with_the_declared_physical_index()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
            mutationOptions: new(IncludeCategoryTransition: true));
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var store = new SqlServerPhysicalDocumentStore(
            container.GetConnectionString(), model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "plan-target", "1", "{\"category\":\"pending\"}"))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "plan-noise", "1", "{\"category\":\"tools\"}"))).Status);
        await SeedPlanNoiseAsync(route);
        var mutationContext = new RelationalPhysicalMutationRuntimeContext(
            store,
            model.Manifest,
            route,
            model.Target.Provider,
            SqlServerGroundworkCapabilities.Provider.Name,
            "sqlserver");
        var request = new DocumentMutation("configurationDocument", "revoke-pending", "explain");
        var evidence = await SqlServerPhysicalMutationRuntime
            .Create(store, model.Manifest, route, model.Target.Provider)
            .ExplainAsync(request);
        var executed = new List<(string Identity, string CommandText, long? PreparedRestrictionRowCount)>();
        var execution = RelationalPhysicalMutationRuntime.CreateWithSelectionObserver(
            mutationContext,
            (identity, command, preparedRestrictionRowCount) =>
            {
                executed.Add((identity, command.CommandText, preparedRestrictionRowCount));
                return ValueTask.CompletedTask;
            });

        var expectedIndex = route.Indexes.Single(index => index.Identity == "by-category").Name.Identifier;
        var result = await execution.ExecuteAsync(request);
        Assert.Equal(BoundedMutationStatus.Completed, result.Status);
        Assert.Equal(1, result.AffectedCount);
        Assert.Equal(
            evidence.Commands.Select(command => (
                command.Identity,
                command.RenderedCommand!,
                command.PreparedRestrictionRowCount)),
            executed);
        Assert.Null(evidence.Commands[0].PreparedRestrictionRowCount);
        Assert.True(evidence.Commands[1].PreparedRestrictionRowCount > 0);
        Assert.All(evidence.Commands, command =>
        {
            Assert.Contains(expectedIndex, command.NativePlan, StringComparison.Ordinal);
            Assert.Contains("Index Seek", command.NativePlan, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Bounded_mutation_ledger_lookup_seeks_the_hash_primary_key_at_scale()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
            mutationOptions: new(IncludeCategoryTransition: true));
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var store = new SqlServerPhysicalDocumentStore(
            container.GetConnectionString(), model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "ledger-plan", "1", "{\"category\":\"pending\"}"))).Status);
        var request = new DocumentMutation(
            "configurationDocument",
            "revoke-pending",
            "sqlserver-ledger-plan-target");
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await SqlServerPhysicalMutationRuntime.Create(store, model.Manifest, route, model.Target.Provider)
                .ExecuteAsync(request));
        await SeedMutationLedgerPlanNoiseAsync(request.OperationId);
        var mutationContext = new RelationalPhysicalMutationRuntimeContext(
            store,
            model.Manifest,
            route,
            model.Target.Provider,
            SqlServerGroundworkCapabilities.Provider.Name,
            "sqlserver");
        var lookup = RelationalPhysicalMutationRuntime.BuildOperationReadCommand(mutationContext, request);
        Assert.All(
            new[] { "manifest_key", "provider_key", "storage_unit_key", "storage_scope_key", "operation_key" },
            key => Assert.Contains(key, lookup.CommandText, StringComparison.Ordinal));
        Assert.Equal(5, lookup.CommandText.Split("varbinary(max)", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("varbinary(900)", lookup.CommandText, StringComparison.Ordinal);

        await ExecuteAdminAsync($"UPDATE STATISTICS {Q(RelationalPhysicalStorageColumns.MutationOperationsTable)};");
        var plan = await ExplainAsync(lookup, route);

        Assert.Contains("PK_groundwork_document_mutation_operations", plan, StringComparison.Ordinal);
        Assert.Contains("Index Seek", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Table Scan", plan, StringComparison.Ordinal);
    }

    [Fact]
    public Task ConcurrentMaterializationAndAcknowledgementLossAreRestartSafe()
    {
        var connectionString = new SqlConnectionStringBuilder(container.GetConnectionString()) { MaxPoolSize = 1 }.ConnectionString;
        return RelationalPhysicalServerAssertions.ConcurrentMaterializationAndAcknowledgementLossAreRestartSafeAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            () => new SqlServerPhysicalSchemaExecutor(connectionString));
    }

    [Fact]
    public Task Unpublished_backfill_acknowledgement_loss_replays_interleaved_writes() =>
        RelationalPhysicalServerAssertions.UnpublishedBackfillAcknowledgementLossReplaysInterleavedWritesAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            () => new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new SqlServerPhysicalDocumentStore(
                container.GetConnectionString(),
                manifest,
                routes,
                DocumentStoreAccess.Global),
            "sqlserver");

    [Fact]
    public async Task Concurrent_distinct_targets_can_bootstrap_a_clean_database()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var database = $"groundwork_bootstrap_{suffix}";
        await ExecuteAdminAsync($"CREATE DATABASE {Q(database)};");
        var connectionString = new SqlConnectionStringBuilder(container.GetConnectionString())
        {
            InitialCatalog = database
        }.ConnectionString;
        try
        {
            var first = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.PhysicalEntityTable,
                SqlServerGroundworkCapabilities.Provider,
                includePriority: true,
                instance: $"first-{suffix}",
                normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
            var second = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.DedicatedDocumentTable,
                SqlServerGroundworkCapabilities.Provider,
                includePriority: true,
                instance: $"second-{suffix}",
                normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
            var results = await Task.WhenAll(
                PhysicalSchemaApplication.ApplyAsync(first.Target, new SqlServerPhysicalSchemaExecutor(connectionString)),
                PhysicalSchemaApplication.ApplyAsync(second.Target, new SqlServerPhysicalSchemaExecutor(connectionString)));
            Assert.All(results, result => Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecuteAdminAsync(
                $"ALTER DATABASE {Q(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Q(database)};");
        }
    }

    [Theory]
    [InlineData(InfrastructureTamper.WrongObjectKind)]
    [InlineData(InfrastructureTamper.ExtraOperationsColumn)]
    [InlineData(InfrastructureTamper.MissingStateColumn)]
    [InlineData(InfrastructureTamper.NullableLockOwner)]
    [InlineData(InfrastructureTamper.WrongLockOwnerType)]
    [InlineData(InfrastructureTamper.WrongStateCollation)]
    [InlineData(InfrastructureTamper.MissingStatePrimaryKey)]
    [InlineData(InfrastructureTamper.ReorderedOperationsPrimaryKey)]
    [InlineData(InfrastructureTamper.PlainHash)]
    [InlineData(InfrastructureTamper.NonPersistedHash)]
    [InlineData(InfrastructureTamper.WrongHashExpression)]
    public async Task Restart_rejects_malformed_infrastructure_before_target_fence_mutation(InfrastructureTamper tamper)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var database = $"groundwork_infrastructure_{suffix}";
        await ExecuteAdminAsync($"CREATE DATABASE {Q(database)};");
        var connectionString = new SqlConnectionStringBuilder(container.GetConnectionString())
        {
            InitialCatalog = database
        }.ConnectionString;
        try
        {
            var initial = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.PhysicalEntityTable,
                SqlServerGroundworkCapabilities.Provider,
                includePriority: true,
                instance: $"initial-{suffix}",
                normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
            await PhysicalSchemaApplication.ApplyAsync(initial.Target, new SqlServerPhysicalSchemaExecutor(connectionString));
            await TamperInfrastructureAsync(connectionString, tamper);

            var rejected = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.PhysicalEntityTable,
                SqlServerGroundworkCapabilities.Provider,
                includePriority: true,
                instance: $"rejected-{suffix}",
                normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using var unused = await new SqlServerPhysicalSchemaExecutor(connectionString)
                    .AcquireApplicationLockAsync(rejected.Target.Identity, CancellationToken.None);
            });
            Assert.Contains("Physical-schema infrastructure", exception.Message, StringComparison.Ordinal);
            if (tamper == InfrastructureTamper.NonPersistedHash)
                Assert.Contains("persisted 'False'", exception.Message, StringComparison.Ordinal);
            if (tamper == InfrastructureTamper.WrongHashExpression)
                Assert.Contains("N'wrong'", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await CountFenceAsync(connectionString, rejected.Target.Identity));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecuteAdminAsync(
                $"ALTER DATABASE {Q(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Q(database)};");
        }
    }

    [Fact]
    public async Task Colliding_long_Unicode_table_prefixes_get_distinct_restart_safe_primary_key_names()
    {
        var sharedPrefix = new string('界', 125);
        var firstTable = sharedPrefix + "一二三";
        var secondTable = sharedPrefix + "四五六";
        var first = CreateLongNamedModel("long-name-first", firstTable);
        var second = CreateLongNamedModel("long-name-second", secondTable);
        var connectionString = container.GetConnectionString();

        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(first.Target, new SqlServerPhysicalSchemaExecutor(connectionString))).Outcome);
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(second.Target, new SqlServerPhysicalSchemaExecutor(connectionString))).Outcome);

        var firstConstraint = await ReadPrimaryKeyNameAsync(firstTable);
        var secondConstraint = await ReadPrimaryKeyNameAsync(secondTable);
        Assert.NotEqual(firstConstraint, secondConstraint);
        Assert.True(firstConstraint.Length <= 128);
        Assert.True(secondConstraint.Length <= 128);
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.NoChanges,
            (await PhysicalSchemaApplication.ApplyAsync(first.Target, new SqlServerPhysicalSchemaExecutor(connectionString))).Outcome);
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.NoChanges,
            (await PhysicalSchemaApplication.ApplyAsync(second.Target, new SqlServerPhysicalSchemaExecutor(connectionString))).Outcome);

        (Groundwork.Core.Manifests.StorageManifest Manifest, PhysicalSchemaTarget Target) CreateLongNamedModel(
            string instance,
            string table) =>
            RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.PhysicalEntityTable,
                SqlServerGroundworkCapabilities.Provider,
                includePriority: false,
                instance: instance,
                normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
                namePolicy: context => context.ObjectKind == PhysicalObjectKind.PrimaryStorage
                    ? table
                    : $"gw_{instance}_{context.FeatureDefaultLogicalName}");
    }

    [Fact]
    public Task Application_lock_disposal_is_heartbeat_race_safe() =>
        RelationalPhysicalServerAssertions.ApplicationLockDisposalIsHeartbeatRaceSafeAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            () => new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));

    [Fact]
    public Task Terminated_lock_session_disposal_is_immediate_and_idempotent() =>
        RelationalPhysicalServerAssertions.TerminatedApplicationLockDisposalIsIdempotentAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            () => new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()),
            SqlServerPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync);

    [Fact]
    public async Task Exhausted_fence_fails_without_poisoning_the_session_lock()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: true,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        var executor = new SqlServerPhysicalSchemaExecutor(container.GetConnectionString());
        await using (await executor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None))
        {
        }
        await SetFenceAsync(model.Target.Identity, long.MaxValue);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var rejected = await executor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None);
        });
        Assert.Contains("fence is exhausted", exception.Message, StringComparison.Ordinal);

        await SetFenceAsync(model.Target.Identity, 41);
        await using var successor = await executor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public Task Terminated_lock_session_cannot_publish_operation_evidence() =>
        RelationalPhysicalServerAssertions.LostOperationLockCannotPublishEvidenceAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            (beforeOperation, beforeState) => new SqlServerPhysicalSchemaExecutor(
                container.GetConnectionString(),
                new SqlServerPhysicalIdentityHash(),
                beforeOperation,
                beforeState),
            SqlServerPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync,
            CountOperationEvidenceAsync,
            TableExistsAsync);

    [Fact]
    public Task Terminated_lock_session_cannot_publish_applied_state() =>
        RelationalPhysicalServerAssertions.LostStateLockCannotPublishAppliedStateAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            (beforeOperation, beforeState) => new SqlServerPhysicalSchemaExecutor(
                container.GetConnectionString(),
                new SqlServerPhysicalIdentityHash(),
                beforeOperation,
                beforeState),
            SqlServerPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync,
            CountAppliedStateAsync);

    [Fact]
    public Task Non_provider_failure_after_real_lock_loss_marks_ownership_lost_and_uses_stable_error() =>
        RelationalPhysicalServerAssertions.NonProviderFailureAfterRealLockLossUsesStableErrorAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            beforeState => new SqlServerPhysicalSchemaExecutor(
                container.GetConnectionString(),
                new SqlServerPhysicalIdentityHash(),
                null,
                beforeState),
            SqlServerPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync,
            CountAppliedStateAsync);

    [Fact]
    public Task Ordinary_invalid_operation_preserves_owned_lock_and_original_error() =>
        RelationalPhysicalServerAssertions.OrdinaryInvalidOperationPreservesOwnedLockAndOriginalErrorAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            beforeState => new SqlServerPhysicalSchemaExecutor(
                container.GetConnectionString(),
                new SqlServerPhysicalIdentityHash(),
                null,
                beforeState));

    [Fact]
    public Task Terminated_lock_session_cannot_commit_backfill_or_operation_evidence() =>
        RelationalPhysicalServerAssertions.LostBackfillLockCannotCommitDataOrEvidenceAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            (beforeOperation, beforeState) => new SqlServerPhysicalSchemaExecutor(
                container.GetConnectionString(),
                new SqlServerPhysicalIdentityHash(),
                beforeOperation,
                beforeState),
            (manifest, routes) => new SqlServerPhysicalDocumentStore(
                container.GetConnectionString(), manifest, routes, DocumentStoreAccess.Global),
            SqlServerPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync,
            CountOperationEvidenceAsync,
            CountProjectedValuesAsync);

    [Fact]
    public Task DecimalLiveAndBackfillValuesUseTheSameNativeSemantics() =>
        RelationalPhysicalServerAssertions.TypedProjectionLiveAndBackfillValuesRemainEquivalentAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            () => new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new SqlServerPhysicalDocumentStore(
                container.GetConnectionString(), manifest, routes, DocumentStoreAccess.Global),
            "sqlserver",
            PortablePhysicalType.Decimal,
            "12.3400",
            "12.34",
            "12.34",
            precision: 18,
            scale: 4);

    [Fact]
    public Task DateTimeLiveAndBackfillValuesPreserveEquivalentUtcTicks() =>
        RelationalPhysicalServerAssertions.TypedProjectionLiveAndBackfillValuesRemainEquivalentAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            () => new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new SqlServerPhysicalDocumentStore(
                container.GetConnectionString(), manifest, routes, DocumentStoreAccess.Global),
            "sqlserver",
            PortablePhysicalType.DateTime,
            "\"2026-01-01T00:00:00.0000001+01:00\"",
            "\"2025-12-31T23:00:00.0000001Z\"",
            "2025-12-31T23:00:00.0000001Z");

    [Fact]
    public async Task ExistingIncompatibleSchemaIsRejectedInsteadOfAcceptedByName()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: true,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        await using (var connection = new SqlConnection(container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE {Q(model.Target.Routes.Single().PrimaryStorage.Name.Identifier)} ([wrong] int NOT NULL);";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new SqlServerPhysicalSchemaExecutor(container.GetConnectionString())));
    }

    [Fact]
    public Task NullableUniqueProjectionUsesPortableNullDistinctSemantics() =>
        RelationalPhysicalServerAssertions.NullableUniqueProjectionUsesPortableNullDistinctSemanticsAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            () => new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new SqlServerPhysicalDocumentStore(
                container.GetConnectionString(), manifest, routes, DocumentStoreAccess.Global));

    [Fact]
    public async Task FourHundredFiftyCharacterUnicodeKindAndIdSupportCrudOccAndRestart()
    {
        var kind = string.Concat(Enumerable.Repeat("界", 450));
        var id = string.Concat(Enumerable.Repeat("é", 450));
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.SharedDocuments,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
            documentKind: kind);
        var connectionString = container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlServerPhysicalSchemaExecutor(connectionString));

        var store = new SqlServerPhysicalDocumentStore(
            connectionString, model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        var saved = await store.SaveAsync(new SaveDocumentRequest(kind, id, "1", "{\"category\":\"before\"}", 0));
        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        Assert.Equal(1, saved.Document!.Version);
        Assert.Equal(id, (await store.LoadAsync(kind, id))!.Id);

        var updated = await store.SaveAsync(new SaveDocumentRequest(kind, id, "1", "{\"category\":\"after\"}", 1));
        Assert.Equal(DocumentStoreWriteStatus.Saved, updated.Status);
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, (await store.SaveAsync(
            new SaveDocumentRequest(kind, id, "1", "{\"category\":\"stale\"}", 1))).Status);

        var restarted = new SqlServerPhysicalDocumentStore(
            connectionString, model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        var loaded = await restarted.LoadAsync(kind, id);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Version);
        Assert.Equal("{\"category\":\"after\"}", loaded.ContentJson);
        Assert.Equal(DocumentStoreWriteStatus.Deleted, (await restarted.DeleteAsync(
            new DeleteDocumentRequest(kind, id, 2))).Status);
        Assert.Null(await new SqlServerPhysicalDocumentStore(
            connectionString, model.Manifest, model.Target.Routes, DocumentStoreAccess.Global).LoadAsync(kind, id));
    }

    [Fact]
    public async Task RetainedIdentityMismatchBehindTheSameHashThrowsDedicatedCollision()
    {
        var hash = new SqlServerPhysicalIdentityHash(_ =>
            "CONVERT(binary(32), 0x0000000000000000000000000000000000000000000000000000000000000000)");
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.SharedDocuments,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        var connectionString = container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new SqlServerPhysicalSchemaExecutor(connectionString, hash));
        var store = new SqlServerPhysicalDocumentStore(
            connectionString,
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Global,
            hash);

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "first", "1", "{\"category\":\"tools\"}", 0))).Status);
        var collision = await Assert.ThrowsAsync<PhysicalIdentityHashCollisionException>(() => store.SaveAsync(
            new SaveDocumentRequest("configurationDocument", "second", "1", "{\"category\":\"tools\"}", 0)));

        Assert.Equal(model.Target.Routes.Single().PrimaryStorage.Name.Identifier, collision.Table);
        Assert.Equal(3, collision.IdentityColumns.Count);
        Assert.NotNull(await store.LoadAsync("configurationDocument", "first"));
        Assert.Null(await store.LoadAsync("configurationDocument", "second"));
    }

    [Fact]
    public async Task Same_target_restart_rejects_tampered_primary_and_linked_identity_hash_columns()
    {
        foreach (var linked in new[] { false, true })
        {
            foreach (var tamper in Enum.GetValues<IdentityHashTamper>())
            {
                var model = RelationalPhysicalStorageTestModels.Create(
                    linked ? PhysicalStorageForm.SharedDocuments : PhysicalStorageForm.PhysicalEntityTable,
                    SqlServerGroundworkCapabilities.Provider,
                    includePriority: false,
                    normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
                await PhysicalSchemaApplication.ApplyAsync(
                    model.Target,
                    new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
                var route = model.Target.Routes.Single();
                var table = linked
                    ? route.LinkedIndexStorage!.Name.Identifier
                    : route.PrimaryStorage.Name.Identifier;
                var retainedId = linked
                    ? route.LinkedRelationship!.DocumentId.Identifier
                    : route.Envelope.Id.Identifier;
                await TamperIdentityHashColumnAsync(table, retainedId, tamper);

                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    PhysicalSchemaApplication.ApplyAsync(
                        model.Target,
                        new SqlServerPhysicalSchemaExecutor(container.GetConnectionString())));

                Assert.Contains(SqlServerPhysicalIdentity.HiddenColumn(retainedId), exception.Message, StringComparison.Ordinal);
                Assert.Contains("computed", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Linked_backfill_hash_collision_rolls_back_all_batches_and_can_retry_after_correction()
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            dedicatedWithoutLinked: true,
            instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        Assert.Null(initial.Target.Routes.Single().LinkedIndexStorage);
        Assert.NotNull(additive.Target.Routes.Single().LinkedIndexStorage);
        Assert.NotEqual(initial.Target.Fingerprint, additive.Target.Fingerprint);
        var connectionString = container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, new SqlServerPhysicalSchemaExecutor(connectionString));
        var initialStore = new SqlServerPhysicalDocumentStore(
            connectionString, initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global);
        for (var index = 0; index < 256; index++)
        {
            Assert.Equal(DocumentStoreWriteStatus.Saved, (await initialStore.SaveAsync(new SaveDocumentRequest(
                "configurationDocument", $"{index:D4}", "1", "{\"category\":\"tools\"}", 0))).Status);
        }
        foreach (var id in new[] { "zz-collision-a", "zz-collision-b" })
        {
            Assert.Equal(DocumentStoreWriteStatus.Saved, (await initialStore.SaveAsync(new SaveDocumentRequest(
                "configurationDocument", id, "1", "{\"category\":\"tools\"}", 0))).Status);
        }

        var route = additive.Target.Routes.Single();
        var linkedLookup = route.LinkedRelationship!.Identity.LookupKey.Identifier;
        var linkedLookupExpression = Q(linkedLookup);
        var collisionLookups = new[] { "zz-collision-a", "zz-collision-b" }
            .Select(id => route.LinkedRelationship.Identity.Project(id).LookupKey)
            .Select(value => $"0x{value}")
            .ToArray();
        var hash = new SqlServerPhysicalIdentityHash(value =>
            string.Equals(value, linkedLookupExpression, StringComparison.Ordinal) ||
            string.Equals(value, "@collisionLookup", StringComparison.Ordinal) ||
            string.Equals(value, "@v4", StringComparison.Ordinal)
                ? $"CASE WHEN {string.Join(" OR ", collisionLookups.Select(lookup => $"{value} = {lookup}"))} " +
                  "THEN CONVERT(binary(32), 0x0000000000000000000000000000000000000000000000000000000000000000) " +
                  $"ELSE CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), {value}))) END"
                : $"CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), {value})))");
        var executor = new SqlServerPhysicalSchemaExecutor(connectionString, hash);
        PhysicalSchemaDiffPlan plan;
        await using (var applicationLock = await executor.AcquireApplicationLockAsync(additive.Target.Identity, CancellationToken.None))
        {
            var history = await executor.ReadHistoryAsync(additive.Target.Identity, applicationLock, CancellationToken.None);
            plan = PhysicalSchemaDiffPlanner.Plan(additive.Target, history, DateTimeOffset.UtcNow);
        }
        var backfills = plan.Operations.OfType<BackfillCanonicalJsonOperation>()
            .Where(operation => operation.Target == ExecutableStorageObjectRole.LinkedIndexStorage)
            .ToArray();
        Assert.NotEmpty(backfills);

        var collision = await Assert.ThrowsAsync<PhysicalIdentityHashCollisionException>(() =>
            PhysicalSchemaApplication.ApplyAsync(additive.Target, executor));
        Assert.Equal(route.LinkedIndexStorage!.Name.Identifier, collision.Table);
        Assert.Equal(0, await CountRowsAsync(route.LinkedIndexStorage.Name.Identifier));
        foreach (var backfill in backfills)
            Assert.Equal(0, await CountOperationEvidenceAsync(backfill.Identity, backfill.Fingerprint));

        Assert.Equal(DocumentStoreWriteStatus.Deleted, (await initialStore.DeleteAsync(
            new DeleteDocumentRequest("configurationDocument", "zz-collision-b", 1))).Status);
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(
                additive.Target,
                new SqlServerPhysicalSchemaExecutor(connectionString, hash))).Outcome);
        Assert.Equal(257, await CountRowsAsync(route.LinkedIndexStorage.Name.Identifier));
    }

    protected override Task<RelationalPhysicalStorageFixture> CreateAsync(
        PhysicalStorageForm form,
        bool dedicatedWithoutLinked = false) =>
        CreateFixtureAsync(RelationalPhysicalStorageTestModels.Create(
            form,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: true,
            dedicatedWithoutLinked: dedicatedWithoutLinked,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames));

    protected override async Task<RelationalServerIdentityFixture> CreateIdentityAsync(
        PhysicalStorageForm form,
        StringIdentityCasePolicy stringCasePolicy = StringIdentityCasePolicy.Ordinal)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            form,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: true,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
            stringCasePolicy: stringCasePolicy);
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var store = new SqlServerPhysicalDocumentStore(
            container.GetConnectionString(), model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        return new RelationalServerIdentityFixture(
            store,
            SqlServerPhysicalQueryRuntime.Create(store, model.Manifest, route, model.Target.Provider),
            route,
            synchronizeAfterPrimaryLock: false,
            lookupKey => CorruptPrimaryLookupAsync(route, lookupKey),
            (retainedId, comparisonKey) => CorruptLinkedIdentityAsync(route, retainedId, comparisonKey),
            linked => ReadIdentitySchemaAsync(route, linked),
            linked => DropComparisonEvidenceAsync(route, linked),
            async () =>
            {
                await PhysicalSchemaApplication.ApplyAsync(
                    model.Target,
                    new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
            },
            SqlServerPhysicalIdentity.HiddenColumn,
            () => ValueTask.CompletedTask);
    }

    protected override async Task<RelationalServerLinkedBackfillCollisionFixture> CreateLinkedBackfillCollisionAsync()
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: true,
            instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        var connectionString = container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(
            initial.Target,
            new SqlServerPhysicalSchemaExecutor(connectionString));
        var route = additive.Target.Routes.Single();
        var priority = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "priority");
        return new RelationalServerLinkedBackfillCollisionFixture(
            new SqlServerPhysicalDocumentStore(
                connectionString,
                initial.Manifest,
                initial.Target.Routes,
                DocumentStoreAccess.Global),
            route,
            (lookupKey, retainedId, comparisonKey) => SetLinkedIdentityAsync(
                route,
                lookupKey,
                retainedId,
                comparisonKey),
            async () => (await PhysicalSchemaApplication.ApplyAsync(
                additive.Target,
                new SqlServerPhysicalSchemaExecutor(connectionString))).Outcome,
            () => ReadNullableInt32Async(
                route.LinkedIndexStorage!.Name.Identifier,
                priority.Column.Identifier,
                route.LinkedRelationship!.DocumentId.Identifier),
            () => ValueTask.CompletedTask);
    }

    protected override async Task<RelationalScopedPhysicalStorageFixture> CreateScopedAsync(PhysicalStorageForm form)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            form,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: true,
            scoped: true,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
        return new RelationalScopedPhysicalStorageFixture(
            access => new SqlServerPhysicalDocumentStore(container.GetConnectionString(), model.Manifest, model.Target.Routes, access),
            () => ValueTask.CompletedTask);
    }

    protected override async Task<RelationalPhysicalStorageEvolutionFixture> CreateEvolutionAsync(PhysicalStorageForm form)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            form, SqlServerGroundworkCapabilities.Provider, includePriority: false, instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        var additive = RelationalPhysicalStorageTestModels.Create(
            form, SqlServerGroundworkCapabilities.Provider, includePriority: true, instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
        var initialStore = new SqlServerPhysicalDocumentStore(
            container.GetConnectionString(), initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global);
        return new RelationalPhysicalStorageEvolutionFixture(
            initialStore,
            () => ApplyAndCreateAsync(additive),
            async () => (await PhysicalSchemaApplication.ApplyAsync(
                additive.Target,
                new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()))).Outcome,
            async cancellationToken => await new SqlServerPhysicalSchemaExecutor(container.GetConnectionString())
                .AcquireApplicationLockAsync(additive.Target.Identity, cancellationToken),
            () => ValueTask.CompletedTask);
    }

    private async Task<RelationalPhysicalStorageFixture> ApplyAndCreateAsync(
        (Groundwork.Core.Manifests.StorageManifest Manifest, PhysicalSchemaTarget Target) model)
    {
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
        return await CreateFixtureAsync(model, apply: false);
    }

    private async Task<RelationalPhysicalStorageFixture> CreateFixtureAsync(
        (Groundwork.Core.Manifests.StorageManifest Manifest, PhysicalSchemaTarget Target) model,
        bool apply = true)
    {
        if (apply)
            await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var store = new SqlServerPhysicalDocumentStore(
            container.GetConnectionString(), model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        return new RelationalPhysicalStorageFixture(
            store,
            route.ProjectedColumns.Count == 0
                ? null
                : SqlServerPhysicalQueryRuntime.Create(store, model.Manifest, route, model.Target.Provider),
            route,
            () => ExplainCategoryLookupAsync(store, model.Manifest, route, model.Target.Provider),
            () => ValueTask.CompletedTask);
    }

    private async Task<string> ExplainCategoryLookupAsync(
        SqlServerPhysicalDocumentStore store,
        Groundwork.Core.Manifests.StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider)
    {
        var query = new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))],
            resultOperation: BoundedQueryResultOperation.Count);
        await SeedPlanNoiseAsync(route);
        await ExecuteAdminAsync(route.LinkedIndexStorage is null
            ? $"UPDATE STATISTICS {Q(route.PrimaryStorage.Name.Identifier)};"
            : $"UPDATE STATISTICS {Q(route.PrimaryStorage.Name.Identifier)}; UPDATE STATISTICS {Q(route.LinkedIndexStorage.Name.Identifier)};");
        var runtime = SqlServerPhysicalQueryRuntime.Create(store, manifest, route, provider);
        var explanation = await Assert.IsAssignableFrom<IPhysicalDocumentQueryExplainer>(runtime).ExplainAsync(query);
        return string.Join(Environment.NewLine, explanation.Commands.Select(command => command.NativePlan));
    }

    private async Task<string> ExplainAsync(
        RelationalPhysicalQueryCommand rendered,
        ExecutableStorageRoute route)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using (var statistics = connection.CreateCommand())
        {
            statistics.CommandText = route.LinkedIndexStorage is null
                ? $"UPDATE STATISTICS {Q(route.PrimaryStorage.Name.Identifier)};"
                : $"UPDATE STATISTICS {Q(route.PrimaryStorage.Name.Identifier)}; UPDATE STATISTICS {Q(route.LinkedIndexStorage.Name.Identifier)};";
            await statistics.ExecuteNonQueryAsync();
        }
        await using (var enable = connection.CreateCommand())
        {
            enable.CommandText = "SET STATISTICS XML ON;";
            await enable.ExecuteNonQueryAsync();
        }
        await using var command = connection.CreateCommand();
        command.CommandText = rendered.CommandText;
        foreach (var (name, value) in rendered.Parameters)
            command.Parameters.AddWithValue($"@{name}", value ?? DBNull.Value);
        var plans = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            while (await reader.ReadAsync())
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                    if (reader.GetName(ordinal).Contains("XML Showplan", StringComparison.OrdinalIgnoreCase) ||
                        reader.GetFieldType(ordinal) == typeof(System.Data.SqlTypes.SqlXml))
                    {
                        plans.Add(reader.GetValue(ordinal).ToString() ?? string.Empty);
                    }
        } while (await reader.NextResultAsync());
        await reader.DisposeAsync();
        await using var disable = connection.CreateCommand();
        disable.CommandText = "SET STATISTICS XML OFF;";
        await disable.ExecuteNonQueryAsync();
        return Assert.Single(plans);
    }

    /// <summary>
    /// Executes the exact production collection-delete DML under statistics XML in the live
    /// mutation transaction, then rolls its savepoint back. This keeps the populated selection
    /// table visible for an actual plan without stealing the handler's real delete.
    /// </summary>
    private static async ValueTask<string> ExplainCollectionDeleteAsync(
        DbConnection connection,
        DbTransaction transaction,
        RelationalPhysicalQueryCommand rendered,
        CancellationToken cancellationToken)
    {
        const string savepoint = "groundwork_collection_plan";
        await ExecutePlanControlAsync(connection, transaction, $"SAVE TRANSACTION {savepoint};", cancellationToken);
        try
        {
            await ExecutePlanControlAsync(connection, transaction, "SET STATISTICS XML ON;", cancellationToken);
            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = rendered.CommandText;
                foreach (var (name, value) in rendered.Parameters)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = $"@{name}";
                    parameter.Value = value ?? DBNull.Value;
                    command.Parameters.Add(parameter);
                }

                var plans = new List<string>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                do
                {
                    while (await reader.ReadAsync(cancellationToken))
                        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                            if (reader.GetName(ordinal).Contains("XML Showplan", StringComparison.OrdinalIgnoreCase) ||
                                reader.GetFieldType(ordinal) == typeof(System.Data.SqlTypes.SqlXml))
                                plans.Add(reader.GetValue(ordinal).ToString() ?? string.Empty);
                } while (await reader.NextResultAsync(cancellationToken));
                return Assert.Single(plans);
            }
            finally
            {
                await ExecutePlanControlAsync(connection, transaction, "SET STATISTICS XML OFF;", cancellationToken);
            }
        }
        finally
        {
            await ExecutePlanControlAsync(connection, transaction, $"ROLLBACK TRANSACTION {savepoint};", cancellationToken);
        }
    }

    private static async ValueTask ExecutePlanControlAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool UsesCollectionOwnerPrimaryKey(
        string? nativePlan,
        ExecutableCollectionElementStorageRoute collection)
    {
        if (string.IsNullOrWhiteSpace(nativePlan))
            return false;

        var document = XDocument.Parse(nativePlan);
        var ownerColumns = new[]
        {
            collection.DocumentKind.Column.Identifier,
            collection.StorageScope.Column.Identifier,
            collection.IdLookupKey.Column.Identifier
        };
        var exactComparison = collection.IdComparisonKey.Column.Identifier;
        var expectedIndex = SqlServerPhysicalName.Normalize($"PK_{collection.Storage.Name.Identifier}");
        var collectionScans = document.Descendants().Where(node =>
            IsShowPlanElement(node, "RelOp") &&
            node.Attribute("PhysicalOp")?.Value.EndsWith("Scan", StringComparison.Ordinal) == true &&
            node.Descendants().Any(candidate =>
                IsShowPlanElement(candidate, "Object") &&
                ShowPlanIdentifierEquals(candidate.Attribute("Table")?.Value, collection.Storage.Name.Identifier)));
        if (collectionScans.Any())
            return false;

        return document.Descendants().Any(node =>
        {
            if (!IsShowPlanElement(node, "RelOp") ||
                !string.Equals(node.Attribute("PhysicalOp")?.Value, "Index Seek", StringComparison.Ordinal))
                return false;

            var target = node.Descendants().FirstOrDefault(candidate =>
                IsShowPlanElement(candidate, "Object") &&
                ShowPlanIdentifierEquals(candidate.Attribute("Table")?.Value, collection.Storage.Name.Identifier));
            if (target is null ||
                !ShowPlanIdentifierEquals(target.Attribute("Index")?.Value, expectedIndex))
                return false;

            var seekText = node.ToString(SaveOptions.DisableFormatting);
            if (!ownerColumns.All(column =>
                    seekText.Contains($"Column=\"{column}\"", StringComparison.Ordinal)))
                return false;

            return node.Ancestors()
                .Where(ancestor =>
                    IsShowPlanElement(ancestor, "RelOp") &&
                    string.Equals(ancestor.Attribute("PhysicalOp")?.Value, "Nested Loops", StringComparison.Ordinal))
                .Any(ancestor =>
                {
                    var branches = ImmediateRelOpBranches(ancestor).ToArray();
                    var seekBranch = branches.SingleOrDefault(branch =>
                        branch.DescendantsAndSelf().Contains(node));
                    return seekBranch is not null && branches
                        .Where(branch => branch != seekBranch)
                        .SelectMany(branch => branch.DescendantsAndSelf())
                        .Any(descendant =>
                            IsShowPlanElement(descendant, "RelOp") &&
                            string.Equals(
                                descendant.Attribute("PhysicalOp")?.Value,
                                "RID Lookup",
                                StringComparison.Ordinal) &&
                            descendant.Descendants().Any(candidate =>
                                IsShowPlanElement(candidate, "Object") &&
                                ShowPlanIdentifierEquals(
                                    candidate.Attribute("Table")?.Value,
                                    collection.Storage.Name.Identifier)) &&
                            descendant.Descendants().Any(candidate =>
                                IsShowPlanElement(candidate, "ColumnReference") &&
                                ShowPlanIdentifierEquals(
                                    candidate.Attribute("Column")?.Value,
                                    exactComparison)));
                });
        });
    }

    private static IEnumerable<XElement> ImmediateRelOpBranches(XElement parent) =>
        parent.Descendants().Where(candidate =>
            IsShowPlanElement(candidate, "RelOp") &&
            candidate.Ancestors().FirstOrDefault(ancestor => IsShowPlanElement(ancestor, "RelOp")) == parent);

    private static bool IsShowPlanElement(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal);

    private static bool ShowPlanIdentifierEquals(string? actual, string expected) =>
        actual is not null &&
        string.Equals(actual.Trim('[', ']'), expected, StringComparison.Ordinal);

    [Fact]
    public void Collection_owner_primary_key_plan_recognition_rejects_non_owner_access()
    {
        var collection = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.PhysicalEntityTable,
                SqlServerGroundworkCapabilities.Provider,
                includePriority: true,
                normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
                includeCollection: true,
                mutationOptions: new(IncludeRangeDelete: true))
            .Target.Routes.Single().CollectionElementStorages.Single();
        var ownerColumns = $$"""
            <ColumnReference Column="{{collection.DocumentKind.Column.Identifier}}" />
            <ColumnReference Column="{{collection.StorageScope.Column.Identifier}}" />
            <ColumnReference Column="{{collection.IdLookupKey.Column.Identifier}}" />
            """;
        var comparison = $"<ColumnReference Column=\"{collection.IdComparisonKey.Column.Identifier}\" />";
        var expectedIndex = SqlServerPhysicalName.Normalize($"PK_{collection.Storage.Name.Identifier}");
        var fixtures = new[]
        {
            $$"""
              <ShowPlanXML><RelOp PhysicalOp="Index Seek"><IndexScan><Object Table="[{{collection.Storage.Name.Identifier}}]" Index="[wrong_index]" /><SeekPredicates>{{ownerColumns}}{{comparison}}</SeekPredicates></IndexScan></RelOp></ShowPlanXML>
              """,
            $$"""
              <ShowPlanXML><RelOp PhysicalOp="Nested Loops"><RelOp PhysicalOp="Index Seek"><IndexScan><Object Table="[{{collection.Storage.Name.Identifier}}]" Index="[{{SqlServerPhysicalName.Normalize($"PK_{collection.Storage.Name.Identifier}")}}]" /><SeekPredicates>{{ownerColumns}}</SeekPredicates></IndexScan></RelOp><RelOp PhysicalOp="Compute Scalar">{{comparison}}</RelOp></RelOp></ShowPlanXML>
              """,
            $$"""
              <ShowPlanXML><RelOp PhysicalOp="Nested Loops"><RelOp PhysicalOp="Index Seek"><IndexScan><Object Table="[{{collection.Storage.Name.Identifier}}]" Index="[{{SqlServerPhysicalName.Normalize($"PK_{collection.Storage.Name.Identifier}")}}]" /><SeekPredicates>{{ownerColumns}}{{comparison}}</SeekPredicates></IndexScan></RelOp><RelOp PhysicalOp="Table Scan"><TableScan><Object Table="[{{collection.Storage.Name.Identifier}}]" /></TableScan></RelOp></RelOp></ShowPlanXML>
              """,
            $$"""
              <ShowPlanXML><RelOp PhysicalOp="Nested Loops"><RelOp PhysicalOp="Index Seek"><IndexScan><Object Table="[decoy_{{collection.Storage.Name.Identifier}}_decoy]" Index="[{{expectedIndex}}]" /><SeekPredicates>{{ownerColumns}}</SeekPredicates></IndexScan></RelOp><RelOp PhysicalOp="RID Lookup"><IndexScan><Object Table="[{{collection.Storage.Name.Identifier}}]" /></IndexScan><Predicate>{{comparison}}</Predicate></RelOp></RelOp></ShowPlanXML>
              """,
            $$"""
              <ShowPlanXML><RelOp PhysicalOp="Nested Loops"><RelOp PhysicalOp="Index Seek"><IndexScan><Object Table="[{{collection.Storage.Name.Identifier}}]" Index="[decoy_{{expectedIndex}}_decoy]" /><SeekPredicates>{{ownerColumns}}</SeekPredicates></IndexScan></RelOp><RelOp PhysicalOp="RID Lookup"><IndexScan><Object Table="[{{collection.Storage.Name.Identifier}}]" /></IndexScan><Predicate>{{comparison}}</Predicate></RelOp></RelOp></ShowPlanXML>
              """,
            $$"""
              <ShowPlanXML><RelOp PhysicalOp="Table Delete"><RelOp PhysicalOp="Nested Loops"><RelOp PhysicalOp="Index Seek"><IndexScan><Object Table="[{{collection.Storage.Name.Identifier}}]" Index="[{{expectedIndex}}]" /><SeekPredicates>{{ownerColumns}}</SeekPredicates></IndexScan></RelOp><RelOp PhysicalOp="Compute Scalar" /></RelOp><RelOp PhysicalOp="RID Lookup"><IndexScan><Object Table="[{{collection.Storage.Name.Identifier}}]" /></IndexScan><Predicate>{{comparison}}</Predicate></RelOp></RelOp></ShowPlanXML>
              """
        };

        Assert.All(fixtures, fixture => Assert.False(UsesCollectionOwnerPrimaryKey(fixture, collection)));
    }

    private async Task SeedPlanNoiseAsync(ExecutableStorageRoute route)
    {
        var category = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "category");
        var table = category.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        var identity = category.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.Envelope.Identity
            : route.LinkedRelationship!.Identity;
        await SqlServerDocumentIdentityNoiseSeeder.SeedAsync(
            container.GetConnectionString(),
            identity,
            table,
            (category.Column.Identifier, "tools"),
            new Dictionary<string, object?> { [category.Column.Identifier] = "noise" });
    }

    private async Task CorruptPrimaryLookupAsync(ExecutableStorageRoute route, string lookupKey)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE {Q(route.PrimaryStorage.Name.Identifier)} SET " +
            $"{Q(route.Envelope.Identity.LookupKey.Identifier)} = @lookupKey;";
        command.Parameters.AddWithValue("@lookupKey", SqlServerDocumentIdentityEncoding.Lookup(lookupKey));
        await command.ExecuteNonQueryAsync();
    }

    private async Task CorruptLinkedIdentityAsync(
        ExecutableStorageRoute route,
        string retainedId,
        string comparisonKey)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE {Q(route.LinkedIndexStorage!.Name.Identifier)} SET " +
            $"{Q(route.LinkedRelationship!.DocumentId.Identifier)} = @retainedId, " +
            $"{Q(route.LinkedRelationship.Identity.ComparisonKey.Identifier)} = @comparisonKey;";
        command.Parameters.AddWithValue("@retainedId", retainedId);
        command.Parameters.AddWithValue("@comparisonKey", EncodeInjectedComparisonEvidence(comparisonKey));
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetLinkedIdentityAsync(
        ExecutableStorageRoute route,
        string lookupKey,
        string retainedId,
        string comparisonKey)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE {Q(route.LinkedIndexStorage!.Name.Identifier)} SET " +
            $"{Q(route.LinkedRelationship!.DocumentId.Identifier)} = @retainedId, " +
            $"{Q(route.LinkedRelationship.Identity.ComparisonKey.Identifier)} = @comparisonKey " +
            $"WHERE {Q(route.LinkedRelationship.Identity.LookupKey.Identifier)} = @lookupKey;";
        command.Parameters.AddWithValue("@retainedId", retainedId);
        command.Parameters.AddWithValue("@comparisonKey", EncodeInjectedComparisonEvidence(comparisonKey));
        command.Parameters.AddWithValue("@lookupKey", SqlServerDocumentIdentityEncoding.Lookup(lookupKey));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static byte[] EncodeInjectedComparisonEvidence(string value)
        => value.Length % 2 == 0 && value.All(Uri.IsHexDigit)
            ? SqlServerDocumentIdentityEncoding.Comparison(value)
            : Encoding.UTF8.GetBytes(value);

    private async Task<IReadOnlyList<int?>> ReadNullableInt32Async(
        string table,
        string column,
        string orderBy)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Q(column)} FROM {Q(table)} ORDER BY {Q(orderBy)};";
        var values = new List<int?>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.IsDBNull(0) ? null : reader.GetInt32(0));
        return values;
    }

    private async Task<RelationalIdentitySchemaEvidence> ReadIdentitySchemaAsync(
        ExecutableStorageRoute route,
        bool linked)
    {
        var table = linked
            ? route.LinkedIndexStorage!.Name.Identifier
            : route.PrimaryStorage.Name.Identifier;
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@table);";
            command.Parameters.AddWithValue("@table", table);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(0));
        }
        var primaryKey = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT c.name
                FROM sys.indexes i
                JOIN sys.index_columns ic
                  ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c
                  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID(@table) AND i.is_primary_key = 1
                ORDER BY ic.key_ordinal;
                """;
            command.Parameters.AddWithValue("@table", table);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                primaryKey.Add(reader.GetString(0));
        }
        return new RelationalIdentitySchemaEvidence(columns, primaryKey);
    }

    private async Task DropComparisonEvidenceAsync(ExecutableStorageRoute route, bool linked)
    {
        var table = linked
            ? route.LinkedIndexStorage!.Name.Identifier
            : route.PrimaryStorage.Name.Identifier;
        var comparison = linked
            ? route.LinkedRelationship!.Identity.ComparisonKey.Identifier
            : route.Envelope.Identity.ComparisonKey.Identifier;
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"ALTER TABLE {Q(table)} DROP COLUMN {Q(SqlServerPhysicalIdentity.HiddenColumn(comparison))}; " +
            $"ALTER TABLE {Q(table)} DROP COLUMN {Q(comparison)};";
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedMutationLedgerPlanNoiseAsync(string operationId)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            WITH source AS (
                SELECT TOP (1) *
                FROM groundwork_document_mutation_operations
                WHERE operation_id = @operationId
            ), numbers AS (
                SELECT TOP (10000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects a CROSS JOIN sys.all_objects b
            )
            INSERT INTO groundwork_document_mutation_operations (
                manifest_id, provider_name, completed_provider_version, storage_unit, storage_scope,
                operation_id, request_fingerprint, affected_count, completed_utc)
            SELECT s.manifest_id, s.provider_name, s.completed_provider_version, s.storage_unit, s.storage_scope,
                CONCAT(N'ledger-plan-noise-', n.n), s.request_fingerprint, s.affected_count, s.completed_utc
            FROM source s CROSS JOIN numbers n;
            """;
        seed.Parameters.AddWithValue("@operationId", operationId);
        Assert.Equal(10000, await seed.ExecuteNonQueryAsync());
    }

    private async Task TerminateSessionAsync(long sessionId)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"KILL {sessionId};";
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> ReadPrimaryKeyNameAsync(string table)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(@table) AND type = 'PK';";
        command.Parameters.AddWithValue("@table", table);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetFenceAsync(PhysicalSchemaTargetIdentity target, long fence)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE groundwork_physical_schema_locks SET fence = @fence WHERE manifest_id = @manifestId AND provider_name = @providerName;";
        command.Parameters.AddWithValue("@fence", fence);
        command.Parameters.AddWithValue("@manifestId", target.ManifestIdentity.Value);
        command.Parameters.AddWithValue("@providerName", target.ProviderName);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task TamperIdentityHashColumnAsync(string table, string retainedColumn, IdentityHashTamper tamper)
    {
        var hidden = SqlServerPhysicalIdentity.HiddenColumn(retainedColumn);
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using (var readKey = connection.CreateCommand())
        {
            readKey.CommandText = "SELECT name FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(@table) AND type = 'PK';";
            readKey.Parameters.AddWithValue("@table", table);
            var constraint = (string)(await readKey.ExecuteScalarAsync())!;
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"ALTER TABLE {Q(table)} DROP CONSTRAINT {Q(constraint)}; ALTER TABLE {Q(table)} DROP COLUMN {Q(hidden)};";
            await drop.ExecuteNonQueryAsync();
        }
        var definition = tamper switch
        {
            IdentityHashTamper.PlainBinary => $"{Q(hidden)} binary(32) NOT NULL",
            IdentityHashTamper.NonPersisted =>
                $"{Q(hidden)} AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), {Q(retainedColumn)})))",
            IdentityHashTamper.WrongExpression =>
                $"{Q(hidden)} AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), N'wrong'))) PERSISTED NOT NULL",
            _ => throw new ArgumentOutOfRangeException(nameof(tamper), tamper, null)
        };
        await using var add = connection.CreateCommand();
        add.CommandText = $"ALTER TABLE {Q(table)} ADD {definition};";
        await add.ExecuteNonQueryAsync();
    }

    private static async Task TamperInfrastructureAsync(string connectionString, InfrastructureTamper tamper)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        if (tamper == InfrastructureTamper.WrongObjectKind)
        {
            await ExecuteAsync(connection, """
                DROP TABLE groundwork_physical_schema_locks;
                EXEC(N'CREATE VIEW groundwork_physical_schema_locks AS
                    SELECT CAST(NULL AS nvarchar(max)) AS manifest_id,
                           CAST(NULL AS nvarchar(max)) AS provider_name WHERE 1 = 0;');
                """);
            return;
        }

        var sql = tamper switch
        {
            InfrastructureTamper.ExtraOperationsColumn =>
                "ALTER TABLE groundwork_physical_schema_operations ADD unexpected int NULL;",
            InfrastructureTamper.MissingStateColumn =>
                "ALTER TABLE groundwork_physical_schema_state DROP COLUMN applied_state_json;",
            InfrastructureTamper.NullableLockOwner =>
                "ALTER TABLE groundwork_physical_schema_locks ALTER COLUMN owner_id nvarchar(32) COLLATE Latin1_General_100_BIN2 NULL;",
            InfrastructureTamper.WrongLockOwnerType =>
                "ALTER TABLE groundwork_physical_schema_locks ALTER COLUMN owner_id nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL;",
            InfrastructureTamper.WrongStateCollation =>
                "ALTER TABLE groundwork_physical_schema_state ALTER COLUMN target_fingerprint nvarchar(128) COLLATE Latin1_General_100_CI_AS NOT NULL;",
            InfrastructureTamper.MissingStatePrimaryKey =>
                $"ALTER TABLE groundwork_physical_schema_state DROP CONSTRAINT {Q(await PrimaryKeyAsync(connection, "groundwork_physical_schema_state"))};",
            InfrastructureTamper.ReorderedOperationsPrimaryKey =>
                $"ALTER TABLE groundwork_physical_schema_operations DROP CONSTRAINT {Q(await PrimaryKeyAsync(connection, "groundwork_physical_schema_operations"))}; " +
                "ALTER TABLE groundwork_physical_schema_operations ADD CONSTRAINT PK_tampered_operations " +
                "PRIMARY KEY NONCLUSTERED (provider_key, manifest_key, operation_key);",
            InfrastructureTamper.PlainHash or InfrastructureTamper.NonPersistedHash or InfrastructureTamper.WrongHashExpression =>
                await HashTamperSqlAsync(connection, tamper),
            _ => throw new ArgumentOutOfRangeException(nameof(tamper), tamper, null)
        };
        await ExecuteAsync(connection, sql);

        static async Task<string> PrimaryKeyAsync(SqlConnection connection, string table)
        {
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT name FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(@table) AND type = 'PK';";
            read.Parameters.AddWithValue("@table", table);
            return (string)(await read.ExecuteScalarAsync())!;
        }

        static async Task<string> HashTamperSqlAsync(SqlConnection connection, InfrastructureTamper tamper)
        {
            var constraint = await PrimaryKeyAsync(connection, "groundwork_physical_schema_locks");
            var definition = tamper switch
            {
                InfrastructureTamper.PlainHash =>
                    "manifest_key binary(32) NOT NULL CONSTRAINT DF_tampered_manifest_key DEFAULT CONVERT(binary(32), 0x00)",
                InfrastructureTamper.NonPersistedHash =>
                    "manifest_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), manifest_id)))",
                InfrastructureTamper.WrongHashExpression =>
                    "manifest_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), N'wrong'))) PERSISTED NOT NULL",
                _ => throw new ArgumentOutOfRangeException(nameof(tamper), tamper, null)
            };
            var reapplyPrimaryKey = tamper == InfrastructureTamper.NonPersistedHash
                ? string.Empty
                : $"ALTER TABLE groundwork_physical_schema_locks ADD CONSTRAINT {Q(constraint)} " +
                  "PRIMARY KEY NONCLUSTERED (manifest_key, provider_key);";
            return $"ALTER TABLE groundwork_physical_schema_locks DROP CONSTRAINT {Q(constraint)}; " +
                   "ALTER TABLE groundwork_physical_schema_locks DROP COLUMN manifest_key; " +
                   $"ALTER TABLE groundwork_physical_schema_locks ADD {definition}; " +
                   reapplyPrimaryKey;
        }

        static async Task ExecuteAsync(SqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> CountFenceAsync(string connectionString, PhysicalSchemaTargetIdentity target)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(*) FROM groundwork_physical_schema_locks WHERE manifest_id = @manifestId AND provider_name = @providerName;";
        command.Parameters.AddWithValue("@manifestId", target.ManifestIdentity.Value);
        command.Parameters.AddWithValue("@providerName", target.ProviderName);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private Task<long> CountOperationEvidenceAsync(string operationId, string fingerprint) =>
        CountAsync(
            "SELECT COUNT_BIG(*) FROM groundwork_physical_schema_operations WHERE operation_id = @first AND operation_fingerprint = @second;",
            operationId,
            fingerprint);

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN OBJECT_ID(@table, N'U') IS NULL THEN 0 ELSE 1 END;";
        command.Parameters.AddWithValue("@table", table);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private Task<long> CountAppliedStateAsync(string manifestId, string providerName) =>
        CountAsync(
            "SELECT COUNT_BIG(*) FROM groundwork_physical_schema_state WHERE manifest_id = @first AND provider_name = @second;",
            manifestId,
            providerName);

    private Task<long> CountRowsAsync(string table) =>
        CountScalarAsync($"SELECT COUNT_BIG(*) FROM {Q(table)};");

    private Task<long> CountProjectedValuesAsync(string table, string column) =>
        CountScalarAsync($"SELECT COUNT_BIG(*) FROM {Q(table)} WHERE {Q(column)} IS NOT NULL;");

    private async Task<long> CountAsync(string sql, string first, string second)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@first", first);
        command.Parameters.AddWithValue("@second", second);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<long> CountScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private RelationalLockContentionProbe LockContention() => new(
        ReadSessionIdAsync,
        WaitUntilBlockedAsync);

    private RelationalMutationServerHarness<SqlServerPhysicalDocumentStore> MutationHarness() => new(
        SqlServerGroundworkCapabilities.Provider,
        "sqlserver",
        SqlServerGroundworkCapabilities.PhysicalNames,
        () => new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()),
        (manifest, routes, access) => new SqlServerPhysicalDocumentStore(
            container.GetConnectionString(), manifest, routes, access),
        SqlServerPhysicalMutationRuntime.Create,
        SqlServerPhysicalQueryRuntime.Create,
        () => new SqlConnection(container.GetConnectionString()),
        () => new SqlServerPhysicalDocumentDialect(),
        LockContention());

    private static async ValueTask<int> ReadSessionIdAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT @@SPID;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task WaitUntilBlockedAsync(
        int blockedSessionId,
        int blockerSessionId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM sys.dm_exec_requests
                WHERE session_id = @blocked AND blocking_session_id = @blocker
            ) THEN 1 ELSE 0 END;
            """;
        command.Parameters.AddWithValue("@blocked", blockedSessionId);
        command.Parameters.AddWithValue("@blocker", blockerSessionId);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken)))
                return;
            await Task.Delay(20, cancellationToken);
        }
        throw new TimeoutException(
            $"SQL Server session {blockedSessionId} was not observed waiting on session {blockerSessionId}.");
    }

    private static string Q(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private enum IdentityHashTamper
    {
        PlainBinary,
        NonPersisted,
        WrongExpression
    }

    public enum InfrastructureTamper
    {
        WrongObjectKind,
        ExtraOperationsColumn,
        MissingStateColumn,
        NullableLockOwner,
        WrongLockOwnerType,
        WrongStateCollation,
        MissingStatePrimaryKey,
        ReorderedOperationsPrimaryKey,
        PlainHash,
        NonPersistedHash,
        WrongHashExpression
    }

    public enum CollectionElementDrift
    {
        WrongType,
        WrongCollation,
        WrongDefault,
        WrongPrimaryKeyOrder
    }

    private sealed class InjectedCollectionWriteException : Exception
    {
    }
}
