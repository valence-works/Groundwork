using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.PostgreSql;
using Groundwork.PostgreSql.Documents;
using Groundwork.PostgreSql.PhysicalStorage;
using Groundwork.Relational.Documents;
using Groundwork.Relational.PhysicalStorage;
using Groundwork.TestInfrastructure;
using Npgsql;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Testcontainers.PostgreSql;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class PostgreSqlPhysicalStorageContainer : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder(Groundwork.TestInfrastructure.TestContainerImages.PostgreSql)
        .WithDatabase("groundwork")
        .WithUsername("groundwork")
        .WithPassword("groundwork")
        .Build();

    public Task InitializeAsync() => Container.StartAsync();
    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

public sealed partial class PostgreSqlRelationalPhysicalStorageConformanceTests(
    PostgreSqlPhysicalStorageContainer fixture)
    : RelationalServerPhysicalIdentityConformance<PostgreSqlPhysicalDocumentStore>, IClassFixture<PostgreSqlPhysicalStorageContainer>
{
    private readonly PostgreSqlContainer container = fixture.Container;

    [Fact]
    public async Task Failed_collection_schema_transition_preserves_old_writer_admission()
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames,
            includeCollection: true);
        var connectionString = container.GetConnectionString();

        await RelationalCollectionSchemaTransitionAssertions.FailedTransitionPreservesOldWriterAdmissionAsync(
            initial,
            additive,
            (manifest, routes) => new PostgreSqlPhysicalDocumentStore(
                connectionString,
                manifest,
                routes,
                DocumentStoreAccess.Global),
            hook => new PostgreSqlPhysicalSchemaExecutor(connectionString, hook, null));
    }

    [Theory]
    [InlineData(128, false)]
    [InlineData(129, true)]
    public async Task Additive_linked_string_projection_length_is_validated_before_postgresql_backfill(
        int valueLength,
        bool rejects)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.SharedDocuments,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.SharedDocuments,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            priorityType: PortablePhysicalType.String,
            priorityLength: 128,
            instance: instance,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        var documents = new PostgreSqlPhysicalDocumentStore(
            container.GetConnectionString(), initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global);
        var value = new string('a', valueLength);
        await documents.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "preexisting", "1", $"{{\"category\":\"tools\",\"priority\":\"{value}\"}}", 0));

        if (!rejects)
        {
            await PhysicalSchemaApplication.ApplyAsync(additive.Target, new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
            var route = additive.Target.Routes.Single();
            var evolved = new PostgreSqlPhysicalDocumentStore(
                container.GetConnectionString(), additive.Manifest, additive.Target.Routes, DocumentStoreAccess.Global);
            var result = await PostgreSqlPhysicalQueryRuntime.Create(evolved, additive.Manifest, route, additive.Target.Provider)
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
            PhysicalSchemaApplication.ApplyAsync(additive.Target, new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString())));
        Assert.Equal("GW-PHYSICAL-037", exception.Diagnostic.Code);
        Assert.Contains(value, (await documents.LoadAsync("configurationDocument", "preexisting"))!.ContentJson);
        var inspection = await new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString())
            .InspectHistoryAsync(additive.Target, CancellationToken.None);
        Assert.Equal(initial.Target.Fingerprint, inspection.History.AppliedState?.TargetFingerprint);
        Assert.NotEqual(additive.Target.Fingerprint, inspection.History.AppliedState?.TargetFingerprint);
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
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: false,
            instance: $"collection_{Guid.NewGuid():N}"[..19],
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames,
            includeCollection: true);
        var storage = Assert.Single(Assert.Single(model.Target.Routes).CollectionElementStorages);
        var executor = new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString());

        var applied = await PhysicalSchemaApplication.ApplyAsync(model.Target, executor);
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, applied.Outcome);
        Assert.Contains(applied.AppliedState!.Snapshot.Routes.Single().ResolvedNames, name =>
            name.Identifier == storage.MembershipKey.Name.Identifier);
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges,
            (await PhysicalSchemaApplication.ApplyAsync(
                model.Target,
                new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()))).Outcome);

        await using (var connection = new NpgsqlConnection(container.GetConnectionString()))
        {
            await connection.OpenAsync();
            var table = QuoteIdentifier(storage.Storage.Name.Identifier);
            var value = QuoteIdentifier(storage.Value.Column.Identifier);
            var key = string.Join(", ", storage.OwnerOrdinalKey.Columns.Reverse().Select(column => QuoteIdentifier(column.Column.Identifier)));
            var sql = drift switch
            {
                CollectionElementDrift.WrongType =>
                    $"ALTER TABLE {table} ALTER COLUMN {value} TYPE integer USING 0;",
                CollectionElementDrift.WrongCollation =>
                    $"ALTER TABLE {table} ALTER COLUMN {value} TYPE character varying(128) COLLATE \"POSIX\" USING {value};",
                CollectionElementDrift.WrongDefault =>
                    $"ALTER TABLE {table} ALTER COLUMN {value} SET DEFAULT 'unexpected';",
                CollectionElementDrift.WrongPrimaryKeyOrder =>
                    $"ALTER TABLE {table} DROP CONSTRAINT {QuoteIdentifier(await PrimaryKeyAsync(connection, storage.Storage.Name.Identifier))}; " +
                    $"ALTER TABLE {table} ADD PRIMARY KEY ({key});",
                _ => throw new ArgumentOutOfRangeException(nameof(drift), drift, null)
            };
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString())));
        Assert.Contains(storage.Storage.Name.Identifier, exception.Message, StringComparison.Ordinal);

        static async Task<string> PrimaryKeyAsync(NpgsqlConnection connection, string table)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT conname FROM pg_catalog.pg_constraint WHERE conrelid = @table::regclass AND contype = 'p';";
            command.Parameters.AddWithValue("table", table);
            return (string)(await command.ExecuteScalarAsync())!;
        }
    }

    [Fact]
    public void PrimaryInsertConflictTargetsOnlyTheCompiledIdentityKey()
    {
        var sql = new PostgreSqlPhysicalDocumentDialect().InsertPrimaryIfAbsent(
            "documents",
            ["kind", "scope", "lookup"],
            ["@kind", "@scope", "@lookup"],
            ["kind", "scope", "lookup"],
            []);

        Assert.Contains(
            "ON CONFLICT (\"kind\", \"scope\", \"lookup\") DO NOTHING",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public Task Bounded_assignment_updates_every_route_selected_document_and_replays_the_exact_count() =>
        RelationalBoundedMutationServerAssertions.AssignmentUpdatesEveryRouteSelectedDocumentAndReplaysExactCountAsync(MutationHarness());

    [Fact]
    public Task Concurrent_bounded_assignment_retry_completes_once_and_replays_the_exact_count() =>
        RelationalBoundedMutationServerAssertions.ConcurrentAssignmentRetryReplaysExactResultAsync(MutationHarness());

    [Fact]
    public Task Concurrent_distinct_assignments_retain_the_exact_matched_count() =>
        RelationalBoundedMutationServerAssertions.ConcurrentDistinctAssignmentsRetainMatchedCountsAsync(MutationHarness());

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
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            instance: Guid.NewGuid().ToString("N")[..8],
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames,
            includeCollection: true,
            mutationOptions: new(IncludeRangeDelete: true));
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var collection = Assert.Single(route.CollectionElementStorages);
        var store = new PostgreSqlPhysicalDocumentStore(
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
                PostgreSqlGroundworkCapabilities.Provider.Name,
                "postgresql"),
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
            "postgresql-collection-native-plan",
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
    public async Task Sort_only_index_field_residual_filters_before_cursor_limit_and_binds_continuation()
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var manifest = SortOnlyResidualPredicateConformance.CreateManifest(instance);
        var target = SortOnlyResidualPredicateConformance.CreateTarget(
            manifest,
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            instance);
        await PhysicalSchemaApplication.ApplyAsync(
            target,
            new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = target.Routes.Single();
        var store = new PostgreSqlPhysicalDocumentStore(
            container.GetConnectionString(),
            manifest,
            target.Routes,
            DocumentStoreAccess.Global);
        var runtime = PostgreSqlPhysicalQueryRuntime.Create(
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
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            () => new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new PostgreSqlPhysicalDocumentStore(
                container.GetConnectionString(),
                manifest,
                routes,
                DocumentStoreAccess.Global),
            (store, manifest, route) => PostgreSqlPhysicalQueryRuntime.Create(
                Assert.IsType<PostgreSqlPhysicalDocumentStore>(store),
                manifest,
                route,
                PostgreSqlGroundworkCapabilities.Provider));

    [Fact]
    public async Task Bounded_mutation_ledger_supports_unbounded_operation_identity()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames,
            mutationOptions: new(IncludeCategoryTransition: true, IncludeRangeDelete: true));
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var store = new PostgreSqlPhysicalDocumentStore(
            container.GetConnectionString(), model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "long-operation", "1", "{\"category\":\"pending\",\"priority\":1}"))).Status);
        var operationId = string.Concat(Enumerable.Range(0, 100).Select(index =>
            Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(index)))));
        Assert.Equal(6400, Encoding.UTF8.GetByteCount(operationId));
        var request = new DocumentMutation("configurationDocument", "revoke-pending", operationId);
        var mutations = PostgreSqlPhysicalMutationRuntime.Create(store, model.Manifest, route, model.Target.Provider);

        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await mutations.ExecuteAsync(request));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Replayed, 1),
            await mutations.ExecuteAsync(request));
        await Assert.ThrowsAsync<BoundedMutationOperationConflictException>(() => mutations.ExecuteAsync(
            new DocumentMutation(
                "configurationDocument",
                "prune-by-category-cutoff",
                operationId,
                [
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "pending")),
                    DocumentQueryClause.Of(DocumentQueryComparison.LessThan("priority", "10"))
                ])));
    }

    [Fact]
    public Task Unpublished_backfill_acknowledgement_loss_replays_interleaved_writes() =>
        RelationalPhysicalServerAssertions.UnpublishedBackfillAcknowledgementLossReplaysInterleavedWritesAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            () => new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new PostgreSqlPhysicalDocumentStore(
                container.GetConnectionString(),
                manifest,
                routes,
                DocumentStoreAccess.Global),
            "postgresql");

    [Fact]
    public async Task Public_query_explain_uses_json_plan_and_leaves_normal_execution_usable()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: false,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames,
            categoryPaging: QueryPagingSupport.Cursor);
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var store = new PostgreSqlPhysicalDocumentStore(
            container.GetConnectionString(), model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "explain-target", "1", "{\"category\":\"pending\"}"))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "explain-target-2", "1", "{\"category\":\"pending\"}"))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "explain-noise", "1", "{\"category\":\"tools\"}"))).Status);
        await SeedPlanNoiseAsync(route);
        await AnalyzeRouteAsync(route);
        var runtime = PostgreSqlPhysicalQueryRuntime.Create(store, model.Manifest, route, model.Target.Provider);
        var explainer = Assert.IsAssignableFrom<IPhysicalDocumentQueryExplainer>(runtime);
        var query = new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "pending"))],
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
            Assert.Equal("postgresql-json", command.NativePlanFormat);
            Assert.Contains(explanation.Plan.IndexName!.Identifier, command.NativePlan, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Node Type\": \"Seq Scan\"", command.NativePlan, StringComparison.Ordinal);
        });
        var page = explanation.Commands.Single(command =>
            command.Identity == PhysicalDocumentQueryCommandIdentities.Page);
        Assert.Contains("\"Node Type\": \"Limit\"", page.NativePlan, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Node Type\": \"Sort\"", page.NativePlan, StringComparison.Ordinal);
        Assert.Single(result.Documents);
        Assert.Null(result.NextContinuation);
    }

    [Fact]
    public async Task Linked_page_limits_one_thousand_matches_before_hydrating_one_hundred_thousand_primary_rows()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.SharedDocuments,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: false,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var store = new PostgreSqlPhysicalDocumentStore(
            container.GetConnectionString(), model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "bounded-hydration-source", "1", "{\"category\":\"pending\"}"))).Status);
        await SeedBoundedHydrationEvidenceAsync(route);
        await AnalyzeRouteAsync(route);
        var runtime = PostgreSqlPhysicalQueryRuntime.Create(store, model.Manifest, route, model.Target.Provider);
        var query = new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "pending"))],
            take: 16);

        var result = await runtime.QueryAsync(query);
        var explanation = await Assert.IsAssignableFrom<IPhysicalDocumentQueryExplainer>(runtime).ExplainAsync(query);
        var page = explanation.Commands.Single(command =>
            command.Identity == PhysicalDocumentQueryCommandIdentities.Page);

        Assert.Equal(1_000, result.TotalCount);
        Assert.Equal(16, result.Documents.Count);
        Assert.Contains("\"Node Type\": \"Limit\"", page.NativePlan, StringComparison.Ordinal);
        Assert.Contains(route.Indexes.Single(index => index.Identity == "by-category").Name.Identifier, page.NativePlan, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Node Type\": \"Hash Join\"", page.NativePlan, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Node Type\": \"Seq Scan\"", page.NativePlan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_distinct_targets_can_bootstrap_a_clean_schema()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schema = $"groundwork_bootstrap_{suffix}";
        await ExecuteAdminAsync($"CREATE SCHEMA {QuoteIdentifier(schema)};");
        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            SearchPath = schema
        }.ConnectionString;
        try
        {
            var first = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.PhysicalEntityTable,
                PostgreSqlGroundworkCapabilities.Provider,
                includePriority: true,
                instance: $"first-{suffix}",
                normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
            var second = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.DedicatedDocumentTable,
                PostgreSqlGroundworkCapabilities.Provider,
                includePriority: true,
                instance: $"second-{suffix}",
                normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
            var results = await Task.WhenAll(
                PhysicalSchemaApplication.ApplyAsync(first.Target, new PostgreSqlPhysicalSchemaExecutor(connectionString)),
                PhysicalSchemaApplication.ApplyAsync(second.Target, new PostgreSqlPhysicalSchemaExecutor(connectionString)));
            Assert.All(results, result => Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync($"DROP SCHEMA {QuoteIdentifier(schema)} CASCADE;");
        }
    }

    [Theory]
    [InlineData(InfrastructureTamper.WrongObjectKind)]
    [InlineData(InfrastructureTamper.ExtraOperationsColumn)]
    [InlineData(InfrastructureTamper.MissingStateColumn)]
    [InlineData(InfrastructureTamper.NullableLockOwner)]
    [InlineData(InfrastructureTamper.WrongLockOwnerType)]
    [InlineData(InfrastructureTamper.WrongStateCollation)]
    [InlineData(InfrastructureTamper.SameNameCShadowCollation)]
    [InlineData(InfrastructureTamper.MissingStatePrimaryKey)]
    [InlineData(InfrastructureTamper.ReorderedOperationsPrimaryKey)]
    [InlineData(InfrastructureTamper.LegacyMutationLedgerPrimaryKey)]
    [InlineData(InfrastructureTamper.WrongMutationLedgerHashExpression)]
    [InlineData(InfrastructureTamper.WrongMutationHashFunction)]
    public async Task Restart_rejects_malformed_infrastructure_before_target_fence_mutation(InfrastructureTamper tamper)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schema = $"groundwork_infrastructure_{suffix}";
        await ExecuteAdminAsync($"CREATE SCHEMA {QuoteIdentifier(schema)};");
        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            SearchPath = schema
        }.ConnectionString;
        try
        {
            var initial = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.PhysicalEntityTable,
                PostgreSqlGroundworkCapabilities.Provider,
                includePriority: true,
                instance: $"initial-{suffix}",
                normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
            await PhysicalSchemaApplication.ApplyAsync(initial.Target, new PostgreSqlPhysicalSchemaExecutor(connectionString));
            await TamperInfrastructureAsync(connectionString, schema, tamper);

            var rejected = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.PhysicalEntityTable,
                PostgreSqlGroundworkCapabilities.Provider,
                includePriority: true,
                instance: $"rejected-{suffix}",
                normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using var unused = await new PostgreSqlPhysicalSchemaExecutor(connectionString)
                    .AcquireApplicationLockAsync(rejected.Target.Identity, CancellationToken.None);
            });
            Assert.Contains("Physical-schema infrastructure", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, await CountFenceAsync(connectionString, rejected.Target.Identity));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync($"DROP SCHEMA {QuoteIdentifier(schema)} CASCADE;");
        }
    }

    [Fact]
    public Task Terminated_lock_backend_disposal_is_immediate_and_idempotent() =>
        RelationalPhysicalServerAssertions.TerminatedApplicationLockDisposalIsIdempotentAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            () => new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()),
            PostgreSqlPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync);

    [Fact]
    public Task Failed_release_alone_does_not_throw_because_closing_the_backend_frees_the_lock() =>
        RelationalPhysicalServerAssertions.FailedReleaseAloneDisposesQuietlyAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            LockFailureHarness);

    [Fact]
    public Task Failed_release_and_failed_backend_close_report_the_possible_leak() =>
        RelationalPhysicalServerAssertions.FailedReleaseAndSessionCloseReportThePossibleLeakAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            LockFailureHarness);

    [Fact]
    public Task Skipped_release_and_failed_backend_close_report_the_possible_leak() =>
        RelationalPhysicalServerAssertions.SkippedReleaseAndFailedSessionCloseReportThePossibleLeakAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            LockFailureHarness);

    [Fact]
    public Task Failed_backend_close_alone_does_not_throw_because_the_lock_was_released() =>
        RelationalPhysicalServerAssertions.FailedSessionCloseAloneDisposesQuietlyAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            LockFailureHarness);

    [Fact]
    public Task Disposal_report_carries_the_heartbeat_probe_failure() =>
        RelationalPhysicalServerAssertions.DisposalReportCarriesTheHeartbeatProbeFailureAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            LockFailureHarness);

    [Fact]
    public Task Throwing_ownership_subscriber_cannot_break_lock_teardown() =>
        RelationalPhysicalServerAssertions.ThrowingOwnershipSubscriberCannotBreakTeardownAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            LockFailureHarness);

    private RelationalLockFailureHarness LockFailureHarness()
    {
        var switches = new RelationalLockFailureSwitches();
        var dialect = new FailureInjectingPostgreSqlDialect(switches);
        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Pooling = false
        }.ConnectionString;
        return new RelationalLockFailureHarness(
            switches,
            () => new RelationalServerPhysicalSchemaExecutor(
                () => new FaultInjectingConnection(new NpgsqlConnection(connectionString), switches),
                dialect));
    }

    private sealed class FailureInjectingPostgreSqlDialect(RelationalLockFailureSwitches switches)
        : PostgreSqlPhysicalSchemaDialect
    {
        public override Task ReleaseApplicationLockAsync(
            DbConnection connection,
            string resource,
            CancellationToken cancellationToken) =>
            switches.FailReleases
                ? throw new InvalidOperationException(RelationalLockFailureSwitches.ReleaseFailureMessage)
                : base.ReleaseApplicationLockAsync(connection, resource, cancellationToken);

        public override Task<bool> VerifyApplicationLockAsync(
            DbConnection connection,
            string resource,
            CancellationToken cancellationToken) =>
            switches.FailVerification
                ? throw new InvalidOperationException(RelationalLockFailureSwitches.VerificationFailureMessage)
                : base.VerifyApplicationLockAsync(connection, resource, cancellationToken);
    }

    [Fact]
    public async Task Exhausted_fence_fails_without_poisoning_the_session_lock()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        var executor = new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString());
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
    public Task Terminated_lock_backend_cannot_publish_operation_evidence() =>
        RelationalPhysicalServerAssertions.LostOperationLockCannotPublishEvidenceAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            (beforeOperation, beforeState) => new PostgreSqlPhysicalSchemaExecutor(
                container.GetConnectionString(), beforeOperation, beforeState),
            PostgreSqlPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync,
            CountOperationEvidenceAsync,
            TableExistsAsync);

    [Fact]
    public Task Terminated_lock_backend_cannot_publish_applied_state() =>
        RelationalPhysicalServerAssertions.LostStateLockCannotPublishAppliedStateAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            (beforeOperation, beforeState) => new PostgreSqlPhysicalSchemaExecutor(
                container.GetConnectionString(), beforeOperation, beforeState),
            PostgreSqlPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync,
            CountAppliedStateAsync);

    [Fact]
    public Task Non_provider_failure_after_real_lock_loss_marks_ownership_lost_and_uses_stable_error() =>
        RelationalPhysicalServerAssertions.NonProviderFailureAfterRealLockLossUsesStableErrorAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            beforeState => new PostgreSqlPhysicalSchemaExecutor(
                container.GetConnectionString(),
                null,
                beforeState),
            PostgreSqlPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync,
            CountAppliedStateAsync);

    [Fact]
    public Task Ordinary_invalid_operation_preserves_owned_lock_and_original_error() =>
        RelationalPhysicalServerAssertions.OrdinaryInvalidOperationPreservesOwnedLockAndOriginalErrorAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            beforeState => new PostgreSqlPhysicalSchemaExecutor(
                container.GetConnectionString(),
                null,
                beforeState));

    [Fact]
    public Task Transient_heartbeat_verification_failures_preserve_owned_lock() =>
        RelationalPhysicalServerAssertions.TransientHeartbeatVerificationFailuresPreserveOwnershipAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            CreateHeartbeatInterceptingExecutor);

    [Fact]
    public Task Persistent_heartbeat_verification_failure_marks_lock_ownership_lost() =>
        RelationalPhysicalServerAssertions.PersistentHeartbeatVerificationFailureMarksOwnershipLostAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            CreateHeartbeatInterceptingExecutor);

    private IPhysicalSchemaExecutor CreateHeartbeatInterceptingExecutor(
        Func<CancellationToken, Task> beforeVerification) =>
        new RelationalServerPhysicalSchemaExecutor(
            () => new NpgsqlConnection(
                new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Pooling = false }.ConnectionString),
            new VerificationInterceptingPostgreSqlDialect(beforeVerification));

    private sealed class VerificationInterceptingPostgreSqlDialect(
        Func<CancellationToken, Task> beforeVerification) : PostgreSqlPhysicalSchemaDialect
    {
        public override async Task<bool> VerifyApplicationLockAsync(
            DbConnection connection,
            string resource,
            CancellationToken cancellationToken)
        {
            await beforeVerification(cancellationToken);
            return await base.VerifyApplicationLockAsync(connection, resource, cancellationToken);
        }
    }

    [Fact]
    public Task Terminated_lock_backend_cannot_commit_backfill_or_operation_evidence() =>
        RelationalPhysicalServerAssertions.LostBackfillLockCannotCommitDataOrEvidenceAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            (beforeOperation, beforeState) => new PostgreSqlPhysicalSchemaExecutor(
                container.GetConnectionString(), beforeOperation, beforeState),
            (manifest, routes) => new PostgreSqlPhysicalDocumentStore(
                container.GetConnectionString(), manifest, routes, DocumentStoreAccess.Global),
            PostgreSqlPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync,
            CountOperationEvidenceAsync,
            CountProjectedValuesAsync);

    [Fact]
    public Task DecimalLiveAndBackfillValuesUseTheSameNativeSemantics() =>
        RelationalPhysicalServerAssertions.TypedProjectionLiveAndBackfillValuesRemainEquivalentAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            () => new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new PostgreSqlPhysicalDocumentStore(
                container.GetConnectionString(), manifest, routes, DocumentStoreAccess.Global),
            "postgresql",
            PortablePhysicalType.Decimal,
            "12.3400",
            "12.34",
            "12.34",
            precision: 18,
            scale: 4);

    [Fact]
    public Task DateTimeLiveAndBackfillValuesPreserveEquivalentUtcTicks() =>
        RelationalPhysicalServerAssertions.TypedProjectionLiveAndBackfillValuesRemainEquivalentAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            () => new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new PostgreSqlPhysicalDocumentStore(
                container.GetConnectionString(), manifest, routes, DocumentStoreAccess.Global),
            "postgresql",
            PortablePhysicalType.DateTime,
            "\"2026-01-01T00:00:00.0000001+01:00\"",
            "\"2025-12-31T23:00:00.0000001Z\"",
            "2025-12-31T23:00:00.0000001Z");

    [Fact]
    public async Task ExistingIncompatibleSchemaIsRejectedInsteadOfAcceptedByName()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        await using (var connection = new NpgsqlConnection(container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE {QuoteIdentifier(model.Target.Routes.Single().PrimaryStorage.Name.Identifier)} (\"wrong\" integer NOT NULL);";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString())));
    }

    [Fact]
    public Task WideningANullExcludingIndexRebuildsItWithoutItsFilter() =>
        RelationalPhysicalServerAssertions.WideningANullExcludingIndexRebuildsItWithoutItsFilterAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            () => new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()),
            ReadIndexFilterAsync);

    private async Task<string?> ReadIndexFilterAsync(string table, string index)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pg_get_expr(i.indpred, i.indrelid) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid " +
            "JOIN pg_class t ON t.oid = i.indrelid WHERE c.relname = @index AND t.relname = @table;";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@index", index);
        return await command.ExecuteScalarAsync() as string;
    }

    [Fact]
    public Task NullableUniqueProjectionUsesPortableNullDistinctSemantics() =>
        RelationalPhysicalServerAssertions.NullableUniqueProjectionUsesPortableNullDistinctSemanticsAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            () => new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new PostgreSqlPhysicalDocumentStore(
                container.GetConnectionString(), manifest, routes, DocumentStoreAccess.Global));

    [Fact]
    public async Task Icu_database_preserves_exact_identity_linked_backfill_and_catalog_C_restart_validation()
    {
        var database = $"groundwork_icu_{Guid.NewGuid():N}";
        var connectionString = await CreateIcuDatabaseAsync(database);
        try
        {
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var collation = connection.CreateCommand();
                collation.CommandText = "CREATE COLLATION gw_nondeterministic (provider = icu, locale = 'und-u-ks-level1', deterministic = false);";
                await collation.ExecuteNonQueryAsync();
            }
            var instance = Guid.NewGuid().ToString("N")[..8];
            var initial = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.SharedDocuments,
                PostgreSqlGroundworkCapabilities.Provider,
                includePriority: false,
                instance: instance,
                normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
            var additive = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.SharedDocuments,
                PostgreSqlGroundworkCapabilities.Provider,
                includePriority: true,
                instance: instance,
                normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
            await PhysicalSchemaApplication.ApplyAsync(initial.Target, new PostgreSqlPhysicalSchemaExecutor(connectionString));
            var store = new PostgreSqlPhysicalDocumentStore(
                connectionString, initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global);
            foreach (var id in new[] { "Case", "case", "café", "cafe" })
            {
                Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
                    "configurationDocument", id, "1", "{\"category\":\"tools\",\"priority\":7}", 0))).Status);
            }

            await PhysicalSchemaApplication.ApplyAsync(additive.Target, new PostgreSqlPhysicalSchemaExecutor(connectionString));
            var additiveStore = new PostgreSqlPhysicalDocumentStore(
                connectionString, additive.Manifest, additive.Target.Routes, DocumentStoreAccess.Global);
            foreach (var id in new[] { "Case", "case", "café", "cafe" })
                Assert.NotNull(await additiveStore.LoadAsync("configurationDocument", id));
            var route = additive.Target.Routes.Single();
            var queries = PostgreSqlPhysicalQueryRuntime.Create(
                additiveStore, additive.Manifest, route, additive.Target.Provider);
            Assert.Equal(4, await queries.CountAsync(new DocumentQuery(
                "configurationDocument",
                "find-by-category-priority",
                [
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools")),
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("priority", "7"))
                ],
                resultOperation: BoundedQueryResultOperation.Count)));
            Assert.Equal(
                PhysicalSchemaApplicationOutcome.NoChanges,
                (await PhysicalSchemaApplication.ApplyAsync(
                    additive.Target,
                    new PostgreSqlPhysicalSchemaExecutor(connectionString))).Outcome);

            await TamperPostgreSqlIdentityCollationAsync(
                connectionString,
                route.LinkedIndexStorage!.Name.Identifier,
                route.LinkedRelationship!.DocumentId.Identifier);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PhysicalSchemaApplication.ApplyAsync(
                    additive.Target,
                    new PostgreSqlPhysicalSchemaExecutor(connectionString)));
            Assert.Contains("collation", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await DropDatabaseAsync(database);
        }
    }

    protected override Task<PhysicalStorageFixture> CreateAsync(
        PhysicalStorageForm form,
        bool dedicatedWithoutLinked = false) =>
        CreateFixtureAsync(RelationalPhysicalStorageTestModels.Create(
            form,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            dedicatedWithoutLinked: dedicatedWithoutLinked,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames));

    protected override async Task<UnfilteredGlobalQueryFixture> CreateUnfilteredGlobalIdQueryAsync()
    {
        var model = CreateUnfilteredGlobalIdQueryModel(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            Guid.NewGuid().ToString("N")[..8]);
        var connectionString = container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new PostgreSqlPhysicalSchemaExecutor(connectionString));
        var route = model.Target.Routes.Single();
        var store = new PostgreSqlPhysicalDocumentStore(
            connectionString,
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Global);
        return new UnfilteredGlobalQueryFixture(
            store,
            PostgreSqlPhysicalQueryRuntime.Create(store, model.Manifest, route, model.Target.Provider),
            route,
            () => ValueTask.CompletedTask);
    }

    protected override void AssertUnfilteredGlobalIdQueryPlan(PhysicalDocumentQueryExplanation explanation)
    {
        Assert.All(explanation.Commands, command =>
        {
            Assert.Equal("postgresql-json", command.NativePlanFormat);
            Assert.False(string.IsNullOrWhiteSpace(command.NativePlan));
        });
        var page = explanation.Commands.Single(command =>
            command.Kind == PhysicalDocumentQueryCommandKind.Page);
        Assert.Contains(explanation.Plan.IndexName!.Identifier, page.NativePlan, StringComparison.Ordinal);
        Assert.Contains("\"Node Type\": \"Limit\"", page.NativePlan, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Node Type\": \"Seq Scan\"", page.NativePlan, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Node Type\": \"Sort\"", page.NativePlan, StringComparison.Ordinal);
    }

    protected override async Task PrepareUnfilteredGlobalIdQueryPlanAsync(
        UnfilteredGlobalQueryFixture fixture)
    {
        await SeedPlanNoiseAsync(fixture.Route, "ignored", "c");
        await AnalyzeRouteAsync(fixture.Route);
    }

    protected override async Task<RelationalServerIdentityFixture> CreateIdentityAsync(
        PhysicalStorageForm form,
        StringIdentityCasePolicy stringCasePolicy = StringIdentityCasePolicy.Ordinal)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            form,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames,
            stringCasePolicy: stringCasePolicy);
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var store = new PostgreSqlPhysicalDocumentStore(
            container.GetConnectionString(), model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        return new RelationalServerIdentityFixture(
            store,
            PostgreSqlPhysicalQueryRuntime.Create(store, model.Manifest, route, model.Target.Provider),
            route,
            synchronizeAfterPrimaryLock: true,
            lookupKey => CorruptPrimaryLookupAsync(route, lookupKey),
            (retainedId, comparisonKey) => CorruptLinkedIdentityAsync(route, retainedId, comparisonKey),
            linked => ReadIdentitySchemaAsync(route, linked),
            linked => DropComparisonEvidenceAsync(route, linked),
            async () =>
            {
                await PhysicalSchemaApplication.ApplyAsync(
                    model.Target,
                    new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
            },
            column => column,
            () => ValueTask.CompletedTask);
    }

    protected override async Task<RelationalServerLinkedBackfillCollisionFixture> CreateLinkedBackfillCollisionAsync()
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            instance: instance,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        var connectionString = container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(
            initial.Target,
            new PostgreSqlPhysicalSchemaExecutor(connectionString));
        var route = additive.Target.Routes.Single();
        var priority = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "priority");
        return new RelationalServerLinkedBackfillCollisionFixture(
            new PostgreSqlPhysicalDocumentStore(
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
                new PostgreSqlPhysicalSchemaExecutor(connectionString))).Outcome,
            () => ReadNullableInt32Async(
                route.LinkedIndexStorage!.Name.Identifier,
                priority.Column.Identifier,
                route.LinkedRelationship!.DocumentId.Identifier),
            () => ValueTask.CompletedTask);
    }

    protected override async Task<ScopedPhysicalStorageFixture> CreateScopedAsync(PhysicalStorageForm form)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            form,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            scoped: true,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        return new ScopedPhysicalStorageFixture(
            access => new PostgreSqlPhysicalDocumentStore(container.GetConnectionString(), model.Manifest, model.Target.Routes, access),
            () => ValueTask.CompletedTask);
    }

    protected override async Task<PhysicalStorageEvolutionFixture> CreateEvolutionAsync(PhysicalStorageForm form)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            form, PostgreSqlGroundworkCapabilities.Provider, includePriority: false, instance: instance,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        var additive = RelationalPhysicalStorageTestModels.Create(
            form, PostgreSqlGroundworkCapabilities.Provider, includePriority: true, instance: instance,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        var initialStore = new PostgreSqlPhysicalDocumentStore(
            container.GetConnectionString(), initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global);
        return new PhysicalStorageEvolutionFixture(
            initialStore,
            () => ApplyAndCreateAsync(additive),
            async () => (await PhysicalSchemaApplication.ApplyAsync(
                additive.Target,
                new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()))).Outcome,
            async cancellationToken => await new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString())
                .AcquireApplicationLockAsync(additive.Target.Identity, cancellationToken),
            () => ValueTask.CompletedTask);
    }

    private async Task<PhysicalStorageFixture> ApplyAndCreateAsync(
        (Groundwork.Core.Manifests.StorageManifest Manifest, PhysicalSchemaTarget Target) model)
    {
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        return await CreateFixtureAsync(model, apply: false);
    }

    private async Task<PhysicalStorageFixture> CreateFixtureAsync(
        (Groundwork.Core.Manifests.StorageManifest Manifest, PhysicalSchemaTarget Target) model,
        bool apply = true)
    {
        if (apply)
            await PhysicalSchemaApplication.ApplyAsync(model.Target, new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var store = new PostgreSqlPhysicalDocumentStore(
            container.GetConnectionString(), model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        return new PhysicalStorageFixture(
            store,
            route.ProjectedColumns.Count == 0
                ? null
                : PostgreSqlPhysicalQueryRuntime.Create(store, model.Manifest, route, model.Target.Provider),
            route,
            () => ExplainCategoryLookupAsync(store, model.Manifest, route, model.Target.Provider),
            () => ValueTask.CompletedTask);
    }

    private async Task<string> ExplainCategoryLookupAsync(
        PostgreSqlPhysicalDocumentStore store,
        Groundwork.Core.Manifests.StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider)
    {
        var query = new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))],
            [new DocumentQueryOrder("category")],
            take: 1);
        await SeedPlanNoiseAsync(route);
        await AnalyzeRouteAsync(route);
        var runtime = PostgreSqlPhysicalQueryRuntime.Create(store, manifest, route, provider);
        var explanation = await Assert.IsAssignableFrom<IPhysicalDocumentQueryExplainer>(runtime).ExplainAsync(query);
        return string.Join(Environment.NewLine, explanation.Commands.Select(command => command.NativePlan));
    }

    private async Task AnalyzeRouteAsync(ExecutableStorageRoute route)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using (var statistics = connection.CreateCommand())
        {
            statistics.CommandText = route.LinkedIndexStorage is null
                ? $"ANALYZE {QuoteIdentifier(route.PrimaryStorage.Name.Identifier)};"
                : $"ANALYZE {QuoteIdentifier(route.PrimaryStorage.Name.Identifier)}; ANALYZE {QuoteIdentifier(route.LinkedIndexStorage.Name.Identifier)};";
            await statistics.ExecuteNonQueryAsync();
        }
    }

    private async Task<string> ExplainAsync(RelationalPhysicalQueryCommand rendered)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN (FORMAT JSON) {rendered.CommandText}";
        foreach (var (name, value) in rendered.Parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            lines.Add(reader.GetString(0));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Captures an <c>EXPLAIN ANALYZE</c> plan for the exact production collection-delete command
    /// while the mutation selection table and outer mutation transaction are still live. The
    /// savepoint rolls the probe back before the handler performs its real delete.
    /// </summary>
    private static async ValueTask<string> ExplainCollectionDeleteAsync(
        DbConnection connection,
        DbTransaction transaction,
        RelationalPhysicalQueryCommand rendered,
        CancellationToken cancellationToken)
    {
        const string savepoint = "groundwork_collection_plan";
        await ExecutePlanControlAsync(connection, transaction, $"SAVEPOINT {savepoint};", cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"EXPLAIN (ANALYZE, FORMAT JSON) {rendered.CommandText}";
            foreach (var (name, value) in rendered.Parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            var lines = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                lines.Add(reader.GetString(0));
            return string.Join(Environment.NewLine, lines);
        }
        finally
        {
            await ExecutePlanControlAsync(connection, transaction, $"ROLLBACK TO SAVEPOINT {savepoint};", cancellationToken);
            await ExecutePlanControlAsync(connection, transaction, $"RELEASE SAVEPOINT {savepoint};", cancellationToken);
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

        using var document = JsonDocument.Parse(nativePlan);
        var exactComparison = collection.IdComparisonKey.Column.Identifier;
        var ownerColumns = new[]
        {
            collection.DocumentKind.Column.Identifier,
            collection.StorageScope.Column.Identifier,
            collection.IdLookupKey.Column.Identifier
        };
        var expectedIndex = $"{collection.Storage.Name.Identifier}_pkey";
        var nodes = EnumeratePlanNodes(document.RootElement).ToArray();
        if (nodes.Any(node =>
                string.Equals(ReadPlanString(node, "Relation Name"), collection.Storage.Name.Identifier, StringComparison.Ordinal) &&
                string.Equals(ReadPlanString(node, "Node Type"), "Seq Scan", StringComparison.Ordinal)))
            return false;

        return nodes.Any(node =>
            (string.Equals(ReadPlanString(node, "Node Type"), "Index Scan", StringComparison.Ordinal) ||
             string.Equals(ReadPlanString(node, "Node Type"), "Index Only Scan", StringComparison.Ordinal)) &&
            string.Equals(ReadPlanString(node, "Relation Name"), collection.Storage.Name.Identifier, StringComparison.Ordinal) &&
            string.Equals(ReadPlanString(node, "Index Name"), expectedIndex, StringComparison.Ordinal) &&
            ownerColumns.All(column => ContainsPlanColumn(ReadPlanString(node, "Index Cond"), column)) &&
            ContainsPlanColumn(PlanNodeText(node), exactComparison));
    }

    private static IEnumerable<JsonElement> EnumeratePlanNodes(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var node in EnumeratePlanNodes(item))
                    yield return node;
            yield break;
        }

        if (element.ValueKind != JsonValueKind.Object)
            yield break;

        if (element.TryGetProperty("Plan", out var plan))
        {
            foreach (var node in EnumeratePlanNodes(plan))
                yield return node;
            yield break;
        }

        if (element.TryGetProperty("Node Type", out _))
            yield return element;
        if (element.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                foreach (var node in EnumeratePlanNodes(child))
                    yield return node;
    }

    private static string ReadPlanString(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string PlanNodeText(JsonElement node) => string.Join(
        Environment.NewLine,
        new[] { "Index Cond", "Filter", "Hash Cond", "Join Filter", "Merge Cond" }
            .Select(property => ReadPlanString(node, property)));

    private static bool ContainsPlanColumn(string planText, string column) =>
        planText.Contains($"\"{column}\"", StringComparison.Ordinal) ||
        planText.Contains(column, StringComparison.Ordinal);

    [Fact]
    public void Collection_owner_primary_key_plan_recognition_rejects_non_owner_access()
    {
        var collection = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.PhysicalEntityTable,
                PostgreSqlGroundworkCapabilities.Provider,
                includePriority: true,
                normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames,
                includeCollection: true,
                mutationOptions: new(IncludeRangeDelete: true))
            .Target.Routes.Single().CollectionElementStorages.Single();
        Dictionary<string, object?> IndexNode(string indexName, string filter) =>
            new()
            {
                ["Node Type"] = "Index Scan",
                ["Relation Name"] = collection.Storage.Name.Identifier,
                ["Index Name"] = indexName,
                ["Index Cond"] = string.Join(" AND ", new[]
                {
                    $"{collection.DocumentKind.Column.Identifier} = s.kind",
                    $"{collection.StorageScope.Column.Identifier} = s.scope",
                    $"{collection.IdLookupKey.Column.Identifier} = s.lookup"
                }),
                ["Filter"] = filter
            };
        var validIndexNode = IndexNode(
            $"{collection.Storage.Name.Identifier}_pkey",
            $"{collection.IdComparisonKey.Column.Identifier} = s.comparison");
        var fixtures = new[]
        {
            new Dictionary<string, object?>
            {
                ["Plan"] = IndexNode(
                    "wrong_index",
                    $"{collection.IdComparisonKey.Column.Identifier} = s.comparison")
            },
            new Dictionary<string, object?>
            {
                ["Plan"] = new Dictionary<string, object?>
                {
                    ["Node Type"] = "Nested Loop",
                    ["Plans"] = new object[]
                    {
                        IndexNode($"{collection.Storage.Name.Identifier}_pkey", string.Empty),
                        new Dictionary<string, object?>
                        {
                            ["Node Type"] = "Result",
                            ["Filter"] = $"{collection.IdComparisonKey.Column.Identifier} = s.comparison"
                        }
                    }
                }
            },
            new Dictionary<string, object?>
            {
                ["Plan"] = new Dictionary<string, object?>
                {
                    ["Node Type"] = "Nested Loop",
                    ["Plans"] = new object[]
                    {
                        validIndexNode,
                        new Dictionary<string, object?>
                        {
                            ["Node Type"] = "Seq Scan",
                            ["Relation Name"] = collection.Storage.Name.Identifier
                        }
                    }
                }
            }
        };

        Assert.All(fixtures, fixture =>
            Assert.False(UsesCollectionOwnerPrimaryKey(JsonSerializer.Serialize(new[] { fixture }), collection)));
    }

    private async Task SeedPlanNoiseAsync(
        ExecutableStorageRoute route,
        string categoryValue = "tools",
        string? sourceId = null)
    {
        var category = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "category");
        var table = category.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        var id = category.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.Envelope.Id.Identifier
            : route.LinkedRelationship!.DocumentId.Identifier;
        var identity = category.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.Envelope.Identity
            : route.LinkedRelationship!.Identity;
        var identityColumns = new HashSet<string>(StringComparer.Ordinal)
        {
            id,
            identity.ComparisonKey.Identifier,
            identity.LookupKey.Identifier
        };
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        var columns = new List<string>();
        await using (var metadata = connection.CreateCommand())
        {
            metadata.CommandText = """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = current_schema() AND table_name = @table AND is_generated = 'NEVER'
                ORDER BY ordinal_position;
                """;
            metadata.Parameters.AddWithValue("table", table);
            await using var reader = await metadata.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(0));
        }
        await using var seed = connection.CreateCommand();
        var sourceIdentityPredicate = sourceId is null
            ? string.Empty
            : $" AND {QuoteIdentifier(id)} = @sourceId";
        seed.CommandText = $"""
            WITH source AS (
                SELECT * FROM {QuoteIdentifier(table)}
                WHERE {QuoteIdentifier(category.Column.Identifier)} = @category{sourceIdentityPredicate}
                LIMIT 1
            )
            INSERT INTO {QuoteIdentifier(table)} ({string.Join(", ", columns.Select(QuoteIdentifier))})
            SELECT {string.Join(", ", columns.Select(column => identityColumns.Contains(column)
                ? $"s.{QuoteIdentifier(column)} || '-noise-' || n::text"
                : column == category.Column.Identifier ? "'noise'" : $"s.{QuoteIdentifier(column)}"))}
            FROM source s CROSS JOIN generate_series(1, 4096) AS n;
            """;
        seed.Parameters.AddWithValue("category", categoryValue);
        if (sourceId is not null)
            seed.Parameters.AddWithValue("sourceId", sourceId);
        await seed.ExecuteNonQueryAsync();
    }

    private async Task SeedBoundedHydrationEvidenceAsync(ExecutableStorageRoute route)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await CopyRowsAsync(
            connection,
            route.LinkedIndexStorage!.Name.Identifier,
            route.LinkedRelationship!.DocumentId.Identifier,
            route.LinkedRelationship.Identity.ComparisonKey.Identifier,
            route.LinkedRelationship.Identity.LookupKey.Identifier,
            "match",
            999);
        await CopyRowsAsync(
            connection,
            route.PrimaryStorage.Name.Identifier,
            route.Envelope.Id.Identifier,
            route.Envelope.Identity.ComparisonKey.Identifier,
            route.Envelope.Identity.LookupKey.Identifier,
            "match",
            999);
        await CopyRowsAsync(
            connection,
            route.LinkedIndexStorage.Name.Identifier,
            route.LinkedRelationship.DocumentId.Identifier,
            route.LinkedRelationship.Identity.ComparisonKey.Identifier,
            route.LinkedRelationship.Identity.LookupKey.Identifier,
            "primary-noise",
            99_000,
            route.ProjectedColumns.Single(column => column.Definition.LogicalName == "category").Column.Identifier,
            "noise");
        await CopyRowsAsync(
            connection,
            route.PrimaryStorage.Name.Identifier,
            route.Envelope.Id.Identifier,
            route.Envelope.Identity.ComparisonKey.Identifier,
            route.Envelope.Identity.LookupKey.Identifier,
            "primary-noise",
            99_000);
    }

    private static async Task CopyRowsAsync(
        NpgsqlConnection connection,
        string table,
        string idColumn,
        string comparisonColumn,
        string lookupColumn,
        string prefix,
        int count,
        string? overrideColumn = null,
        string? overrideValue = null)
    {
        var columns = new List<string>();
        await using (var metadata = connection.CreateCommand())
        {
            metadata.CommandText = """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = current_schema() AND table_name = @table AND is_generated = 'NEVER'
                ORDER BY ordinal_position;
                """;
            metadata.Parameters.AddWithValue("table", table);
            await using var reader = await metadata.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(0));
        }
        await using var copy = connection.CreateCommand();
        copy.CommandText = $"""
            WITH source AS (
                SELECT * FROM {QuoteIdentifier(table)} LIMIT 1
            )
            INSERT INTO {QuoteIdentifier(table)} ({string.Join(", ", columns.Select(QuoteIdentifier))})
            SELECT {string.Join(", ", columns.Select(column => column switch
            {
                var value when value == idColumn => $"'{prefix}-id-' || n::text",
                var value when value == comparisonColumn => $"'{prefix}-comparison-' || n::text",
                var value when value == lookupColumn => $"'{prefix}-lookup-' || n::text",
                var value when value == overrideColumn => $"'{overrideValue}'",
                _ => $"s.{QuoteIdentifier(column)}"
            }))}
            FROM source s CROSS JOIN generate_series(1, @count) AS n;
            """;
        copy.Parameters.AddWithValue("count", count);
        await copy.ExecuteNonQueryAsync();
    }

    private async Task CorruptPrimaryLookupAsync(ExecutableStorageRoute route, string lookupKey)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE {QuoteIdentifier(route.PrimaryStorage.Name.Identifier)} SET " +
            $"{QuoteIdentifier(route.Envelope.Identity.LookupKey.Identifier)} = @lookupKey;";
        command.Parameters.AddWithValue("lookupKey", lookupKey);
        await command.ExecuteNonQueryAsync();
    }

    private async Task CorruptLinkedIdentityAsync(
        ExecutableStorageRoute route,
        string retainedId,
        string comparisonKey)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE {QuoteIdentifier(route.LinkedIndexStorage!.Name.Identifier)} SET " +
            $"{QuoteIdentifier(route.LinkedRelationship!.DocumentId.Identifier)} = @retainedId, " +
            $"{QuoteIdentifier(route.LinkedRelationship.Identity.ComparisonKey.Identifier)} = @comparisonKey;";
        command.Parameters.AddWithValue("retainedId", retainedId);
        command.Parameters.AddWithValue("comparisonKey", comparisonKey);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetLinkedIdentityAsync(
        ExecutableStorageRoute route,
        string lookupKey,
        string retainedId,
        string comparisonKey)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE {QuoteIdentifier(route.LinkedIndexStorage!.Name.Identifier)} SET " +
            $"{QuoteIdentifier(route.LinkedRelationship!.DocumentId.Identifier)} = @retainedId, " +
            $"{QuoteIdentifier(route.LinkedRelationship.Identity.ComparisonKey.Identifier)} = @comparisonKey " +
            $"WHERE {QuoteIdentifier(route.LinkedRelationship.Identity.LookupKey.Identifier)} = @lookupKey;";
        command.Parameters.AddWithValue("retainedId", retainedId);
        command.Parameters.AddWithValue("comparisonKey", comparisonKey);
        command.Parameters.AddWithValue("lookupKey", lookupKey);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<IReadOnlyList<int?>> ReadNullableInt32Async(
        string table,
        string column,
        string orderBy)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {QuoteIdentifier(column)} FROM {QuoteIdentifier(table)} ORDER BY {QuoteIdentifier(orderBy)};";
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
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = current_schema() AND table_name = @table;
                """;
            command.Parameters.AddWithValue("table", table);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(0));
        }
        var primaryKey = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON kcu.constraint_schema = tc.constraint_schema
                 AND kcu.constraint_name = tc.constraint_name
                WHERE tc.table_schema = current_schema()
                  AND tc.table_name = @table
                  AND tc.constraint_type = 'PRIMARY KEY'
                ORDER BY kcu.ordinal_position;
                """;
            command.Parameters.AddWithValue("table", table);
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
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        var dependentIndexes = route.Indexes
            .Where(index =>
                index.Target == (linked
                    ? ExecutableStorageObjectRole.LinkedIndexStorage
                    : ExecutableStorageObjectRole.PrimaryStorage) &&
                index.Columns.Any(column => column.Column.Identifier == comparison))
            .Select(index => $"DROP INDEX {QuoteIdentifier(index.Name.Identifier)}; ");
        command.CommandText =
            string.Concat(dependentIndexes) +
            $"ALTER TABLE {QuoteIdentifier(table)} DROP COLUMN {QuoteIdentifier(comparison)};";
        await command.ExecuteNonQueryAsync();
    }

    private async Task TerminateSessionAsync(long sessionId)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_catalog.pg_terminate_backend(@sessionId);";
        command.Parameters.AddWithValue("sessionId", checked((int)sessionId));
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetFenceAsync(PhysicalSchemaTargetIdentity target, long fence)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE groundwork_physical_schema_locks SET fence = @fence WHERE manifest_id = @manifestId AND provider_name = @providerName;";
        command.Parameters.AddWithValue("fence", fence);
        command.Parameters.AddWithValue("manifestId", target.ManifestIdentity.Value);
        command.Parameters.AddWithValue("providerName", target.ProviderName);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<string> CreateIcuDatabaseAsync(string database)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {QuoteIdentifier(database)} TEMPLATE template0 ENCODING 'UTF8' LOCALE_PROVIDER icu ICU_LOCALE 'und-u-ks-level1';";
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = database }.ConnectionString;
    }

    private async Task DropDatabaseAsync(string database)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS {QuoteIdentifier(database)} WITH (FORCE);";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task TamperPostgreSqlIdentityCollationAsync(
        string connectionString,
        string table,
        string column)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        string constraint;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT conname FROM pg_catalog.pg_constraint WHERE conrelid = @table::regclass AND contype = 'p';";
            read.Parameters.AddWithValue("table", table);
            constraint = (string)(await read.ExecuteScalarAsync())!;
        }
        await using var tamper = connection.CreateCommand();
        tamper.CommandText = $"ALTER TABLE {QuoteIdentifier(table)} DROP CONSTRAINT {QuoteIdentifier(constraint)}; " +
                             $"ALTER TABLE {QuoteIdentifier(table)} ALTER COLUMN {QuoteIdentifier(column)} TYPE text COLLATE gw_nondeterministic;";
        await tamper.ExecuteNonQueryAsync();
    }

    private Task<long> CountOperationEvidenceAsync(string operationId, string fingerprint) =>
        CountAsync(
            "SELECT COUNT(*) FROM groundwork_physical_schema_operations WHERE operation_id = @first AND operation_fingerprint = @second;",
            operationId,
            fingerprint);

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@table) IS NOT NULL;";
        command.Parameters.AddWithValue("table", table);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private Task<long> CountAppliedStateAsync(string manifestId, string providerName) =>
        CountAsync(
            "SELECT COUNT(*) FROM groundwork_physical_schema_state WHERE manifest_id = @first AND provider_name = @second;",
            manifestId,
            providerName);

    private async Task<long> CountProjectedValuesAsync(string table, string column)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(table)} WHERE {QuoteIdentifier(column)} IS NOT NULL;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountFenceAsync(string connectionString, PhysicalSchemaTargetIdentity target)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM groundwork_physical_schema_locks WHERE manifest_id = @manifestId AND provider_name = @providerName;";
        command.Parameters.AddWithValue("manifestId", target.ManifestIdentity.Value);
        command.Parameters.AddWithValue("providerName", target.ProviderName);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task TamperInfrastructureAsync(
        string connectionString,
        string schema,
        InfrastructureTamper tamper)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var sql = tamper switch
        {
            InfrastructureTamper.WrongObjectKind => """
                DROP TABLE groundwork_physical_schema_locks;
                CREATE VIEW groundwork_physical_schema_locks AS
                    SELECT NULL::text AS manifest_id, NULL::text AS provider_name WHERE false;
                """,
            InfrastructureTamper.ExtraOperationsColumn =>
                "ALTER TABLE groundwork_physical_schema_operations ADD COLUMN unexpected integer NULL;",
            InfrastructureTamper.MissingStateColumn =>
                "ALTER TABLE groundwork_physical_schema_state DROP COLUMN applied_state_json;",
            InfrastructureTamper.NullableLockOwner =>
                "ALTER TABLE groundwork_physical_schema_locks ALTER COLUMN owner_id DROP NOT NULL;",
            InfrastructureTamper.WrongLockOwnerType =>
                "ALTER TABLE groundwork_physical_schema_locks ALTER COLUMN owner_id TYPE character varying(32);",
            InfrastructureTamper.WrongStateCollation => $"""
                CREATE COLLATION {QuoteIdentifier(schema)}.gw_nondeterministic
                    (provider = icu, locale = 'und-u-ks-level1', deterministic = false);
                ALTER TABLE groundwork_physical_schema_state ALTER COLUMN target_fingerprint
                    TYPE text COLLATE {QuoteIdentifier(schema)}.gw_nondeterministic;
                """,
            InfrastructureTamper.SameNameCShadowCollation => $"""
                CREATE COLLATION {QuoteIdentifier(schema)}.{QuoteIdentifier("C")}
                    (provider = icu, locale = 'und-u-ks-level1', deterministic = false);
                ALTER TABLE groundwork_physical_schema_locks DROP CONSTRAINT groundwork_physical_schema_locks_pkey;
                ALTER TABLE groundwork_physical_schema_locks ALTER COLUMN manifest_id
                    TYPE text COLLATE {QuoteIdentifier(schema)}.{QuoteIdentifier("C")};
                ALTER TABLE groundwork_physical_schema_locks ADD PRIMARY KEY (manifest_id, provider_name);
                """,
            InfrastructureTamper.MissingStatePrimaryKey =>
                "ALTER TABLE groundwork_physical_schema_state DROP CONSTRAINT groundwork_physical_schema_state_pkey;",
            InfrastructureTamper.ReorderedOperationsPrimaryKey => """
                ALTER TABLE groundwork_physical_schema_operations DROP CONSTRAINT groundwork_physical_schema_operations_pkey;
                ALTER TABLE groundwork_physical_schema_operations
                    ADD PRIMARY KEY (provider_name, manifest_id, operation_id);
                """,
            InfrastructureTamper.LegacyMutationLedgerPrimaryKey => """
                ALTER TABLE groundwork_document_mutation_operations
                    DROP CONSTRAINT groundwork_document_mutation_operations_pkey;
                ALTER TABLE groundwork_document_mutation_operations
                    DROP COLUMN manifest_key,
                    DROP COLUMN provider_key,
                    DROP COLUMN storage_unit_key,
                    DROP COLUMN storage_scope_key,
                    DROP COLUMN operation_key;
                ALTER TABLE groundwork_document_mutation_operations
                    ADD PRIMARY KEY (manifest_id, provider_name, storage_unit, storage_scope, operation_id);
                """,
            InfrastructureTamper.WrongMutationLedgerHashExpression => """
                ALTER TABLE groundwork_document_mutation_operations
                    DROP CONSTRAINT groundwork_document_mutation_operations_pkey;
                ALTER TABLE groundwork_document_mutation_operations DROP COLUMN operation_key;
                ALTER TABLE groundwork_document_mutation_operations
                    ADD COLUMN operation_key bytea
                    GENERATED ALWAYS AS (groundwork_utf8_sha256('wrong-operation')) STORED NOT NULL;
                ALTER TABLE groundwork_document_mutation_operations
                    ADD PRIMARY KEY (manifest_key, provider_key, storage_unit_key, storage_scope_key, operation_key);
                """,
            InfrastructureTamper.WrongMutationHashFunction => """
                CREATE OR REPLACE FUNCTION groundwork_utf8_sha256(value text) RETURNS bytea
                LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
                AS $function$
                    SELECT pg_catalog.sha256(pg_catalog.convert_to('wrong-value', 'UTF8'))
                $function$;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(tamper), tamper, null)
        };
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string sql, string first, string second)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("first", first);
        command.Parameters.AddWithValue("second", second);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private RelationalLockContentionProbe LockContention() => new(
        ReadSessionIdAsync,
        WaitUntilBlockedAsync);

    private protected override RelationalMutationServerHarness<PostgreSqlPhysicalDocumentStore> MutationHarness() => new(
        PostgreSqlGroundworkCapabilities.Provider,
        "postgresql",
        PostgreSqlGroundworkCapabilities.PhysicalNames,
        () => new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()),
        (manifest, routes, access) => new PostgreSqlPhysicalDocumentStore(
            container.GetConnectionString(), manifest, routes, access),
        PostgreSqlPhysicalMutationRuntime.Create,
        PostgreSqlPhysicalQueryRuntime.Create,
        () => new NpgsqlConnection(container.GetConnectionString()),
        () => new PostgreSqlPhysicalDocumentDialect(),
        LockContention());

    private protected override IPhysicalSchemaExecutor CreateSchemaExecutorWithOperationHook(
        Func<PhysicalSchemaOperation, CancellationToken, Task>? beforeOperation) =>
        new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString(), beforeOperation, null);

    private protected override Func<IPhysicalSchemaExecutor> SingleConnectionSchemaExecutorFactory()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            MaxPoolSize = 1
        }.ConnectionString;
        return () => new PostgreSqlPhysicalSchemaExecutor(connectionString);
    }

    private protected override async Task PrepareMutationPlanEvidenceAsync(ExecutableStorageRoute route)
    {
        await SeedPlanNoiseAsync(route);
        await AnalyzeRouteAsync(route);
    }

    private protected override async Task<EphemeralServerDatabase> CreateEphemeralDatabaseAsync(string name)
    {
        await ExecuteAdminAsync($"CREATE DATABASE {QuoteIdentifier(name)};");
        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = name
        }.ConnectionString;
        return new EphemeralServerDatabase(connectionString, async () =>
        {
            NpgsqlConnection.ClearAllPools();
            await DropDatabaseAsync(name);
        });
    }

    private protected override async Task<object?> OpenPhysicalAutoApplyAsync(
        string connectionString,
        Groundwork.Core.Manifests.StorageManifest manifest,
        Groundwork.Core.Capabilities.ProviderIdentity provider,
        IPhysicalNamePolicy namePolicy) =>
        await PostgreSqlDocumentStoreFactory.OpenPhysicalAsync(
            connectionString,
            manifest,
            provider,
            DocumentStoreAccess.Global,
            namePolicy: namePolicy,
            options: new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true });

    private protected override IPhysicalSchemaHistoryInspector CreateSchemaHistoryInspectorFor(string connectionString) =>
        new PostgreSqlPhysicalSchemaExecutor(connectionString);

    private protected override void AssertMutationExplainCommandPlan(string? nativePlan, string expectedIndex)
    {
        Assert.Contains(expectedIndex, nativePlan, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan", nativePlan, StringComparison.Ordinal);
    }

    private protected override async Task RebuildCollectionMembershipIndexReversedAsync(
        ExecutableCollectionElementStorageRoute storage)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"DROP INDEX {QuoteIdentifier(storage.MembershipKey.Name.Identifier)}; " +
            $"CREATE INDEX {QuoteIdentifier(storage.MembershipKey.Name.Identifier)} ON {QuoteIdentifier(storage.Storage.Name.Identifier)} (" +
            $"{string.Join(", ", storage.MembershipKey.OwnerColumns.Select(column => QuoteIdentifier(column.Column.Identifier))
                .Append(QuoteIdentifier(storage.MembershipKey.Value.Column.Identifier)))});";
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<int> ReadSessionIdAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_catalog.pg_backend_pid();";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task WaitUntilBlockedAsync(
        int blockedSessionId,
        int blockerSessionId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @blocker = ANY(pg_catalog.pg_blocking_pids(@blocked));";
        command.Parameters.AddWithValue("blocker", blockerSessionId);
        command.Parameters.AddWithValue("blocked", blockedSessionId);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken)))
                return;
            await Task.Delay(20, cancellationToken);
        }
        throw new TimeoutException(
            $"PostgreSQL session {blockedSessionId} was not observed waiting on session {blockerSessionId}.");
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    public enum InfrastructureTamper
    {
        WrongObjectKind,
        ExtraOperationsColumn,
        MissingStateColumn,
        NullableLockOwner,
        WrongLockOwnerType,
        WrongStateCollation,
        SameNameCShadowCollation,
        MissingStatePrimaryKey,
        ReorderedOperationsPrimaryKey,
        LegacyMutationLedgerPrimaryKey,
        WrongMutationLedgerHashExpression,
        WrongMutationHashFunction
    }

    public enum CollectionElementDrift
    {
        WrongType,
        WrongCollation,
        WrongDefault,
        WrongPrimaryKeyOrder
    }

}
