using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Scoping;
using Groundwork.Core.Manifests;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Provider.Relational;
using Groundwork.Relational.Documents;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Groundwork.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqlitePhysicalDocumentStoreTests
{
    [Fact]
    public async Task OnlyPrimaryKeyAndUniqueExtendedConstraintCodesAreClassifiedAsConcurrency()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE constraint_probe (id INTEGER PRIMARY KEY, value TEXT NOT NULL UNIQUE);";
            await create.ExecuteNonQueryAsync();
        }
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = "INSERT INTO constraint_probe (id, value) VALUES (1, 'one');";
            await seed.ExecuteNonQueryAsync();
        }
        var dialect = new SqlitePhysicalDocumentDialect();

        var unique = await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO constraint_probe (id, value) VALUES (2, 'one');";
            await command.ExecuteNonQueryAsync();
        });
        var notNull = await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO constraint_probe (id, value) VALUES (3, NULL);";
            await command.ExecuteNonQueryAsync();
        });

        Assert.Equal(2067, unique.SqliteExtendedErrorCode);
        Assert.True(dialect.IsUniqueConstraintException(unique));
        Assert.Equal(1299, notNull.SqliteExtendedErrorCode);
        Assert.False(dialect.IsUniqueConstraintException(notNull));
    }

    [Theory]
    [InlineData(StorageIdentityKind.Guid)]
    [InlineData(StorageIdentityKind.Composite)]
    public async Task NonStringIdentityKindsPreserveOrdinalProjection(StorageIdentityKind identityKind)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (template, _) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: false);
        var manifest = template with
        {
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    IdentityPolicy = new IdentityPolicy(identityKind, "id")
                }
            ]
        };
        var target = PhysicalSchemaTargetCompiler.Compile(
            manifest,
            SqliteTestManifests.Provider,
            SqliteGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "A-B", "1", """{"category":"upper"}""", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "a-b", "1", """{"category":"lower"}""", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "A-B", "1", """{"category":"updated"}""", 1))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Deleted, (await store.DeleteAsync(new DeleteDocumentRequest(
            "configurationDocument", "a-b", 1))).Status);
        Assert.Contains("updated", (await store.LoadAsync("configurationDocument", "A-B"))!.ContentJson);
        Assert.Null(await store.LoadAsync("configurationDocument", "a-b"));
    }

    [Fact]
    public async Task RequiredProjectionIsValidatedBeforeSqlDispatch()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
            new SaveDocumentRequest("configurationDocument", "missing-category", "1", """{"priority":1}""", 0)));

        Assert.Contains("category", exception.Message);
        Assert.Null(await store.LoadAsync("configurationDocument", "missing-category"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task CrudOccAndProjectionsFollowTheCompiledRouteAtomically(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);

        var created = await store.SaveAsync(Save("one", "tools", 7, 0));
        var conflict = await store.SaveAsync(Save("one", "wrong", 9, 0));
        var updated = await store.SaveAsync(Save("one", "gadgets", 8, 1));

        Assert.Equal(DocumentStoreWriteStatus.Saved, created.Status);
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, conflict.Status);
        Assert.Equal(2, updated.Document!.Version);
        var loaded = await store.LoadAsync("configurationDocument", "one");
        Assert.Equal(updated.Document.ContentJson, loaded!.ContentJson);

        var category = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "category");
        var projectionTable = category.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        Assert.Equal("gadgets", await ScalarAsync(connection,
            $"SELECT \"{category.Column.Identifier}\" FROM \"{projectionTable}\";"));

        var deleted = await store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", "one", 2));
        Assert.Equal(DocumentStoreWriteStatus.Deleted, deleted.Status);
        Assert.Equal("one", deleted.AuthoritativeId);
        Assert.Null(await store.LoadAsync("configurationDocument", "one"));
        if (route.LinkedIndexStorage is not null)
            Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(connection, $"SELECT COUNT(*) FROM \"{route.LinkedIndexStorage.Name.Identifier}\";")));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task UnicodeIgnoreCaseLoadsEquivalentSpellingAndRejectsItsSave(PhysicalStorageForm form)
    {
        await using var harness = await CreateHarnessAsync(
            form,
            StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);

        var saved = await harness.Store.SaveAsync(Save("Configuration-One", "tools", 7, 0));
        var loaded = await harness.Store.LoadAsync("configurationDocument", "configuration-one");
        var conflict = await harness.Store.SaveAsync(Save("configuration-one", "gadgets", 8, 1));

        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        Assert.Equal("Configuration-One", loaded!.Id);
        Assert.Equal(1, loaded.Version);
        Assert.Equal(DocumentStoreWriteStatus.IdentityConflict, conflict.Status);
        Assert.Equal("Configuration-One", conflict.AuthoritativeId);
        Assert.Contains("\"category\":\"tools\"", loaded.ContentJson);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task UnicodeIgnoreCaseDeleteUsesEquivalentSpellingWithoutBypassingOcc(PhysicalStorageForm form)
    {
        await using var harness = await CreateHarnessAsync(
            form,
            StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        await harness.Store.SaveAsync(Save("Configuration-One", "tools", 7, 0));

        var stale = await harness.Store.DeleteAsync(new DeleteDocumentRequest(
            "configurationDocument",
            "configuration-one",
            ExpectedVersion: 2));
        var deleted = await harness.Store.DeleteAsync(new DeleteDocumentRequest(
            "configurationDocument",
            "configuration-one",
            ExpectedVersion: 1));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, stale.Status);
        Assert.Equal(DocumentStoreWriteStatus.Deleted, deleted.Status);
        Assert.Equal("Configuration-One", deleted.AuthoritativeId);
        Assert.Null(await harness.Store.LoadAsync("configurationDocument", "Configuration-One"));
    }

    [Fact]
    public async Task UnicodeIgnoreCaseSupportsSupplementaryPlaneIdentitySpelling()
    {
        await using var harness = await CreateHarnessAsync(
            PhysicalStorageForm.PhysicalEntityTable,
            StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        var retained = $"document-{char.ConvertFromUtf32(0x10428)}";
        var equivalent = $"document-{char.ConvertFromUtf32(0x10400)}";

        await harness.Store.SaveAsync(Save(retained, "tools", 7, 0));
        var loaded = await harness.Store.LoadAsync("configurationDocument", equivalent);
        var conflict = await harness.Store.SaveAsync(Save(equivalent, "gadgets", 8, 1));

        Assert.Equal(retained, loaded!.Id);
        Assert.Equal(DocumentStoreWriteStatus.IdentityConflict, conflict.Status);
        Assert.Equal(retained, conflict.AuthoritativeId);
    }

    [Fact]
    public async Task LookupCollisionEvidenceFailsLoadSaveAndDeleteClosed()
    {
        await using var harness = await CreateHarnessAsync(PhysicalStorageForm.PhysicalEntityTable);
        var route = harness.Route;
        await harness.Store.SaveAsync(Save("Retained-Id", "tools", 7, 0));
        var requestedId = "Requested-Id";
        var requestedLookup = route.Envelope.Identity.Project(requestedId).LookupKey;
        await using (var corrupt = harness.Connection.CreateCommand())
        {
            corrupt.CommandText =
                $"UPDATE \"{route.PrimaryStorage.Name.Identifier}\" " +
                $"SET \"{route.Envelope.Identity.LookupKey.Identifier}\" = @lookup;";
            corrupt.Parameters.AddWithValue("@lookup", requestedLookup);
            await corrupt.ExecuteNonQueryAsync();
        }

        var load = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(
            () => harness.Store.LoadAsync("configurationDocument", requestedId));
        var save = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(
            () => harness.Store.SaveAsync(Save(requestedId, "gadgets", 8, 0)));
        var delete = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(
            () => harness.Store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", requestedId)));

        AssertCollision(load, requestedId, requestedLookup);
        AssertCollision(save, requestedId, requestedLookup);
        AssertCollision(delete, requestedId, requestedLookup);
    }

    [Fact]
    public async Task LinkedLookupCollisionEvidenceFailsSaveClosed()
    {
        await using var harness = await CreateHarnessAsync(PhysicalStorageForm.SharedDocuments);
        var route = harness.Route;
        const string requestedId = "Requested-Id";
        await harness.Store.SaveAsync(Save(requestedId, "tools", 7, 0));
        var requestedLookup = route.LinkedRelationship!.Identity.Project(requestedId).LookupKey;
        await using (var corrupt = harness.Connection.CreateCommand())
        {
            corrupt.CommandText =
                $"UPDATE \"{route.LinkedIndexStorage!.Name.Identifier}\" SET " +
                $"\"{route.LinkedRelationship.DocumentId.Identifier}\" = 'Collision-Retained', " +
                $"\"{route.LinkedRelationship.Identity.ComparisonKey.Identifier}\" = 'different-comparison';";
            await corrupt.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(
            () => harness.Store.SaveAsync(Save(requestedId, "gadgets", 8, 1)));

        Assert.Equal("configurationDocument", exception.DocumentKind);
        Assert.Equal(requestedId, exception.RequestedId);
        Assert.Equal("Collision-Retained", exception.RetainedId);
        Assert.Equal(requestedLookup, exception.LookupKey);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task UnitOfWorkCommitsOrRollsBackEnvelopeAndProjectionTogether(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);

        await using (var rollback = await store.BeginAsync(DocumentCommitScope.Of("configurationDocument")))
        {
            await rollback.SaveAsync(Save("rolled-back", "tools", 1));
            await rollback.RollbackAsync();
        }
        await using (var commit = await store.BeginAsync(DocumentCommitScope.Of("configurationDocument")))
        {
            await commit.SaveAsync(Save("committed", "tools", 1));
            await commit.CommitAsync();
        }

        Assert.Null(await store.LoadAsync("configurationDocument", "rolled-back"));
        Assert.NotNull(await store.LoadAsync("configurationDocument", "committed"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    public async Task FailedLinkedMutationAbortsTheUnitOfWorkBeforePartialStateCanBeCommitted(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            form,
            includePriority: true,
            categoryUnique: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        await store.SaveAsync(Save("owner", "tools", 1, 0));
        await store.SaveAsync(Save("candidate", "gadgets", 2, 0));

        await using var unitOfWork = await store.BeginAsync(DocumentCommitScope.Of("configurationDocument"));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await unitOfWork.SaveAsync(Save("earlier", "other", 3, 0))).Status);
        Assert.Equal(
            DocumentStoreWriteStatus.ConcurrencyConflict,
            (await unitOfWork.SaveAsync(Save("candidate", "tools", 2, 1))).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.CommitAsync());

        Assert.Null(await store.LoadAsync("configurationDocument", "earlier"));
        Assert.Contains("\"category\":\"gadgets\"", (await store.LoadAsync("configurationDocument", "candidate"))!.ContentJson);
        var route = target.Routes.Single();
        var category = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "category");
        Assert.Equal("gadgets", await ScalarAsync(
            connection,
            $"SELECT \"{category.Column.Identifier}\" FROM \"{route.LinkedIndexStorage!.Name.Identifier}\" " +
            $"WHERE \"{route.LinkedRelationship!.DocumentId.Identifier}\" = 'candidate';"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task ScopeAndUniqueIndexesIsolateTheSameIdentityAndValue(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            form,
            includePriority: true,
            scoped: true,
            categoryUnique: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var tenantA = new SqlitePhysicalDocumentStore(
            connection, manifest, target.Routes, DocumentStoreAccess.Scoped(new StorageScope("tenant-a")));
        var tenantB = new SqlitePhysicalDocumentStore(
            connection, manifest, target.Routes, DocumentStoreAccess.Scoped(new StorageScope("tenant-b")));

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await tenantA.SaveAsync(Save("same", "tools", 1, 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await tenantB.SaveAsync(Save("same", "tools", 2, 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, (await tenantA.SaveAsync(Save("other", "tools", 3, 0))).Status);

        Assert.Contains("\"priority\":1", (await tenantA.LoadAsync("configurationDocument", "same"))!.ContentJson);
        Assert.Contains("\"priority\":2", (await tenantB.LoadAsync("configurationDocument", "same"))!.ContentJson);
    }

    [Fact]
    public async Task DedicatedDocumentStorageDoesNotRequireALinkedTable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
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
        Assert.True(resolution.IsValid, string.Join("; ", resolution.Diagnostics.Select(x => x.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(compilation.IsValid, string.Join("; ", compilation.Diagnostics.Select(x => x.Message)));
        var target = new PhysicalSchemaTarget(manifest.Identity, manifest.Version, SqliteTestManifests.Provider, compilation.Routes);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        Assert.Null(route.LinkedIndexStorage);
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(Save("one", "tools", 1, 0))).Status);
        Assert.NotNull(await store.LoadAsync("configurationDocument", "one"));
    }

    [Fact]
    public async Task StatelessFacadeOwnsOneSerializedConnectionPerOperationAndOnePerUnitOfWork()
    {
        var database = Path.Combine(Path.GetTempPath(), $"groundwork-physical-sessions-{Guid.NewGuid():N}.db");
        var connections = new List<SqliteConnection>();
        var overlappingSessionObserved = false;
        try
        {
            var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
                PhysicalStorageForm.PhysicalEntityTable,
                includePriority: true);
            await using (var materializationConnection = new SqliteConnection($"Data Source={database}"))
            {
                await materializationConnection.OpenAsync();
                await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(materializationConnection));
            }
            var sessions = RelationalSessionFactory.Serialized(() =>
            {
                lock (connections)
                {
                    overlappingSessionObserved |= connections.Any(connection => connection.State == System.Data.ConnectionState.Open);
                    var connection = new SqliteConnection($"Data Source={database}");
                    connections.Add(connection);
                    return connection;
                }
            });
            var store = new SqlitePhysicalDocumentStore(
                sessions,
                manifest,
                target.Routes,
                DocumentStoreAccess.Global);

            await store.SaveAsync(Save("one", "tools", 1, 0));
            await store.LoadAsync("configurationDocument", "one");
            var queries = SqlitePhysicalQueryRuntime.Create(store, manifest, target.Routes.Single(), target.Provider);
            Assert.Equal(1, (await queries.QueryAsync(new DocumentQuery(
                "configurationDocument",
                "list-by-category",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))]))).TotalCount);
            Assert.Equal(3, connections.Count);
            Assert.All(connections, connection => Assert.Equal(System.Data.ConnectionState.Closed, connection.State));

            var beforeUnitOfWork = connections.Count;
            await using var unitOfWork = await store.BeginAsync(DocumentCommitScope.Of("configurationDocument"));
            await unitOfWork.SaveAsync(Save("two", "tools", 2, 0));
            await unitOfWork.SaveAsync(Save("three", "tools", 3, 0));
            Assert.Equal(beforeUnitOfWork + 1, connections.Count);
            Assert.Equal(System.Data.ConnectionState.Open, connections[^1].State);
            await unitOfWork.CommitAsync();
            Assert.Equal(System.Data.ConnectionState.Closed, connections[^1].State);
            Assert.False(overlappingSessionObserved);
        }
        finally
        {
            File.Delete(database);
        }
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=:MEMORY:")]
    [InlineData("Data Source=file::memory:")]
    [InlineData("Data Source=file::memory:?cache=shared")]
    [InlineData("Data Source=file:groundwork.db?mode=memory")]
    [InlineData("Data Source=file:groundwork.db?MODE=MEMORY&cache=shared")]
    [InlineData("Data Source=groundwork.db;Mode=Memory")]
    public void StatelessSqliteFacadeRejectsPrivateInMemoryStorage(string connectionString)
    {
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true);

        var exception = Assert.Throws<ArgumentException>(() => new SqlitePhysicalDocumentStore(
            connectionString,
            manifest,
            target.Routes,
            DocumentStoreAccess.Global));

        Assert.Equal("connectionString", exception.ParamName);
        Assert.Contains("direct-connection constructor", exception.Message);
    }

    [Theory]
    [InlineData("Cache=Shared")]
    [InlineData("Data Source=")]
    [InlineData("Data Source=   ")]
    public void StatelessSqliteFacadeRejectsAnEmptyOrWhitespaceDataSource(string connectionString)
    {
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true);

        var exception = Assert.Throws<ArgumentException>(() => new SqlitePhysicalDocumentStore(
            connectionString,
            manifest,
            target.Routes,
            DocumentStoreAccess.Global));

        Assert.Equal("connectionString", exception.ParamName);
        Assert.Contains("non-empty file-backed data source", exception.Message);
    }

    [Fact]
    public async Task StatelessSqliteFacadeAcceptsAFilePathWhoseTextContainsModeMemory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"groundwork-memory-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "mode=memory.db"),
                Pooling = false
            }.ConnectionString;
            Assert.Contains("mode=memory", connectionString, StringComparison.OrdinalIgnoreCase);
            var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
                PhysicalStorageForm.PhysicalEntityTable,
                includePriority: true);
            await using (var materializationConnection = new SqliteConnection(connectionString))
            {
                await materializationConnection.OpenAsync();
                await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(materializationConnection));
            }

            var store = new SqlitePhysicalDocumentStore(
                connectionString,
                manifest,
                target.Routes,
                DocumentStoreAccess.Global);

            Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(Save("one", "tools", 1, 0))).Status);
            Assert.NotNull(await store.LoadAsync("configurationDocument", "one"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReusableKernelSupportsConcurrentPerOperationSessionsWithoutRetainingConnections()
    {
        var database = Path.Combine(Path.GetTempPath(), $"groundwork-physical-pool-{Guid.NewGuid():N}.db");
        var connections = new List<SqliteConnection>();
        try
        {
            var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
                PhysicalStorageForm.PhysicalEntityTable,
                includePriority: true);
            await using (var materializationConnection = new SqliteConnection($"Data Source={database}"))
            {
                await materializationConnection.OpenAsync();
                await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(materializationConnection));
                var seed = new SqlitePhysicalDocumentStore(
                    materializationConnection, manifest, target.Routes, DocumentStoreAccess.Global);
                await seed.SaveAsync(Save("one", "tools", 1, 0));
            }
            var sessions = RelationalSessionFactory.Concurrent(() =>
            {
                var connection = new SqliteConnection($"Data Source={database}");
                lock (connections)
                    connections.Add(connection);
                return connection;
            });
            var store = new SqlitePhysicalDocumentStore(
                sessions, manifest, target.Routes, DocumentStoreAccess.Global);

            var loaded = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(_ => store.LoadAsync("configurationDocument", "one")));

            Assert.All(loaded, Assert.NotNull);
            Assert.Equal(20, connections.Count);
            Assert.All(connections, connection => Assert.Equal(System.Data.ConnectionState.Closed, connection.State));
        }
        finally
        {
            File.Delete(database);
        }
    }

    [Fact]
    public async Task ConcurrentFactoryReusesAdmittedTargetAcrossParallelOperations()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"groundwork-concurrent-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var connectionString = $"Data Source={Path.Combine(directory, "groundwork.db")}";
        try
        {
            var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
                PhysicalStorageForm.PhysicalEntityTable,
                includePriority: true);
            await using (var admissionConnection = new SqliteConnection(connectionString))
            {
                await PhysicalSchemaApplication.ApplyAsync(
                    target,
                    new SqlitePhysicalSchemaExecutor(admissionConnection));
            }
            var store = SqlitePhysicalDocumentStore.CreateConcurrent(
                connectionString,
                manifest,
                target,
                DocumentStoreAccess.Global);
            Assert.Equal(
                DocumentStoreWriteStatus.Saved,
                (await store.SaveAsync(Save("one", "tools", 1, 0))).Status);

            var loaded = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(_ => store.LoadAsync("configurationDocument", "one")));

            Assert.All(loaded, Assert.NotNull);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedStatelessUnitOfWorkRollsBackAndReleasesItsOwnedSession()
    {
        var database = Path.Combine(Path.GetTempPath(), $"groundwork-physical-uow-{Guid.NewGuid():N}.db");
        var connections = new List<SqliteConnection>();
        try
        {
            var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
                PhysicalStorageForm.DedicatedDocumentTable,
                includePriority: true,
                categoryUnique: true);
            await using (var materializationConnection = new SqliteConnection($"Data Source={database}"))
            {
                await materializationConnection.OpenAsync();
                await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(materializationConnection));
            }
            var sessions = RelationalSessionFactory.Serialized(() =>
            {
                var connection = new SqliteConnection($"Data Source={database}");
                connections.Add(connection);
                return connection;
            });
            var store = new SqlitePhysicalDocumentStore(
                sessions, manifest, target.Routes, DocumentStoreAccess.Global);
            await store.SaveAsync(Save("owner", "tools", 1, 0));
            await store.SaveAsync(Save("candidate", "gadgets", 2, 0));

            await using var unitOfWork = await store.BeginAsync(DocumentCommitScope.Of("configurationDocument"));
            Assert.Equal(DocumentStoreWriteStatus.Saved, (await unitOfWork.SaveAsync(Save("earlier", "other", 3, 0))).Status);
            Assert.Equal(
                DocumentStoreWriteStatus.ConcurrencyConflict,
                (await unitOfWork.SaveAsync(Save("candidate", "tools", 2, 1))).Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.CommitAsync());

            Assert.All(connections, connection => Assert.Equal(System.Data.ConnectionState.Closed, connection.State));
            Assert.Null(await store.LoadAsync("configurationDocument", "earlier"));
            Assert.Contains("\"category\":\"gadgets\"", (await store.LoadAsync("configurationDocument", "candidate"))!.ContentJson);
        }
        finally
        {
            File.Delete(database);
        }
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task UnguardedUpdateWhosePrimaryWriteAffectsZeroRowsIsNotFoundAndRollsBackCompletely(PhysicalStorageForm form)
    {
        await using var harness = await CreateInterceptedHarnessAsync(form);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await harness.Store.SaveAsync(Save("victim", "tools", 1, 0))).Status);
        harness.Store.WriteInterceptor = DeletePrimaryRowsAt(
            RelationalPhysicalWriteExecutionPoint.AfterPrimaryLock,
            RelationalPhysicalWriteOperation.Save,
            harness.Route);

        var result = await harness.Store.SaveAsync(Save("victim", "loser", 9));
        harness.Store.WriteInterceptor = null;

        Assert.Equal(DocumentStoreWriteStatus.NotFound, result.Status);
        var retained = await harness.Store.LoadAsync("configurationDocument", "victim");
        Assert.Equal(1, retained!.Version);
        Assert.Contains("\"category\":\"tools\"", retained.ContentJson);
        Assert.Equal(1, (await harness.Queries.QueryAsync(CategoryQuery("tools"))).TotalCount);
        Assert.Equal(0, (await harness.Queries.QueryAsync(CategoryQuery("loser"))).TotalCount);
        await AssertLinkedCategoryAsync(harness, "tools");
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task UnguardedDeleteWhosePrimaryWriteAffectsZeroRowsIsRefusedAndRollsBackLinkedMaintenance(PhysicalStorageForm form)
    {
        await using var harness = await CreateInterceptedHarnessAsync(form);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await harness.Store.SaveAsync(Save("victim", "tools", 1, 0))).Status);
        harness.Store.WriteInterceptor = DeletePrimaryRowsAt(
            RelationalPhysicalWriteExecutionPoint.AfterPrimaryLock,
            RelationalPhysicalWriteOperation.Delete,
            harness.Route);

        var result = await harness.Store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", "victim"));
        harness.Store.WriteInterceptor = null;

        // A row that vanishes between the in-transaction locked read and the primary delete can only
        // be a lost concurrent race, so the store reports it as a concurrency conflict (an unguarded
        // delete of a document that never existed is NotFound before any write is attempted, which
        // the storage-scope and physical-storage conformances assert). Either way nothing commits:
        // the already-executed linked maintenance is rolled back with the transaction.
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, result.Status);
        var retained = await harness.Store.LoadAsync("configurationDocument", "victim");
        Assert.Equal(1, retained!.Version);
        Assert.Equal(1, (await harness.Queries.QueryAsync(CategoryQuery("tools"))).TotalCount);
        await AssertLinkedCategoryAsync(harness, "tools");
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    public async Task DependencyStepFailureDuringLinkedMaintenanceRollsBackThePrimaryMutation(PhysicalStorageForm form)
    {
        await using var harness = await CreateInterceptedHarnessAsync(form);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await harness.Store.SaveAsync(Save("victim", "tools", 1, 0))).Status);
        harness.Store.WriteInterceptor = async (point, operation, connection, transaction, cancellationToken) =>
        {
            if (point != RelationalPhysicalWriteExecutionPoint.AfterPrimaryMutation ||
                operation != RelationalPhysicalWriteOperation.Save)
                return;
            await using var drop = connection.CreateCommand();
            drop.Transaction = transaction;
            drop.CommandText = $"DROP TABLE \"{harness.Route.LinkedIndexStorage!.Name.Identifier}\";";
            await drop.ExecuteNonQueryAsync(cancellationToken);
        };

        await Assert.ThrowsAsync<SqliteException>(() => harness.Store.SaveAsync(Save("victim", "gadgets", 2, 1)));
        harness.Store.WriteInterceptor = null;

        var retained = await harness.Store.LoadAsync("configurationDocument", "victim");
        Assert.Equal(1, retained!.Version);
        Assert.Contains("\"category\":\"tools\"", retained.ContentJson);
        Assert.Equal(1, (await harness.Queries.QueryAsync(CategoryQuery("tools"))).TotalCount);
        Assert.Equal(0, (await harness.Queries.QueryAsync(CategoryQuery("gadgets"))).TotalCount);
        await AssertLinkedCategoryAsync(harness, "tools");
    }

    [Fact]
    public async Task PublicFactoryRejectsIdentityPolicyDriftInAppliedDatabaseState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-identity-drift-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        try
        {
            var options = new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true };
            var ordinal = SqliteTestManifests.MetadataManifest();
            await SqliteDocumentStoreFactory.OpenPhysicalAsync(
                connectionString,
                ordinal,
                SqliteTestManifests.Provider,
                DocumentStoreAccess.Global,
                options: options);

            var unicode = TestManifests.WithUnicodeIdentity(ordinal);
            var exception = await Assert.ThrowsAsync<GroundworkRuntimeSchemaAdmissionException>(() =>
                SqliteDocumentStoreFactory.OpenPhysicalAsync(
                    connectionString,
                    unicode,
                    SqliteTestManifests.Provider,
                    DocumentStoreAccess.Global,
                    options: options));

            Assert.Contains("GW-SCHEMA-006", exception.Message, StringComparison.Ordinal);
            // The rejected admission repaired or re-keyed nothing: the applied ordinal target still
            // opens cleanly afterwards.
            Assert.NotNull(await SqliteDocumentStoreFactory.OpenPhysicalAsync(
                connectionString,
                ordinal,
                SqliteTestManifests.Provider,
                DocumentStoreAccess.Global,
                options: options));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<InterceptedStoreHarness> CreateInterceptedHarnessAsync(PhysicalStorageForm form)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        return new InterceptedStoreHarness(
            connection,
            store,
            SqlitePhysicalQueryRuntime.Create(store, manifest, route, SqliteTestManifests.Provider),
            route);
    }

    private static RelationalPhysicalWriteInterceptor DeletePrimaryRowsAt(
        RelationalPhysicalWriteExecutionPoint targetPoint,
        RelationalPhysicalWriteOperation targetOperation,
        ExecutableStorageRoute route) =>
        async (point, operation, connection, transaction, cancellationToken) =>
        {
            if (point != targetPoint || operation != targetOperation)
                return;
            await using var vanish = connection.CreateCommand();
            vanish.Transaction = transaction;
            vanish.CommandText = $"DELETE FROM \"{route.PrimaryStorage.Name.Identifier}\";";
            await vanish.ExecuteNonQueryAsync(cancellationToken);
        };

    private static async Task AssertLinkedCategoryAsync(InterceptedStoreHarness harness, string expectedCategory)
    {
        if (harness.Route.LinkedIndexStorage is null)
            return;
        var category = harness.Route.ProjectedColumns.Single(column => column.Definition.LogicalName == "category");
        Assert.Equal(expectedCategory, await ScalarAsync(
            harness.Connection,
            $"SELECT \"{category.Column.Identifier}\" FROM \"{harness.Route.LinkedIndexStorage.Name.Identifier}\";"));
    }

    private static DocumentQuery CategoryQuery(string category) => new(
        "configurationDocument",
        "list-by-category",
        [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", category))]);

    private sealed record InterceptedStoreHarness(
        SqliteConnection Connection,
        SqlitePhysicalDocumentStore Store,
        IBoundedDocumentStore Queries,
        ExecutableStorageRoute Route) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private static SaveDocumentRequest Save(string id, string category, int priority, long? expectedVersion = null) =>
        new("configurationDocument", id, "1", $"{{\"category\":\"{category}\",\"priority\":{priority}}}", expectedVersion);

    private static async Task<PhysicalStoreHarness> CreateHarnessAsync(
        PhysicalStorageForm form,
        StringIdentityCasePolicy stringCasePolicy = StringIdentityCasePolicy.Ordinal)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            form,
            includePriority: true,
            stringCasePolicy: stringCasePolicy);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        return new PhysicalStoreHarness(
            connection,
            new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global),
            target.Routes.Single());
    }

    private static void AssertCollision(
        DocumentIdentityLookupCollisionException exception,
        string requestedId,
        string lookupKey)
    {
        Assert.Equal("configurationDocument", exception.DocumentKind);
        Assert.Equal(requestedId, exception.RequestedId);
        Assert.Equal("Retained-Id", exception.RetainedId);
        Assert.Equal(lookupKey, exception.LookupKey);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private sealed record PhysicalStoreHarness(
        SqliteConnection Connection,
        IDocumentStore Store,
        ExecutableStorageRoute Route) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
