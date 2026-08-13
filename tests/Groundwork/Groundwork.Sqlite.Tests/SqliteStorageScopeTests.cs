using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Scoping;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Groundwork.TestInfrastructure;
using Microsoft.Data.Sqlite;
using System.Diagnostics.Metrics;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteStorageScopeTests
{
    [Fact]
    public async Task SatisfiesSharedStorageScopeBlackBoxContract()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var manifest = ScopedManifest();
        var applied = new Dictionary<StorageManifest, PhysicalSchemaTarget>();

        async Task<IDocumentStore> CreateStoreAsync(StorageManifest targetManifest, DocumentStoreAccess access)
        {
            if (!applied.TryGetValue(targetManifest, out var target))
            {
                target = Compile(targetManifest);
                await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
                applied[targetManifest] = target;
            }

            return new SqlitePhysicalDocumentStore(connection, targetManifest, target.Routes, access);
        }

        await StorageScopeDocumentStoreConformance.VerifyAsync(
            manifest,
            CreateStoreAsync,
            (store, targetManifest) => SqlitePhysicalQueryRuntime.Create(
                (SqlitePhysicalDocumentStore)store,
                targetManifest,
                applied[targetManifest].Routes.Single(route =>
                    route.StorageUnit.Value == targetManifest.StorageUnits[0].Identity.Value),
                SqliteTestManifests.Provider));
    }

    [Fact]
    public async Task ScopedIdentitySurvivesProviderRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-scope-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        var manifest = ScopedManifest();
        try
        {
            var options = new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true };
            var first = await SqliteDocumentStoreFactory.OpenPhysicalAsync(
                connectionString,
                manifest,
                SqliteTestManifests.Provider,
                DocumentStoreAccess.Scoped(new StorageScope("tenant-a")),
                options: options);
            await first.SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "restart", "1", """{"key":"restart"}"""));

            var restarted = await SqliteDocumentStoreFactory.OpenPhysicalAsync(
                connectionString,
                manifest,
                SqliteTestManifests.Provider,
                DocumentStoreAccess.Scoped(new StorageScope("tenant-a")),
                options: options);
            var other = await SqliteDocumentStoreFactory.OpenPhysicalAsync(
                connectionString,
                manifest,
                SqliteTestManifests.Provider,
                DocumentStoreAccess.Scoped(new StorageScope("tenant-b")),
                options: options);

            Assert.NotNull(await restarted.LoadAsync("configurationDocument", "restart"));
            Assert.Null(await other.LoadAsync("configurationDocument", "restart"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ScopeLeadsEveryPhysicalKeyAndSynthesizedIndex()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var manifest = ScopedManifest();
        var target = Compile(manifest);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));

        var route = Assert.Single(target.Routes);
        var byKey = route.Indexes.Single(index => index.Identity == "by-key");
        Assert.True(byKey.Definition.IsUnique);
        Assert.Equal("storage_scope", byKey.Definition.Columns[0].ColumnLogicalName);

        var physicalColumns = await ReadIndexColumns(connection, byKey.Name.Identifier);
        Assert.Equal(
            byKey.Columns.Select(column => column.Column.Identifier).ToArray(),
            physicalColumns);
    }

    [Fact]
    public async Task InvalidAndPrivilegedAccessEmitLowCardinalityEvidenceBeforeIo()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var observer = new RecordingObserver();
        var measurements = new List<(string Instrument, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Meter.Name == "Groundwork.Documents.StorageScope")
                currentListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray())));
        listener.Start();
        var globalManifest = SqliteTestManifests.MetadataManifest();
        var wrong = new SqlitePhysicalDocumentStore(
            connection,
            globalManifest,
            Compile(globalManifest).Routes,
            DocumentStoreAccess.Scoped(new StorageScope("tenant-a")),
            observer);

        var exception = await Assert.ThrowsAsync<InvalidStorageScopeAccessException>(() =>
            wrong.LoadAsync("configurationDocument", "secret"));

        // The physical store validates scope before executing any command, though the connection
        // itself opens lazily ahead of the check; assert the rejection evidence, not connection state.
        Assert.Equal(StorageScopeRejectionReason.GlobalAccessRequired, exception.Rejection.Reason);
        Assert.Single(observer.Rejections);

        var scopedManifest = ScopedManifest();
        var scopedRoutes = Compile(scopedManifest).Routes;
        var missingScope = new SqlitePhysicalDocumentStore(
            connection,
            scopedManifest,
            scopedRoutes,
            DocumentStoreAccess.Global,
            observer);
        var scopedRequired = await Assert.ThrowsAsync<InvalidStorageScopeAccessException>(() =>
            missingScope.LoadAsync("configurationDocument", "secret"));
        Assert.Equal(StorageScopeRejectionReason.ScopedAccessRequired, scopedRequired.Rejection.Reason);

        var crossScope = new SqlitePhysicalDocumentStore(
            connection,
            scopedManifest,
            scopedRoutes,
            DocumentStoreAccess.PrivilegedAcrossScopes(new PrivilegedStorageAccess("repair")),
            observer);
        Assert.Single(observer.PrivilegedAcquisitions);
        Assert.Equal("repair", observer.PrivilegedAcquisitions[0].Reason);
        var ambiguous = await Assert.ThrowsAsync<InvalidStorageScopeAccessException>(() =>
            crossScope.LoadAsync("configurationDocument", "secret"));
        Assert.Equal(StorageScopeRejectionReason.TargetScopeRequired, ambiguous.Rejection.Reason);
        Assert.DoesNotContain(observer.Rejections.Select(x => x.ToString()), value => value.Contains("tenant-a", StringComparison.Ordinal));
        Assert.Contains(measurements, measurement => measurement.Instrument == "groundwork.document_store.privileged_sessions");
        Assert.Contains(measurements, measurement => measurement.Instrument == "groundwork.document_store.scope_rejections");
        Assert.DoesNotContain(
            measurements.SelectMany(measurement => measurement.Tags),
            tag => string.Equals(tag.Value?.ToString(), "tenant-a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnitOfWorkRejectsMixedGlobalAndScopedUnitsBeforeIo()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var scoped = ScopedManifest();
        var first = scoped.StorageUnits.Single();
        var mixed = scoped with
        {
            StorageUnits =
            [
                first,
                first with
                {
                    Identity = new StorageUnitIdentity("globalDocument"),
                    Tenancy = TenancyPolicy.Global
                }
            ]
        };
        var store = new SqlitePhysicalDocumentStore(
            connection,
            mixed,
            Compile(mixed).Routes,
            DocumentStoreAccess.Scoped(new StorageScope("tenant-a")));

        var exception = await Assert.ThrowsAsync<InvalidStorageScopeAccessException>(() =>
            store.BeginAsync(DocumentCommitScope.Of("configurationDocument", "globalDocument")));

        Assert.Equal(StorageScopeRejectionReason.MixedUnitOfWorkPolicy, exception.Rejection.Reason);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    private static StorageManifest ScopedManifest()
    {
        var manifest = SqliteTestManifests.MetadataManifest();
        return manifest with
        {
            Identity = new StorageManifestIdentity("scoped.metadata"),
            StorageUnits =
            [
                manifest.StorageUnits.Single() with
                {
                    Tenancy = TenancyPolicy.Scoped
                }
            ]
        };
    }

    private static PhysicalSchemaTarget Compile(StorageManifest manifest) =>
        PhysicalSchemaTargetCompiler.Compile(
            manifest,
            SqliteTestManifests.Provider,
            SqliteGroundworkCapabilities.PhysicalNames);

    private static async Task<IReadOnlyList<string>> ReadIndexColumns(SqliteConnection connection, string indexName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_info({indexName});";
        var columns = new List<(long Order, string Name)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add((reader.GetInt64(0), reader.GetString(2)));
        return columns.OrderBy(x => x.Order).Select(x => x.Name).ToArray();
    }

    private sealed class RecordingObserver : IStorageScopeObserver
    {
        public List<PrivilegedStorageSessionAudit> PrivilegedAcquisitions { get; } = [];
        public List<StorageScopeAccessRejection> Rejections { get; } = [];

        public void PrivilegedSessionAcquired(PrivilegedStorageSessionAudit audit) => PrivilegedAcquisitions.Add(audit);

        public void ScopeAccessRejected(StorageScopeAccessRejection rejection) => Rejections.Add(rejection);
    }
}
