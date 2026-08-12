using System.Text.Json;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Relational.Documents;
using Groundwork.TestInfrastructure;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public abstract class RelationalServerPhysicalIdentityConformance<TStore> : PhysicalStorageConformance
    where TStore : RelationalPhysicalDocumentStore
{
    protected abstract Task<RelationalServerIdentityFixture> CreateIdentityAsync(
        PhysicalStorageForm form,
        StringIdentityCasePolicy stringCasePolicy = StringIdentityCasePolicy.Ordinal);

    protected abstract Task<RelationalServerLinkedBackfillCollisionFixture> CreateLinkedBackfillCollisionAsync();

    /// <summary>The provider delta bundle every lifted server-tier scenario is expressed against.</summary>
    private protected abstract RelationalMutationServerHarness<TStore> MutationHarness();

    /// <summary>A schema executor whose operation execution can be intercepted (transition fencing).</summary>
    private protected abstract IPhysicalSchemaExecutor CreateSchemaExecutorWithOperationHook(
        Func<PhysicalSchemaOperation, CancellationToken, Task>? beforeOperation);

    /// <summary>A schema-executor factory pinned to a single pooled connection (acknowledgement-loss replay).</summary>
    private protected abstract Func<IPhysicalSchemaExecutor> SingleConnectionSchemaExecutorFactory();

    /// <summary>Seeds provider statistics/noise so mutation explains bind the declared index.</summary>
    private protected abstract Task PrepareMutationPlanEvidenceAsync(ExecutableStorageRoute route);

    /// <summary>Provider-native plan assertion for every command of a bounded-mutation explain.</summary>
    private protected abstract void AssertMutationExplainCommandPlan(string? nativePlan, string expectedIndex);

    /// <summary>Recreates the collection membership index with its value/owner columns reversed.</summary>
    private protected abstract Task RebuildCollectionMembershipIndexReversedAsync(
        ExecutableCollectionElementStorageRoute storage);

    /// <summary>Provisions a server database that is dropped (with pools cleared) on disposal.</summary>
    private protected abstract Task<EphemeralServerDatabase> CreateEphemeralDatabaseAsync(string name);

    /// <summary>The public physical factory entry point with safe auto-apply enabled.</summary>
    private protected abstract Task<object?> OpenPhysicalAutoApplyAsync(
        string connectionString,
        StorageManifest manifest,
        Groundwork.Core.Capabilities.ProviderIdentity provider,
        IPhysicalNamePolicy namePolicy);

    /// <summary>A schema-history inspector bound to the given connection string.</summary>
    private protected abstract IPhysicalSchemaHistoryInspector CreateSchemaHistoryInspectorFor(string connectionString);

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task UnicodeIgnoreCaseUsesRetainedOriginalForEquivalentSpelling(PhysicalStorageForm form)
    {
        await using var fixture = await CreateIdentityAsync(form, StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(Save("Configuration-One", "tools", 0))).Status);
        var loaded = await fixture.Documents.LoadAsync("configurationDocument", "configuration-one");
        var conflict = await fixture.Documents.SaveAsync(Save("configuration-one", "gadgets", 1));

        Assert.Equal("Configuration-One", loaded!.Id);
        Assert.Equal(DocumentStoreWriteStatus.IdentityConflict, conflict.Status);
        Assert.Equal("Configuration-One", conflict.AuthoritativeId);
        Assert.Contains("\"category\":\"tools\"", loaded.ContentJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task UnicodeIgnoreCaseDeleteUsesEquivalentSpellingWithoutBypassingOcc(PhysicalStorageForm form)
    {
        await using var fixture = await CreateIdentityAsync(form, StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        await fixture.Documents.SaveAsync(Save("Configuration-One", "tools", 0));

        var stale = await fixture.Documents.DeleteAsync(new DeleteDocumentRequest(
            "configurationDocument", "configuration-one", ExpectedVersion: 2));
        var deleted = await fixture.Documents.DeleteAsync(new DeleteDocumentRequest(
            "configurationDocument", "configuration-one", ExpectedVersion: 1));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, stale.Status);
        Assert.Equal(DocumentStoreWriteStatus.Deleted, deleted.Status);
        Assert.Equal("Configuration-One", deleted.AuthoritativeId);
        Assert.Null(await fixture.Documents.LoadAsync("configurationDocument", "Configuration-One"));
    }

    [Fact]
    public async Task UnicodeIgnoreCaseSupportsSupplementaryPlaneIdentitySpelling()
    {
        await using var fixture = await CreateIdentityAsync(
            PhysicalStorageForm.PhysicalEntityTable,
            StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        var retained = $"document-{char.ConvertFromUtf32(0x10428)}";
        var equivalent = $"document-{char.ConvertFromUtf32(0x10400)}";

        await fixture.Documents.SaveAsync(Save(retained, "tools", 0));
        var loaded = await fixture.Documents.LoadAsync("configurationDocument", equivalent);
        var conflict = await fixture.Documents.SaveAsync(Save(equivalent, "gadgets", 1));

        Assert.Equal(retained, loaded!.Id);
        Assert.Equal(DocumentStoreWriteStatus.IdentityConflict, conflict.Status);
        Assert.Equal(retained, conflict.AuthoritativeId);
    }

    [Fact]
    public async Task SchemaRetainsOriginalComparisonAndLookupWhileKeyingOnLookup()
    {
        await using var fixture = await CreateIdentityAsync(PhysicalStorageForm.SharedDocuments);

        AssertIdentitySchema(
            await fixture.ReadIdentitySchemaAsync(false),
            fixture.Route.Envelope.Identity,
            fixture.PhysicalKeyColumn);
        AssertIdentitySchema(
            await fixture.ReadIdentitySchemaAsync(true),
            fixture.Route.LinkedRelationship!.Identity,
            fixture.PhysicalKeyColumn);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartRejectsMissingComparisonEvidence(bool linked)
    {
        await using var fixture = await CreateIdentityAsync(
            linked ? PhysicalStorageForm.SharedDocuments : PhysicalStorageForm.PhysicalEntityTable);
        await fixture.DropComparisonEvidenceAsync(linked);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(fixture.RestartAsync);

        Assert.Contains(
            linked
                ? fixture.Route.LinkedRelationship!.Identity.ComparisonKey.Identifier
                : fixture.Route.Envelope.Identity.ComparisonKey.Identifier,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task ConcurrentEquivalentSpellingCreatesExactlyOneAuthoritativeDocument(PhysicalStorageForm form)
    {
        await using var fixture = await CreateIdentityAsync(form, StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        fixture.Store.WriteInterceptor = async (point, operation, _, _, cancellationToken) =>
        {
            if (operation != RelationalPhysicalWriteOperation.Save ||
                point != fixture.RaceSynchronizationPoint)
                return;
            if (Interlocked.Increment(ref arrivals) == 2)
                release.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };

        var first = fixture.Documents.SaveAsync(Save("Configuration-Race", "tools", 0));
        var second = fixture.Documents.SaveAsync(Save("configuration-race", "tools", 0));
        var results = await Task.WhenAll(first, second);
        fixture.Store.WriteInterceptor = null;

        var saved = Assert.Single(results, result => result.Status == DocumentStoreWriteStatus.Saved);
        var conflict = Assert.Single(results, result => result.Status == DocumentStoreWriteStatus.IdentityConflict);
        Assert.Equal(saved.Document!.Id, conflict.AuthoritativeId);
        Assert.Equal(saved.Document.Id, (await fixture.Documents.LoadAsync(
            "configurationDocument", "CONFIGURATION-RACE"))!.Id);
        Assert.Equal(1, await fixture.Queries.CountAsync(new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))],
            resultOperation: BoundedQueryResultOperation.Count)));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task LookupCollisionFailsLoadSaveAndDeleteClosed(PhysicalStorageForm form)
    {
        await using var fixture = await CreateIdentityAsync(form);
        await fixture.Documents.SaveAsync(Save("Retained-Id", "tools", 0));
        const string requestedId = "Requested-Id";
        var lookupKey = fixture.Route.Envelope.Identity.Project(requestedId).LookupKey;
        await fixture.CorruptPrimaryLookupAsync(lookupKey);

        var load = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(
            () => fixture.Documents.LoadAsync("configurationDocument", requestedId));
        var save = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(
            () => fixture.Documents.SaveAsync(Save(requestedId, "gadgets", 0)));
        var delete = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(
            () => fixture.Documents.DeleteAsync(new DeleteDocumentRequest("configurationDocument", requestedId)));

        AssertCollision(load, requestedId, lookupKey);
        AssertCollision(save, requestedId, lookupKey);
        AssertCollision(delete, requestedId, lookupKey);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    public async Task LinkedLookupCollisionRollsBackPrimaryUpdate(PhysicalStorageForm form)
    {
        await using var fixture = await CreateIdentityAsync(form);
        const string requestedId = "Requested-Id";
        await fixture.Documents.SaveAsync(Save(requestedId, "tools", 0));
        await fixture.CorruptLinkedIdentityAsync("Collision-Retained", "different-comparison");

        var exception = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(
            () => fixture.Documents.SaveAsync(Save(requestedId, "gadgets", 1)));

        AssertCollision(
            exception,
            requestedId,
            fixture.Route.LinkedRelationship!.Identity.Project(requestedId).LookupKey,
            "Collision-Retained");
        Assert.Contains(
            "\"category\":\"tools\"",
            (await fixture.Documents.LoadAsync("configurationDocument", requestedId))!.ContentJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookupCollisionTerminatesUnitOfWorkAndRollsBackPriorWrite()
    {
        await using var fixture = await CreateIdentityAsync(PhysicalStorageForm.SharedDocuments);
        await fixture.Documents.SaveAsync(Save("Retained-Id", "tools", 0));
        const string requestedId = "Requested-Id";
        await fixture.CorruptPrimaryLookupAsync(
            fixture.Route.Envelope.Identity.Project(requestedId).LookupKey);
        await using var transaction = await fixture.Documents.BeginAsync(
            DocumentCommitScope.Of("configurationDocument"));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await transaction.SaveAsync(
            Save("staged-before-collision", "tools", 0))).Status);

        await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(
            () => transaction.SaveAsync(Save(requestedId, "gadgets", 0)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        Assert.Null(await fixture.Documents.LoadAsync("configurationDocument", "staged-before-collision"));
    }

    [Fact]
    public async Task LinkedBackfillLookupCollisionRollsBackProjectedRowsAndCanRetry()
    {
        await using var fixture = await CreateLinkedBackfillCollisionAsync();
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.InitialDocuments.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "a-valid", "1", "{\"category\":\"tools\",\"priority\":1}", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.InitialDocuments.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "z-collision", "1", "{\"category\":\"tools\",\"priority\":2}", 0))).Status);

        var requestedIdentity = fixture.Route.LinkedRelationship!.Identity.Project("z-collision");
        await fixture.SetLinkedIdentityAsync(
            requestedIdentity.LookupKey,
            "Retained-Collision-Id",
            "different-comparison");

        var exception = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(
            fixture.ApplyAdditiveAsync);

        AssertCollision(
            exception,
            "z-collision",
            requestedIdentity.LookupKey,
            "Retained-Collision-Id");
        Assert.Equal([null, null], await fixture.ReadPriorityValuesAsync());

        await fixture.SetLinkedIdentityAsync(
            requestedIdentity.LookupKey,
            requestedIdentity.OriginalValue,
            requestedIdentity.ComparisonKey);
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, await fixture.ApplyAdditiveAsync());
        Assert.Equal([1, 2], await fixture.ReadPriorityValuesAsync());
    }

    protected sealed override async Task<CursorPagingFixture> CreateCursorPagingAsync(PhysicalStorageForm form)
    {
        var harness = MutationHarness();
        var model = RelationalPhysicalStorageTestModels.Create(
            form,
            harness.Provider,
            includePriority: false,
            instance: Guid.NewGuid().ToString("N")[..8],
            normalizer: harness.Normalizer,
            categoryPaging: QueryPagingSupport.Cursor);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        var route = model.Target.Routes.Single();
        return new CursorPagingFixture(
            harness.CreateStore(model.Manifest, model.Target.Routes),
            () => harness.CreateQueryRuntime(
                harness.CreateStore(model.Manifest, model.Target.Routes),
                model.Manifest,
                route,
                model.Target.Provider),
            route,
            () => ValueTask.CompletedTask);
    }

    [Fact]
    public async Task Collection_membership_and_contains_all_execute_from_typed_element_storage()
    {
        var harness = MutationHarness();
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: false,
            instance: Guid.NewGuid().ToString("N")[..8],
            normalizer: harness.Normalizer,
            includeCollection: true,
            includeCollectionMembershipQuery: true);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        var store = harness.CreateStore(model.Manifest, model.Target.Routes);
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "one", "1", """{"category":"x","permissions":["a","b","b"]}"""));
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "two", "1", """{"category":"x","permissions":["a"]}"""));
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "three", "1", """{"category":"x","permissions":["b","c"]}"""));
        var queries = harness.CreateQueryRuntime(
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
        var harness = MutationHarness();
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: false,
            instance: Guid.NewGuid().ToString("N")[..8],
            normalizer: harness.Normalizer,
            includeCollection: true,
            includeCollectionMembershipQuery: true,
            collectionType: PortablePhysicalType.Int32,
            collectionLogicalValueKind: IndexValueKind.Number,
            collectionLength: null,
            collectionCollation: null);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        var store = harness.CreateStore(model.Manifest, model.Target.Routes);
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "one", "1", """{"category":"x","permissions":[1,2]}"""));
        var runtime = harness.CreateQueryRuntime(
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
            harness.HandlerPrefix,
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
        var harness = MutationHarness();
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: false,
            instance: instance,
            normalizer: harness.Normalizer);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: false,
            instance: instance,
            normalizer: harness.Normalizer,
            includeCollection: true,
            includeCollectionMembershipQuery: true);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, harness.CreateExecutor());
        await harness.CreateStore(initial.Manifest, initial.Target.Routes)
            .SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "preexisting", "1", """{"category":"x","permissions":["a","b"]}"""));

        var applied = await PhysicalSchemaApplication.ApplyAsync(additive.Target, harness.CreateExecutor());
        var evolved = harness.CreateStore(additive.Manifest, additive.Target.Routes);
        var result = await harness.CreateQueryRuntime(
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
        var harness = MutationHarness();
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: false,
            instance: instance,
            normalizer: harness.Normalizer,
            mutationOptions: new(IncludeCategoryTransition: true));
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: false,
            instance: instance,
            normalizer: harness.Normalizer,
            mutationOptions: new(IncludeCategoryTransition: true),
            includeCollection: true,
            includeCollectionMembershipQuery: true);

        await RelationalCollectionSchemaTransitionAssertions.SuccessfulTransitionFencesOldWritersAsync(
            initial,
            additive,
            (manifest, routes) => harness.CreateStore(manifest, routes),
            (store, manifest, route) => harness.CreateMutationRuntime(
                Assert.IsAssignableFrom<TStore>(store),
                manifest,
                route,
                harness.Provider),
            CreateSchemaExecutorWithOperationHook);
    }

    [Fact]
    public async Task Collection_save_failure_rolls_back_primary_and_element_rows()
    {
        var harness = MutationHarness();
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: false,
            instance: Guid.NewGuid().ToString("N")[..8],
            normalizer: harness.Normalizer,
            includeCollection: true);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        var store = harness.CreateStore(model.Manifest, model.Target.Routes);
        store.WriteInterceptor = (point, operation, _, _, _) =>
            point == RelationalPhysicalWriteExecutionPoint.AfterPrimaryMutation &&
            operation == RelationalPhysicalWriteOperation.Save
                ? ValueTask.FromException(new InjectedServerCollectionWriteException())
                : ValueTask.CompletedTask;

        await Assert.ThrowsAsync<InjectedServerCollectionWriteException>(() => store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "failed", "1", """{"category":"x","permissions":["a"]}""")));
        store.WriteInterceptor = null;

        Assert.Null(await store.LoadAsync("configurationDocument", "failed"));
        var table = Assert.Single(model.Target.Routes.Single().CollectionElementStorages).Storage.Name.Identifier;
        await using var connection = harness.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {harness.CreateDialect().QuoteIdentifier(table)};";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Collection_membership_index_drift_is_rejected_from_live_schema()
    {
        var harness = MutationHarness();
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: false,
            instance: $"collection_{Guid.NewGuid():N}"[..19],
            normalizer: harness.Normalizer,
            includeCollection: true);
        var storage = Assert.Single(model.Target.Routes.Single().CollectionElementStorages);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        await RebuildCollectionMembershipIndexReversedAsync(storage);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor()));
        Assert.Contains(storage.MembershipKey.Name.Identifier, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bounded_mutation_explains_the_exact_execution_stages_with_the_declared_physical_index(
        bool assignment)
    {
        var harness = MutationHarness();
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: assignment,
            instance: Guid.NewGuid().ToString("N")[..8],
            normalizer: harness.Normalizer,
            // Keep assignment evidence bound to its declared source index, not a covering compound alternative.
            includeCategoryPriorityQuery: !assignment,
            mutationOptions: assignment
                ? new(IncludePriorityAssignment: true)
                : new(IncludeCategoryTransition: true));
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        var route = model.Target.Routes.Single();
        var store = harness.CreateStore(model.Manifest, model.Target.Routes);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "plan-target",
            "1",
            assignment
                ? "{\"category\":\"assignment-evidence\",\"priority\":7}"
                : "{\"category\":\"pending\"}"))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "plan-noise",
            "1",
            assignment
                ? "{\"category\":\"tools\",\"priority\":1}"
                : "{\"category\":\"tools\"}"))).Status);
        await PrepareMutationPlanEvidenceAsync(route);
        var mutationContext = new RelationalPhysicalMutationRuntimeContext(
            store,
            model.Manifest,
            route,
            model.Target.Provider,
            harness.Provider.Name,
            harness.HandlerPrefix);
        var request = assignment
            ? new DocumentMutation(
                "configurationDocument",
                "assign-priority",
                "assignment-explain",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "assignment-evidence"))])
            : new DocumentMutation("configurationDocument", "revoke-pending", "explain");
        var evidence = await Assert.IsAssignableFrom<IPhysicalDocumentMutationExplainer>(
                harness.CreateMutationRuntime(store, model.Manifest, route, model.Target.Provider))
            .ExplainAsync(request);
        if (assignment)
            Assert.IsType<PhysicalAssignMutationAction>(evidence.Plan.Action);
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
        if (assignment)
        {
            var document = await store.LoadAsync("configurationDocument", "plan-target");
            Assert.NotNull(document);
            using var json = JsonDocument.Parse(document.ContentJson);
            Assert.Equal(42, json.RootElement.GetProperty("priority").GetInt32());
        }
        Assert.Equal(
            evidence.Commands.Select(command => (
                command.Identity,
                command.RenderedCommand!,
                command.PreparedRestrictionRowCount)),
            executed);
        Assert.Null(evidence.Commands[0].PreparedRestrictionRowCount);
        Assert.True(evidence.Commands[1].PreparedRestrictionRowCount > 0);
        Assert.All(evidence.Commands, command =>
            AssertMutationExplainCommandPlan(command.NativePlan, expectedIndex));
    }

    [Fact]
    public async Task Physical_factory_auto_applies_safe_schema_when_enabled()
    {
        var harness = MutationHarness();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var database = await CreateEphemeralDatabaseAsync($"groundwork_startup_{suffix}");
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: false,
            instance: suffix,
            normalizer: harness.Normalizer);

        var store = await OpenPhysicalAutoApplyAsync(
            database.ConnectionString,
            model.Manifest,
            model.Target.Provider,
            new DelegatePhysicalNamePolicy(context => $"gw_{suffix}_{context.FeatureDefaultLogicalName}"));
        var inspection = await CreateSchemaHistoryInspectorFor(database.ConnectionString)
            .InspectHistoryAsync(model.Target, CancellationToken.None);

        Assert.NotNull(store);
        Assert.Equal(model.Target.Fingerprint, inspection.History.AppliedState?.TargetFingerprint);
    }

    [Fact]
    public Task Bounded_transition_updates_the_exact_indexed_identity_set() =>
        RelationalBoundedMutationServerAssertions.TransitionUpdatesExactIndexedIdentitySetAsync(MutationHarness());

    [Fact]
    public Task Ordinary_save_and_delete_serialize_with_the_selected_set() =>
        RelationalBoundedMutationServerAssertions.OrdinaryCrudSerializesWithSelectedSetAsync(MutationHarness());

    [Fact]
    public Task ConcurrentMaterializationAndAcknowledgementLossAreRestartSafe()
    {
        var harness = MutationHarness();
        return RelationalPhysicalServerAssertions.ConcurrentMaterializationAndAcknowledgementLossAreRestartSafeAsync(
            harness.Provider,
            harness.Normalizer,
            SingleConnectionSchemaExecutorFactory());
    }

    [Fact]
    public Task Application_lock_disposal_is_heartbeat_race_safe()
    {
        var harness = MutationHarness();
        return RelationalPhysicalServerAssertions.ApplicationLockDisposalIsHeartbeatRaceSafeAsync(
            harness.Provider,
            harness.Normalizer,
            harness.CreateExecutor);
    }

    private static SaveDocumentRequest Save(string id, string category, long expectedVersion) =>
        new("configurationDocument", id, "1", $"{{\"category\":\"{category}\",\"priority\":1}}", expectedVersion);

    private static void AssertIdentitySchema(
        RelationalIdentitySchemaEvidence evidence,
        ExecutableDocumentIdentityRoute identity,
        Func<string, string> physicalKeyColumn)
    {
        Assert.Contains(identity.OriginalId.Identifier, evidence.Columns);
        Assert.Contains(identity.ComparisonKey.Identifier, evidence.Columns);
        Assert.Contains(identity.LookupKey.Identifier, evidence.Columns);
        Assert.Contains(physicalKeyColumn(identity.LookupKey.Identifier), evidence.PrimaryKeyColumns);
        Assert.DoesNotContain(physicalKeyColumn(identity.OriginalId.Identifier), evidence.PrimaryKeyColumns);
        Assert.DoesNotContain(physicalKeyColumn(identity.ComparisonKey.Identifier), evidence.PrimaryKeyColumns);
    }

    private static void AssertCollision(
        DocumentIdentityLookupCollisionException exception,
        string requestedId,
        string lookupKey,
        string retainedId = "Retained-Id")
    {
        Assert.Equal("configurationDocument", exception.DocumentKind);
        Assert.Equal(requestedId, exception.RequestedId);
        Assert.Equal(retainedId, exception.RetainedId);
        Assert.Equal(lookupKey, exception.LookupKey);
    }
}

internal sealed class InjectedServerCollectionWriteException : Exception;

internal sealed class EphemeralServerDatabase(
    string connectionString,
    Func<ValueTask> disposeAsync) : IAsyncDisposable
{
    public string ConnectionString { get; } = connectionString;
    public ValueTask DisposeAsync() => disposeAsync();
}

public sealed class RelationalServerIdentityFixture(
    RelationalPhysicalDocumentStore store,
    IBoundedDocumentStore queries,
    ExecutableStorageRoute route,
    bool synchronizeAfterPrimaryLock,
    Func<string, Task> corruptPrimaryLookupAsync,
    Func<string, string, Task> corruptLinkedIdentityAsync,
    Func<bool, Task<RelationalIdentitySchemaEvidence>> readIdentitySchemaAsync,
    Func<bool, Task> dropComparisonEvidenceAsync,
    Func<Task> restartAsync,
    Func<string, string> physicalKeyColumn,
    Func<ValueTask> disposeAsync) : IAsyncDisposable
{
    public RelationalPhysicalDocumentStore Store { get; } = store;
    public IDocumentStore Documents => Store;
    public IBoundedDocumentStore Queries { get; } = queries;
    public ExecutableStorageRoute Route { get; } = route;
    internal RelationalPhysicalWriteExecutionPoint RaceSynchronizationPoint { get; } = synchronizeAfterPrimaryLock
        ? RelationalPhysicalWriteExecutionPoint.AfterPrimaryLock
        : RelationalPhysicalWriteExecutionPoint.BeforePrimaryLock;
    public Func<string, Task> CorruptPrimaryLookupAsync { get; } = corruptPrimaryLookupAsync;
    public Func<string, string, Task> CorruptLinkedIdentityAsync { get; } = corruptLinkedIdentityAsync;
    public Func<bool, Task<RelationalIdentitySchemaEvidence>> ReadIdentitySchemaAsync { get; } = readIdentitySchemaAsync;
    public Func<bool, Task> DropComparisonEvidenceAsync { get; } = dropComparisonEvidenceAsync;
    public Func<Task> RestartAsync { get; } = restartAsync;
    public Func<string, string> PhysicalKeyColumn { get; } = physicalKeyColumn;
    public ValueTask DisposeAsync() => disposeAsync();
}

public sealed record RelationalIdentitySchemaEvidence(
    IReadOnlySet<string> Columns,
    IReadOnlyList<string> PrimaryKeyColumns);

public sealed class RelationalServerLinkedBackfillCollisionFixture(
    IDocumentStore initialDocuments,
    ExecutableStorageRoute route,
    Func<string, string, string, Task> setLinkedIdentityAsync,
    Func<Task<PhysicalSchemaApplicationOutcome>> applyAdditiveAsync,
    Func<Task<IReadOnlyList<int?>>> readPriorityValuesAsync,
    Func<ValueTask> disposeAsync) : IAsyncDisposable
{
    public IDocumentStore InitialDocuments { get; } = initialDocuments;
    public ExecutableStorageRoute Route { get; } = route;
    public Func<string, string, string, Task> SetLinkedIdentityAsync { get; } = setLinkedIdentityAsync;
    public Func<Task<PhysicalSchemaApplicationOutcome>> ApplyAdditiveAsync { get; } = applyAdditiveAsync;
    public Func<Task<IReadOnlyList<int?>>> ReadPriorityValuesAsync { get; } = readPriorityValuesAsync;
    public ValueTask DisposeAsync() => disposeAsync();
}
