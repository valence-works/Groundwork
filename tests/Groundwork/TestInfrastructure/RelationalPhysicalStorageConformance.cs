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
/// Provider-neutral black-box contract for route-driven relational physical storage. SQL Server
/// and PostgreSQL provider slices inherit this suite and supply only their connection/materializer
/// fixture, keeping behavior assertions identical to the SQLite reference.
/// </summary>
public abstract class RelationalPhysicalStorageConformance
{
    protected abstract Task<RelationalPhysicalStorageFixture> CreateAsync(
        PhysicalStorageForm form,
        bool dedicatedWithoutLinked = false);

    protected abstract Task<RelationalScopedPhysicalStorageFixture> CreateScopedAsync(PhysicalStorageForm form);

    protected abstract Task<RelationalPhysicalStorageEvolutionFixture> CreateEvolutionAsync(PhysicalStorageForm form);

    protected abstract Task<RelationalUnfilteredGlobalQueryFixture> CreateUnfilteredGlobalIdQueryAsync();

    protected virtual Task PrepareUnfilteredGlobalIdQueryPlanAsync(RelationalUnfilteredGlobalQueryFixture fixture) =>
        Task.CompletedTask;

    protected abstract void AssertUnfilteredGlobalIdQueryPlan(PhysicalDocumentQueryExplanation explanation);

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
        Assert.Equal(PhysicalQueryAccessKind.PrimaryEnvelope, explanation.Plan.AccessKind);
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
            MissingValueBehavior.Excluded);
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
            [],
            [],
            PhysicalizationPolicy.Portable)
        {
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [index],
                [query])
        };
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

public sealed class RelationalPhysicalStorageFixture(
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

public sealed class RelationalScopedPhysicalStorageFixture(
    Func<DocumentStoreAccess, IDocumentStore> open,
    Func<ValueTask> disposeAsync) : IAsyncDisposable
{
    public IDocumentStore Open(DocumentStoreAccess access) => open(access);
    public ValueTask DisposeAsync() => disposeAsync();
}

public sealed class RelationalPhysicalStorageEvolutionFixture(
    IDocumentStore initialDocuments,
    Func<Task<RelationalPhysicalStorageFixture>> applyAdditiveAsync,
    Func<Task<PhysicalSchemaApplicationOutcome>> restartAsync,
    Func<CancellationToken, ValueTask<IAsyncDisposable>> acquireApplicationLockAsync,
    Func<ValueTask> disposeAsync) : IAsyncDisposable
{
    public IDocumentStore InitialDocuments { get; } = initialDocuments;
    public Func<Task<RelationalPhysicalStorageFixture>> ApplyAdditiveAsync { get; } = applyAdditiveAsync;
    public Func<Task<PhysicalSchemaApplicationOutcome>> RestartAsync { get; } = restartAsync;
    public Func<CancellationToken, ValueTask<IAsyncDisposable>> AcquireApplicationLockAsync { get; } = acquireApplicationLockAsync;
    public ValueTask DisposeAsync() => disposeAsync();
}

public sealed class RelationalUnfilteredGlobalQueryFixture(
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
