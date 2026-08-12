using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Relational.Documents;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Groundwork.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteRelationalPhysicalStorageConformanceTests : PhysicalStorageConformance
{
    [Fact]
    public async Task Sort_only_index_field_residual_filters_before_cursor_limit_and_binds_continuation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var instance = Guid.NewGuid().ToString("N")[..8];
        var manifest = SortOnlyResidualPredicateConformance.CreateManifest(instance);
        var target = SortOnlyResidualPredicateConformance.CreateTarget(
            manifest,
            SqliteTestManifests.Provider,
            ProviderPhysicalNameNormalizer.Identity,
            instance);
        await PhysicalSchemaApplication.ApplyAsync(
            target,
            new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        var store = new SqlitePhysicalDocumentStore(
            connection,
            manifest,
            target.Routes,
            DocumentStoreAccess.Global);
        var runtime = SqlitePhysicalQueryRuntime.Create(
            store,
            manifest,
            route,
            target.Provider);

        await SortOnlyResidualPredicateConformance.VerifyAsync(store, runtime);
    }

    /// <summary>
    /// Every relational provider renders the predicate through the same handler, so proving the
    /// complement here proves it for SQL Server and PostgreSQL too; the MongoDB half is asserted by the
    /// same conformance in its own suite.
    /// </summary>
    [Fact]
    public async Task Not_equal_is_the_complement_of_equal_including_rows_with_no_value()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var instance = Guid.NewGuid().ToString("N")[..8];
        var manifest = NotEqualNullSemanticsConformance.CreateManifest(instance);
        var target = NotEqualNullSemanticsConformance.CreateTarget(
            manifest,
            SqliteTestManifests.Provider,
            ProviderPhysicalNameNormalizer.Identity,
            instance);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        var store = new SqlitePhysicalDocumentStore(
            connection,
            manifest,
            target.Routes,
            DocumentStoreAccess.Global);
        var runtime = SqlitePhysicalQueryRuntime.Create(store, manifest, route, target.Provider);

        await NotEqualNullSemanticsConformance.VerifyAsync(store, runtime);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Caller_cancellation_after_non_success_cannot_prevent_rollback(
        PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var model = SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority: true);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlitePhysicalSchemaExecutor(connection));
        using var cancellation = new CancellationTokenSource();
        var store = new RelationalPhysicalDocumentStore(
            connection,
            model.Manifest,
            model.Target.Routes,
            new SqlitePhysicalDocumentDialect(),
            DocumentStoreAccess.Global,
            _ =>
            {
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            });
        await using var transaction = await store.BeginAsync(
            DocumentCommitScope.Of("configurationDocument"));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await transaction.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "staged-before-cancellation",
            "1",
            "{\"category\":\"tools\",\"priority\":1}",
            ExpectedVersion: 0))).Status);

        var nonSuccess = await transaction.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "missing",
            "1",
            "{\"category\":\"tools\",\"priority\":1}",
            ExpectedVersion: 1), cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(DocumentStoreWriteStatus.NotFound, nonSuccess.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync());
        Assert.Null(await store.LoadAsync("configurationDocument", "staged-before-cancellation"));
    }

    protected override async Task<PhysicalStorageFixture> CreateAsync(
        PhysicalStorageForm form,
        bool dedicatedWithoutLinked = false)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var model = dedicatedWithoutLinked ? CreateDedicatedWithoutLinked() : SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority: true);
            await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlitePhysicalSchemaExecutor(connection));
            var route = model.Target.Routes.Single();
            var store = new SqlitePhysicalDocumentStore(
                connection,
                model.Manifest,
                model.Target.Routes,
                DocumentStoreAccess.Global);
            var queries = dedicatedWithoutLinked
                ? null
                : SqlitePhysicalQueryRuntime.Create(store, model.Manifest, route, model.Target.Provider);
            return new PhysicalStorageFixture(
                store,
                queries,
                route,
                dedicatedWithoutLinked ? () => Task.FromResult(string.Empty) : () => ExplainCategoryLookupAsync(connection, route),
                connection.DisposeAsync);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    protected override async Task<UnfilteredGlobalQueryFixture> CreateUnfilteredGlobalIdQueryAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var model = CreateUnfilteredGlobalIdQueryModel(
                SqliteTestManifests.Provider,
                ProviderPhysicalNameNormalizer.Identity,
                Guid.NewGuid().ToString("N")[..8]);
            await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlitePhysicalSchemaExecutor(connection));
            var route = model.Target.Routes.Single();
            var store = new SqlitePhysicalDocumentStore(
                connection,
                model.Manifest,
                model.Target.Routes,
                DocumentStoreAccess.Global);
            return new UnfilteredGlobalQueryFixture(
                store,
                SqlitePhysicalQueryRuntime.Create(store, model.Manifest, route, model.Target.Provider),
                route,
                connection.DisposeAsync);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    protected override void AssertUnfilteredGlobalIdQueryPlan(PhysicalDocumentQueryExplanation explanation)
    {
        Assert.All(explanation.Commands, command =>
        {
            Assert.Equal("sqlite-query-plan", command.NativePlanFormat);
            Assert.False(string.IsNullOrWhiteSpace(command.NativePlan));
        });
        var page = explanation.Commands.Single(command =>
            command.Kind == PhysicalDocumentQueryCommandKind.Page);
        Assert.Contains(explanation.Plan.IndexName!.Identifier, page.NativePlan, StringComparison.Ordinal);
        Assert.DoesNotContain("USE TEMP B-TREE FOR ORDER BY", page.NativePlan, StringComparison.Ordinal);
    }

    protected override async Task<CursorPagingFixture> CreateCursorPagingAsync(PhysicalStorageForm form)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var model = SqlitePhysicalSchemaExecutorTests.CreateModel(
                form,
                includePriority: false,
                categoryPaging: QueryPagingSupport.Cursor);
            await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlitePhysicalSchemaExecutor(connection));
            var route = model.Target.Routes.Single();
            SqlitePhysicalDocumentStore OpenStore() => new(
                connection,
                model.Manifest,
                model.Target.Routes,
                DocumentStoreAccess.Global);
            return new CursorPagingFixture(
                OpenStore(),
                () => SqlitePhysicalQueryRuntime.Create(OpenStore(), model.Manifest, route, model.Target.Provider),
                route,
                connection.DisposeAsync);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    protected override async Task<ScopedPhysicalStorageFixture> CreateScopedAsync(PhysicalStorageForm form)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var model = SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority: true, scoped: true);
            await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlitePhysicalSchemaExecutor(connection));
            return new ScopedPhysicalStorageFixture(
                access => new SqlitePhysicalDocumentStore(connection, model.Manifest, model.Target.Routes, access),
                connection.DisposeAsync);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    protected override async Task<PhysicalStorageEvolutionFixture> CreateEvolutionAsync(PhysicalStorageForm form)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var initial = SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority: false);
            var additive = SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority: true);
            var executor = new SqlitePhysicalSchemaExecutor(connection);
            await PhysicalSchemaApplication.ApplyAsync(initial.Target, executor);
            var initialDocuments = new SqlitePhysicalDocumentStore(
                connection,
                initial.Manifest,
                initial.Target.Routes,
                DocumentStoreAccess.Global);
            return new PhysicalStorageEvolutionFixture(
                initialDocuments,
                async () =>
                {
                    await PhysicalSchemaApplication.ApplyAsync(additive.Target, executor);
                    var route = additive.Target.Routes.Single();
                    var store = new SqlitePhysicalDocumentStore(
                        connection,
                        additive.Manifest,
                        additive.Target.Routes,
                        DocumentStoreAccess.Global);
                    return new PhysicalStorageFixture(
                        store,
                        SqlitePhysicalQueryRuntime.Create(store, additive.Manifest, route, additive.Target.Provider),
                        route,
                        () => ExplainCategoryLookupAsync(connection, route),
                        () => ValueTask.CompletedTask);
                },
                async () => (await PhysicalSchemaApplication.ApplyAsync(additive.Target, executor)).Outcome,
                async cancellationToken =>
                    await executor.AcquireApplicationLockAsync(additive.Target.Identity, cancellationToken),
                connection.DisposeAsync);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<string> ExplainCategoryLookupAsync(SqliteConnection connection, ExecutableStorageRoute route)
    {
        var category = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "category");
        var table = category.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        var scope = category.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.ScopeKey.Column.Identifier
            : route.LinkedRelationship!.StorageScope.Identifier;
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"EXPLAIN QUERY PLAN SELECT * FROM \"{table}\" WHERE \"{scope}\" = @scope AND \"{category.Column.Identifier}\" = @category;";
        command.Parameters.AddWithValue("@scope", "__groundwork_global__");
        command.Parameters.AddWithValue("@category", "tools");
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            details.Add(reader.GetString(3));
        return string.Join(Environment.NewLine, details);
    }

    private static (StorageManifest Manifest, PhysicalSchemaTarget Target) CreateDedicatedWithoutLinked()
    {
        var template = SqliteTestManifests.MetadataManifest();
        var manifest = template with
        {
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        StorageUnitProvisioningMode.Declared,
                        PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.DedicatedDocumentTable("configuration_documents")))
                }
            ]
        };
        var resolution = PhysicalStorageResolver.Resolve(manifest, PhysicalNamePolicy.Identity, ProviderPhysicalNameNormalizer.Identity);
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        return (manifest, new PhysicalSchemaTarget(manifest.Identity, manifest.Version, SqliteTestManifests.Provider, compilation.Routes));
    }
}
