using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Scoping;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Xunit;

namespace Groundwork.TestInfrastructure;

/// <summary>
/// Provider-neutral black-box contract for route-driven physical storage. The SQLite, SQL Server,
/// PostgreSQL, and MongoDB provider slices inherit this suite and supply only their
/// connection/materializer fixture, keeping the behavior assertions identical across providers.
/// </summary>
public abstract class PhysicalStorageConformance
{
    protected abstract Task<PhysicalStorageFixture> CreateAsync(
        PhysicalStorageForm form,
        bool dedicatedWithoutLinked = false);

    protected abstract Task<ScopedPhysicalStorageFixture> CreateScopedAsync(PhysicalStorageForm form);

    protected abstract Task<PhysicalStorageEvolutionFixture> CreateEvolutionAsync(PhysicalStorageForm form);

    protected abstract Task<UnfilteredGlobalQueryFixture> CreateUnfilteredGlobalIdQueryAsync();

    protected abstract Task<CursorPagingFixture> CreateCursorPagingAsync(PhysicalStorageForm form);

    protected virtual Task PrepareUnfilteredGlobalIdQueryPlanAsync(UnfilteredGlobalQueryFixture fixture) =>
        Task.CompletedTask;

    protected abstract void AssertUnfilteredGlobalIdQueryPlan(PhysicalDocumentQueryExplanation explanation);

    /// <summary>
    /// The access kind an explicitly unfiltered global-id route plans with. Relational providers
    /// page the primary envelope directly; MongoDB plans native document fields.
    /// </summary>
    protected virtual PhysicalQueryAccessKind UnfilteredGlobalIdQueryAccessKind =>
        PhysicalQueryAccessKind.PrimaryEnvelope;

    /// <summary>
    /// Provider-specific native-plan assertions for the resumed cursor page. The default asserts
    /// nothing beyond the shared behavioral contract.
    /// </summary>
    protected virtual Task AssertCursorPageExplanationAsync(
        IBoundedDocumentStore queries,
        DocumentQuery middleQuery,
        ExecutableStorageRoute route) => Task.CompletedTask;

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task CrudOccAndBoundedQueriesFollowTheCompiledRoute(PhysicalStorageForm form)
    {
        await using var fixture = await CreateAsync(form);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(Save("b", "tools", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(Save("a", "tools", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, (await fixture.Documents.SaveAsync(Save("a", "other", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(Save("a", "tools", 1))).Status);

        var query = new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))],
            [new DocumentQueryOrder("category")],
            take: 1);
        var page = await fixture.Queries!.QueryAsync(query);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal("a", Assert.Single(page.Documents).Id);
        Assert.Equal(2, await fixture.Queries.CountAsync(query.Select(BoundedQueryResultOperation.Count)));
        Assert.Contains(
            fixture.Route.Indexes.Single(index => index.Identity == "by-category").Name.Identifier,
            await fixture.ExplainCategoryLookupAsync());

        var loaded = await fixture.Documents.LoadAsync("configurationDocument", "a");
        Assert.Equal(2, loaded!.Version);
        Assert.Equal(DocumentStoreWriteStatus.Deleted, (await fixture.Documents.DeleteAsync(
            new DeleteDocumentRequest("configurationDocument", "a", 2))).Status);
        Assert.Null(await fixture.Documents.LoadAsync("configurationDocument", "a"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task StaleExpectedVersionSavesAndDeletesAreConflictsAndMutateNothing(PhysicalStorageForm form)
    {
        await using var fixture = await CreateAsync(form);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(Save("occ", "tools", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(Save("occ", "other", 1))).Status);

        Assert.Equal(
            DocumentStoreWriteStatus.ConcurrencyConflict,
            (await fixture.Documents.SaveAsync(Save("occ", "stale", 1))).Status);
        var loaded = await fixture.Documents.LoadAsync("configurationDocument", "occ");
        Assert.Equal(2, loaded!.Version);
        Assert.Contains("other", loaded.ContentJson);
        Assert.Equal(0, await fixture.Queries!.CountAsync(CategoryCount("stale")));
        Assert.Equal(1, await fixture.Queries.CountAsync(CategoryCount("other")));

        Assert.Equal(
            DocumentStoreWriteStatus.ConcurrencyConflict,
            (await fixture.Documents.DeleteAsync(new DeleteDocumentRequest("configurationDocument", "occ", 1))).Status);
        Assert.NotNull(await fixture.Documents.LoadAsync("configurationDocument", "occ"));
        Assert.Equal(1, await fixture.Queries.CountAsync(CategoryCount("other")));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task PositiveExpectedVersionAgainstAnAbsentDocumentIsNotFoundAndWritesNothing(PhysicalStorageForm form)
    {
        await using var fixture = await CreateAsync(form);

        Assert.Equal(
            DocumentStoreWriteStatus.NotFound,
            (await fixture.Documents.SaveAsync(Save("absent", "phantom", 2))).Status);
        Assert.Null(await fixture.Documents.LoadAsync("configurationDocument", "absent"));
        Assert.Equal(0, await fixture.Queries!.CountAsync(CategoryCount("phantom")));

        Assert.Equal(
            DocumentStoreWriteStatus.NotFound,
            (await fixture.Documents.DeleteAsync(new DeleteDocumentRequest("configurationDocument", "absent", 1))).Status);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task TakeZeroReturnsAnEmptyWindowWithTheFullTotalCount(PhysicalStorageForm form)
    {
        await using var fixture = await CreateAsync(form);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(Save("one", "tools", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(Save("two", "tools", 0))).Status);

        var window = await fixture.Queries!.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))],
            [new DocumentQueryOrder("category")],
            take: 0));

        Assert.Empty(window.Documents);
        Assert.Equal(2, window.TotalCount);
    }

    private static DocumentQuery CategoryCount(string category) => new(
        "configurationDocument",
        "list-by-category",
        [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", category))],
        resultOperation: BoundedQueryResultOperation.Count);

    [Fact]
    public async Task DedicatedDocumentStorageWorksWithoutALinkedObject()
    {
        await using var fixture = await CreateAsync(PhysicalStorageForm.DedicatedDocumentTable, dedicatedWithoutLinked: true);
        Assert.Null(fixture.Route.LinkedIndexStorage);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(Save("one", "tools", 0))).Status);
        Assert.NotNull(await fixture.Documents.LoadAsync("configurationDocument", "one"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task StorageScopeIsPartOfIdentityAcrossAllPhysicalForms(PhysicalStorageForm form)
    {
        await using var fixture = await CreateScopedAsync(form);
        var tenantA = fixture.Open(DocumentStoreAccess.Scoped(new StorageScope("tenant-a")));
        var tenantB = fixture.Open(DocumentStoreAccess.Scoped(new StorageScope("tenant-b")));

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await tenantA.SaveAsync(Save("same-id", "alpha", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await tenantB.SaveAsync(Save("same-id", "beta", 0))).Status);

        Assert.Contains("alpha", (await tenantA.LoadAsync("configurationDocument", "same-id"))!.ContentJson);
        Assert.Contains("beta", (await tenantB.LoadAsync("configurationDocument", "same-id"))!.ContentJson);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task UnitOfWorkCommitAndRollbackRemainAtomicAcrossAllPhysicalForms(PhysicalStorageForm form)
    {
        await using var fixture = await CreateAsync(form);
        await using (var rollback = await fixture.Documents.BeginAsync(DocumentCommitScope.Of("configurationDocument")))
        {
            Assert.Equal(DocumentStoreWriteStatus.Saved, (await rollback.SaveAsync(Save("rolled-back", "tools", 0))).Status);
            await rollback.RollbackAsync();
        }
        await using (var commit = await fixture.Documents.BeginAsync(DocumentCommitScope.Of("configurationDocument")))
        {
            Assert.Equal(DocumentStoreWriteStatus.Saved, (await commit.SaveAsync(Save("committed", "tools", 0))).Status);
            await commit.CommitAsync();
        }

        Assert.Null(await fixture.Documents.LoadAsync("configurationDocument", "rolled-back"));
        Assert.NotNull(await fixture.Documents.LoadAsync("configurationDocument", "committed"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task UnitOfWorkRejectsKindsOutsideItsCommitScopeWithoutBecomingTerminal(PhysicalStorageForm form)
    {
        await using var fixture = await CreateAsync(form);
        await using var transaction = await fixture.Documents.BeginAsync(
            DocumentCommitScope.Of("configurationDocument"));

        await Assert.ThrowsAsync<ArgumentException>(() => transaction.SaveAsync(new SaveDocumentRequest(
            "otherDocument", "outside-save", "1", "{}", ExpectedVersion: 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => transaction.DeleteAsync(new DeleteDocumentRequest(
            "otherDocument", "outside-delete")));
        await Assert.ThrowsAsync<ArgumentException>(() => transaction.LoadAsync("otherDocument", "outside-load"));

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await transaction.SaveAsync(
            Save("inside", "tools", 0))).Status);
        await transaction.CommitAsync();
        Assert.NotNull(await fixture.Documents.LoadAsync("configurationDocument", "inside"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task UnitOfWorkNonSuccessRollsBackAndMakesTheTransactionTerminal(PhysicalStorageForm form)
    {
        await using var fixture = await CreateAsync(form);
        await using var transaction = await fixture.Documents.BeginAsync(
            DocumentCommitScope.Of("configurationDocument"));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await transaction.SaveAsync(
            Save("staged-before-non-success", "tools", 0))).Status);

        Assert.Equal(DocumentStoreWriteStatus.NotFound, (await transaction.SaveAsync(
            Save("missing", "tools", 1))).Status);

        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.LoadAsync(
            "configurationDocument", "staged-before-non-success"));
        Assert.Null(await fixture.Documents.LoadAsync("configurationDocument", "staged-before-non-success"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task AdditiveEvolutionBackfillsRestartsAndUsesAnExclusiveApplicationLock(PhysicalStorageForm form)
    {
        await using var fixture = await CreateEvolutionAsync(form);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.InitialDocuments.SaveAsync(Save("existing", "tools", 0))).Status);

        await using var evolved = await fixture.ApplyAdditiveAsync();
        var count = await evolved.Queries!.CountAsync(new DocumentQuery(
            "configurationDocument",
            "find-by-category-priority",
            [
                DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools")),
                DocumentQueryClause.Of(DocumentQueryComparison.Equal("priority", "1"))
            ],
            resultOperation: BoundedQueryResultOperation.Count));

        Assert.Equal(1, count);
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, await fixture.RestartAsync());
        await using var lease = await fixture.AcquireApplicationLockAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.AcquireApplicationLockAsync(cancellation.Token));
    }

    [Fact]
    public async Task Explicitly_unfiltered_global_id_route_executes_an_indexed_offset_page_and_provider_side_count()
    {
        await using var fixture = await CreateUnfilteredGlobalIdQueryAsync();
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(Save("c", "ignored", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(Save("a", "ignored", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(Save("b", "ignored", 0))).Status);
        await PrepareUnfilteredGlobalIdQueryPlanAsync(fixture);

        var pageQuery = new DocumentQuery(
            "configurationDocument",
            "list-all-by-id",
            [],
            [new DocumentQueryOrder(PhysicalDocumentFieldPaths.Id)],
            skip: 1,
            take: 1);
        var countQuery = new DocumentQuery(
            pageQuery.DocumentKind,
            pageQuery.QueryIdentity,
            [],
            pageQuery.Order,
            resultOperation: BoundedQueryResultOperation.Count);

        var page = await fixture.Queries.QueryAsync(pageQuery);
        var count = await fixture.Queries.CountAsync(countQuery);
        var explanation = await Assert.IsAssignableFrom<IPhysicalDocumentQueryExplainer>(fixture.Queries)
            .ExplainAsync(pageQuery);

        Assert.Equal(count, page.TotalCount);
        Assert.True(count >= 3);
        Assert.Equal("b", Assert.Single(page.Documents).Id);
        Assert.Empty(explanation.Plan.Predicates);
        Assert.Equal(UnfilteredGlobalIdQueryAccessKind, explanation.Plan.AccessKind);
        Assert.Equal(fixture.Route.Indexes.Single().Name, explanation.Plan.IndexName);
        Assert.Equal(
            fixture.Route.Envelope.Identity.ComparisonKey.Identifier,
            Assert.Single(explanation.Plan.Order).Field.Identifier);
        Assert.Equal(
            [PhysicalDocumentQueryCommandKind.Count, PhysicalDocumentQueryCommandKind.Page],
            explanation.Commands.Select(command => command.Kind));
        Assert.All(explanation.Commands, command => Assert.NotEqual(0, command.ProviderAppliedMaximumRows));
        AssertUnfilteredGlobalIdQueryPlan(explanation);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Cursor_pages_resume_by_identity_across_a_reopened_store(PhysicalStorageForm form)
    {
        await using var fixture = await CreateCursorPagingAsync(form);
        foreach (var id in new[] { "c", "a", "b", "d", "e" })
        {
            Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(
                Save(id, "tools", 0))).Status);
        }

        var query = new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))],
            [new DocumentQueryOrder("category")],
            take: 1);
        var first = await fixture.OpenQueries().QueryAsync(query);

        var reopened = fixture.OpenQueries();
        var middleQuery = new DocumentQuery(
            query.DocumentKind,
            query.QueryIdentity,
            query.Clauses,
            query.Order,
            take: 2,
            continuation: first.NextContinuation);
        var middle = await reopened.QueryAsync(middleQuery);
        var final = await reopened.QueryAsync(new DocumentQuery(
            query.DocumentKind,
            query.QueryIdentity,
            query.Clauses,
            query.Order,
            take: 10,
            continuation: middle.NextContinuation));
        var expected = new[] { "a", "b", "c", "d", "e" }
            .OrderBy(id => fixture.Route.Envelope.Identity.Project(id).LookupKey, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected[0], Assert.Single(first.Documents).Id);
        Assert.NotNull(first.NextContinuation);
        Assert.Equal(expected[1..3], middle.Documents.Select(document => document.Id));
        Assert.NotNull(middle.NextContinuation);
        Assert.Equal(expected[3..], final.Documents.Select(document => document.Id));
        Assert.Null(final.NextContinuation);
        Assert.Equal(5, final.TotalCount);
        await AssertCursorPageExplanationAsync(reopened, middleQuery, fixture.Route);
    }

    protected static (StorageManifest Manifest, PhysicalSchemaTarget Target) CreateUnfilteredGlobalIdQueryModel(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        string instance)
    {
        var index = new LogicalIndexDeclaration(
            "by-id",
            [new IndexField(PhysicalDocumentFieldPaths.Id)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var query = new BoundedQueryDeclaration(
            "list-all-by-id",
            index.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true,
            sortFields: [new BoundedQuerySortField(PhysicalDocumentFieldPaths.Id, PhysicalSortDirection.Ascending)],
            predicateFields: []);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "global_documents",
            [new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String)],
            indexes:
            [
                new PhysicalIndexDefinition(
                    index.Identity,
                    [new PhysicalIndexColumnDefinition("id_comparison_key", 0)])
            ]);
        var unit = new StorageUnit(
            new StorageUnitIdentity("configurationDocument"),
            "Configuration document",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Global,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [index],
                [query]));
        var manifest = new StorageManifest(
            new StorageManifestIdentity($"unfiltered-global-id.{instance}"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            []);
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            new DelegatePhysicalNamePolicy(context => $"gw_{instance}_{context.FeatureDefaultLogicalName}"),
            normalizer);
        if (!resolution.IsValid)
            throw new InvalidOperationException(string.Join("; ", resolution.Diagnostics.Select(x => x.Message)));
        var routes = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        if (!routes.IsValid)
            throw new InvalidOperationException(string.Join("; ", routes.Diagnostics.Select(x => x.Message)));

        return (manifest, new PhysicalSchemaTarget(manifest.Identity, manifest.Version, provider, routes.Routes));
    }

    private static SaveDocumentRequest Save(string id, string category, long expectedVersion) =>
        new("configurationDocument", id, "1", $"{{\"category\":\"{category}\",\"priority\":1}}", expectedVersion);
}

public sealed class PhysicalStorageFixture(
    IDocumentStore documents,
    IBoundedDocumentStore? queries,
    ExecutableStorageRoute route,
    Func<Task<string>> explainCategoryLookupAsync,
    Func<ValueTask> disposeAsync) : IAsyncDisposable
{
    public IDocumentStore Documents { get; } = documents;
    public IBoundedDocumentStore? Queries { get; } = queries;
    public ExecutableStorageRoute Route { get; } = route;
    public Func<Task<string>> ExplainCategoryLookupAsync { get; } = explainCategoryLookupAsync;
    public ValueTask DisposeAsync() => disposeAsync();
}

public sealed class ScopedPhysicalStorageFixture(
    Func<DocumentStoreAccess, IDocumentStore> open,
    Func<ValueTask> disposeAsync) : IAsyncDisposable
{
    public IDocumentStore Open(DocumentStoreAccess access) => open(access);
    public ValueTask DisposeAsync() => disposeAsync();
}

public sealed class PhysicalStorageEvolutionFixture(
    IDocumentStore initialDocuments,
    Func<Task<PhysicalStorageFixture>> applyAdditiveAsync,
    Func<Task<PhysicalSchemaApplicationOutcome>> restartAsync,
    Func<CancellationToken, ValueTask<IAsyncDisposable>> acquireApplicationLockAsync,
    Func<ValueTask> disposeAsync) : IAsyncDisposable
{
    public IDocumentStore InitialDocuments { get; } = initialDocuments;
    public Func<Task<PhysicalStorageFixture>> ApplyAdditiveAsync { get; } = applyAdditiveAsync;
    public Func<Task<PhysicalSchemaApplicationOutcome>> RestartAsync { get; } = restartAsync;
    public Func<CancellationToken, ValueTask<IAsyncDisposable>> AcquireApplicationLockAsync { get; } = acquireApplicationLockAsync;
    public ValueTask DisposeAsync() => disposeAsync();
}

public sealed class UnfilteredGlobalQueryFixture(
    IDocumentStore documents,
    IBoundedDocumentStore queries,
    ExecutableStorageRoute route,
    Func<ValueTask> disposeAsync) : IAsyncDisposable
{
    public IDocumentStore Documents { get; } = documents;
    public IBoundedDocumentStore Queries { get; } = queries;
    public ExecutableStorageRoute Route { get; } = route;
    public ValueTask DisposeAsync() => disposeAsync();
}

public sealed class CursorPagingFixture(
    IDocumentStore documents,
    Func<IBoundedDocumentStore> openQueries,
    ExecutableStorageRoute route,
    Func<ValueTask> disposeAsync) : IAsyncDisposable
{
    public IDocumentStore Documents { get; } = documents;

    /// <summary>Opens a query surface over a freshly reopened store each call.</summary>
    public Func<IBoundedDocumentStore> OpenQueries { get; } = openQueries;

    public ExecutableStorageRoute Route { get; } = route;
    public ValueTask DisposeAsync() => disposeAsync();
}
