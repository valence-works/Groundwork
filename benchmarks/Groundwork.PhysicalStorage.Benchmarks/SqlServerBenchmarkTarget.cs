using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Relational.Documents;
using Groundwork.SqlServer;
using Groundwork.SqlServer.Documents;
using Groundwork.SqlServer.PhysicalStorage;
using Microsoft.Data.SqlClient;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed class SqlServerBenchmarkTarget(
    PhysicalStorageForm storageForm,
    string instance,
    string serverConnectionString,
    int migrationDatasetSize,
    string sourceDescription) : RelationalServerBenchmarkTarget(
        BenchmarkProvider.SqlServer,
        storageForm,
        instance,
        migrationDatasetSize)
{
    private readonly string serverConnectionString = serverConnectionString;
    private readonly string databaseName = $"groundwork_bench_{instance}_{storageForm}".ToLowerInvariant();
    private readonly string sourceDescription = sourceDescription;
    private string connectionString = string.Empty;

    public override string ProviderVersion { get; protected set; } = "unknown";
    public override IReadOnlyDictionary<string, string> ProviderConfiguration => new Dictionary<string, string>
    {
        ["source"] = sourceDescription,
        ["database_per_form"] = "true",
        ["pooling"] = new SqlConnectionStringBuilder(serverConnectionString).Pooling.ToString(),
        ["connection_lifetime"] = "per-operation concurrent production sessions",
        ["factory"] = nameof(SqlServerDocumentStoreFactory.OpenPhysicalAsync)
    };

    public override DatabaseSignalTarget SignalTarget => DatabaseSignalTarget.ForSqlServer(databaseName);

    protected override ProviderIdentity GroundworkProvider => SqlServerGroundworkCapabilities.Provider;
    protected override IProviderPhysicalNameNormalizer PhysicalNames => SqlServerGroundworkCapabilities.PhysicalNames;
    protected override string HandlerPrefix => "sqlserver";
    protected override string ConnectionString => connectionString;

    public override async Task<IReadOnlyList<NativePlanEvidence>> CaptureNativePlansAsync(
        IReadOnlyList<BenchmarkPlanRequest> requests,
        NativePlanAssertionMode assertionMode,
        CancellationToken cancellationToken)
    {
        var store = RelationalTenantA;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var fields = BenchmarkModelFactory.FieldBindingFor(Model.Route);
        var evidence = new List<NativePlanEvidence>(requests.Count);
        foreach (var request in requests)
        {
            var indexName = BenchmarkModelFactory.IndexFor(Model.Route, request.Ordered).Name.Identifier;
            var rendered = RenderPlan(request, store);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET STATISTICS XML ON; {rendered.CommandText} SET STATISTICS XML OFF;";
            foreach (var (name, value) in rendered.Parameters)
                command.Parameters.AddWithValue($"@{name}", value ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var plans = await SqlServerShowplanReader.ReadAsync(reader, cancellationToken);
            var plan = plans.SingleOrDefault() ?? throw new InvalidOperationException(
                $"SQL Server returned no native XML plan for {request.Workload}/{request.Operation}.");
            var physicalObject = (Model.Route.LinkedIndexStorage ?? Model.Route.PrimaryStorage).Name.Identifier;
            if (assertionMode == NativePlanAssertionMode.RequireDeclaredIndex &&
                !UsesDeclaredIndex(plan, indexName, physicalObject))
            {
                throw new InvalidOperationException(
                    $"SQL Server native-plan gate rejected {request.Workload}/{request.Operation}.");
            }
            var queryReceipt = NativePlanQueryReceipt.FromRelational(
                request,
                assertionMode,
                rendered.CommandText,
                rendered.Parameters,
                fields);
            evidence.Add(new NativePlanEvidence(
                request,
                Provider, StorageForm, BenchmarkModelFactory.QueryIdentityFor(request.Ordered),
                physicalObject,
                indexName, plan,
                NativePlanEvidenceAssertions.ForSqlServer(assertionMode))
            {
                CommandBinding = new NativePlanCommandBinding(
                    physicalObject,
                    Model.Route.LinkedIndexStorage is null ? "p" : "l",
                    rendered.CommandText,
                    queryReceipt.Shape,
                    queryReceipt)
                {
                    Fields = fields
                }
            });
        }
        return evidence;
    }

    internal static bool UsesDeclaredIndex(
        string nativePlan,
        string indexName,
        string physicalObject)
    {
        try
        {
            SqlServerShowplanReader.EnsureScaleBearingIndex(nativePlan, indexName, physicalObject);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Xml.XmlException)
        {
            return false;
        }
    }

    public override async Task<StorageSnapshot> CaptureStorageAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var tables = new[] { Model.Route.PrimaryStorage.Name.Identifier, Model.Route.LinkedIndexStorage?.Name.Identifier }
            .Where(name => name is not null).Cast<string>().ToArray();
        long totalBytes = 0;
        long indexBytes = 0;
        long primaryRows = 0;
        long linkedRows = 0;
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    COALESCE(SUM(reserved_page_count), 0) * 8192,
                    COALESCE(SUM(CASE WHEN index_id > 0 THEN reserved_page_count ELSE 0 END), 0) * 8192,
                    COALESCE(SUM(CASE WHEN index_id IN (0, 1) THEN row_count ELSE 0 END), 0)
                FROM sys.dm_db_partition_stats
                WHERE object_id = OBJECT_ID(@table);
                """;
            command.Parameters.AddWithValue("@table", table);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            totalBytes += reader.GetInt64(0);
            indexBytes += reader.GetInt64(1);
            if (table == Model.Route.PrimaryStorage.Name.Identifier)
                primaryRows = reader.GetInt64(2);
            else
                linkedRows = reader.GetInt64(2);
        }
        return new StorageSnapshot(totalBytes, indexBytes, primaryRows, linkedRows,
            new Dictionary<string, long> { ["reserved_bytes"] = totalBytes });
    }

    public override async ValueTask DisposeAsync() => await DisposeServerAsync();

    protected override async Task CreateIsolationBoundaryAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(serverConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {Q(databaseName)};";
        await command.ExecuteNonQueryAsync(cancellationToken);
        connectionString = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = databaseName }.ConnectionString;
    }

    protected override async Task DropIsolationBoundaryAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(connectionString))
            return;
        await using var connection = new SqlConnection(serverConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE {Q(databaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Q(databaseName)};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    protected override IPhysicalSchemaExecutor CreateExecutor() => new SqlServerPhysicalSchemaExecutor(ConnectionString);

    protected override async Task<RelationalPhysicalDocumentStore> OpenStoreAsync(
        StorageManifest manifest,
        IPhysicalNamePolicy namePolicy,
        DocumentStoreAccess access,
        CancellationToken cancellationToken) =>
        await SqlServerDocumentStoreFactory.OpenPhysicalAsync(
            ConnectionString,
            manifest,
            GroundworkProvider,
            access,
            namePolicy,
            cancellationToken: cancellationToken);

    protected override async Task<string> ReadProviderVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'));";
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? "unknown";
    }

    protected override async Task<long> CountProjectedRowsAsync(
        ExecutableStorageRoute route,
        ExecutableProjectedColumnRoute projection,
        string value,
        CancellationToken cancellationToken)
    {
        var table = projection.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(*) FROM {Q(table)} WHERE {Q(projection.Column.Identifier)} = @value;";
        command.Parameters.AddWithValue("@value", value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    protected override void ClearPools() => SqlConnection.ClearAllPools();

    protected override async Task FinalizeSeedAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await UpdateStatisticsAsync(connection, cancellationToken);
    }

    private async Task UpdateStatisticsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var statistics = connection.CreateCommand();
        statistics.CommandText = Model.Route.LinkedIndexStorage is null
            ? $"UPDATE STATISTICS {Q(Model.Route.PrimaryStorage.Name.Identifier)} WITH FULLSCAN;"
            : $"UPDATE STATISTICS {Q(Model.Route.PrimaryStorage.Name.Identifier)} WITH FULLSCAN; " +
              $"UPDATE STATISTICS {Q(Model.Route.LinkedIndexStorage.Name.Identifier)} WITH FULLSCAN;";
        await statistics.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Q(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
}
