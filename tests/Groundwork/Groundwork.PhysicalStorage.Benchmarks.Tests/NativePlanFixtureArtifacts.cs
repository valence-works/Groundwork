using System.Security.Cryptography;
using System.Text.Json;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

internal static class NativePlanTestBindings
{
    public static NativePlanFieldBinding CanonicalFields { get; } = new(
        "storage_scope",
        "document_kind",
        "status",
        "rank",
        "id_comparison_key");
}

internal static class NativePlanFixtureArtifacts
{
    private const string PhysicalObject = "fixture-physical-object";
    private const string IndexName = "fixture_index";

    public static async Task<IReadOnlyList<string>> WriteCanonicalAsync(
        ArtifactLayout layout,
        BenchmarkCase benchmarkCase,
        BenchmarkDataShape dataShape,
        CancellationToken cancellationToken)
    {
        await using var writer = new BenchmarkArtifactWriter(layout);
        return await WriteCanonicalAsync(writer, benchmarkCase, dataShape, cancellationToken);
    }

    public static async Task<IReadOnlyList<string>> WriteCanonicalAsync(
        BenchmarkArtifactWriter writer,
        BenchmarkCase benchmarkCase,
        BenchmarkDataShape dataShape,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(benchmarkCase);
        ArgumentNullException.ThrowIfNull(dataShape);

        var assertionMode = BenchmarkSelectivityPolicy.PlanAssertionModeFor(dataShape);
        var artifacts = new List<string>();
        foreach (var request in BenchmarkPlanRequests.ForWorkloads([benchmarkCase.Workload]))
        {
            var nativePlan = NativePlan(benchmarkCase.Provider);
            artifacts.Add(await writer.WritePlanAsync(
                benchmarkCase,
                new NativePlanEvidence(
                    request,
                    benchmarkCase.Provider,
                    benchmarkCase.StorageForm,
                    BenchmarkModelFactory.QueryIdentityFor(request.Ordered),
                    PhysicalObject,
                    IndexName,
                    nativePlan,
                    Assertions(benchmarkCase.Provider, assertionMode, nativePlan))
                {
                    CommandBinding = CommandBinding(benchmarkCase.Provider, request, assertionMode)
                },
                assertionMode,
                cancellationToken));
        }
        return artifacts;
    }

    private static NativePlanCommandBinding CommandBinding(
        BenchmarkProvider provider,
        BenchmarkPlanRequest request,
        NativePlanAssertionMode assertionMode)
    {
        if (provider == BenchmarkProvider.MongoDb)
        {
            var command = new NativePlanMongoCommandReceipt(
                request.Operation == NativePlanOperation.Selection
                    ? Groundwork.Documents.Store.PhysicalDocumentQueryCommandKind.Page
                    : Groundwork.Documents.Store.PhysicalDocumentQueryCommandKind.Count,
                MongoCommand(request));
            var mongoQueryReceipt = NativePlanQueryReceipt.FromMongoDb(
                request,
                assertionMode,
                command,
                NativePlanTestBindings.CanonicalFields);
            return new NativePlanCommandBinding(
                PhysicalObject,
                null,
                null,
                mongoQueryReceipt.Shape,
                mongoQueryReceipt)
            {
                Fields = NativePlanTestBindings.CanonicalFields,
                MongoCommandReceipt = command
            };
        }

        var commandText = RelationalCommand(provider, request);
        var relationalQueryReceipt = NativePlanQueryReceipt.FromRelational(
            request,
            assertionMode,
            commandText,
            Parameters(request),
            NativePlanTestBindings.CanonicalFields);
        return new NativePlanCommandBinding(
            PhysicalObject,
            "l",
            commandText,
            relationalQueryReceipt.Shape,
            relationalQueryReceipt)
        {
            Fields = NativePlanTestBindings.CanonicalFields
        };
    }

    private static IReadOnlyList<(string Name, object? Value)> Parameters(BenchmarkPlanRequest request)
    {
        var values = new List<(string Name, object? Value)>
        {
            ("scope", "tenant-a"),
            ("kind", "benchmark-document"),
            ("q0", "open")
        };
        if (request.Operation == NativePlanOperation.Selection)
        {
            values.Add(("skip", request.Skip ?? 0));
            values.Add(("take", request.Take));
        }
        return values;
    }

    private static string RelationalCommand(BenchmarkProvider provider, BenchmarkPlanRequest request)
    {
        var quote = provider == BenchmarkProvider.SqlServer ? ("[", "]") : ("\"", "\"");
        var source = $"{quote.Item1}{PhysicalObject}{quote.Item2}";
        var filters = $"l.{quote.Item1}storage_scope{quote.Item2} = @scope AND l.{quote.Item1}document_kind{quote.Item2} = @kind AND l.{quote.Item1}status{quote.Item2} = @q0";
        if (request.Operation == NativePlanOperation.Count)
            return $"SELECT COUNT(*) FROM {source} AS l WHERE {filters}";
        var order = request.Ordered ? $" ORDER BY l.{quote.Item1}rank{quote.Item2} DESC" : string.Empty;
        var paging = provider == BenchmarkProvider.SqlServer
            ? " OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY"
            : " LIMIT @take OFFSET @skip";
        return $"SELECT * FROM {source} AS l WHERE {filters}{order}{paging}";
    }

    private static string MongoCommand(BenchmarkPlanRequest request)
    {
        var order = request.Operation == NativePlanOperation.Selection
            ? request.Ordered
                ? ", { \"$sort\": { \"rank\": -1, \"storage_scope\": 1, \"id_comparison_key\": 1 } }"
                : ", { \"$sort\": { \"storage_scope\": 1, \"id_comparison_key\": 1 } }"
            : string.Empty;
        var paging = request.Operation == NativePlanOperation.Selection
            ? $", {{ \"$skip\": {request.Skip ?? 0} }}, {{ \"$limit\": {request.Take} }}"
            : ", { \"$count\": \"<redacted>\" }";
        return $$"""
            { "aggregate": "{{PhysicalObject}}", "pipeline": [
              { "$match": { "storage_scope": "<redacted>", "document_kind": "<redacted>", "status": "<redacted>" } }{{order}}{{paging}}
            ] }
            """;
    }

    private static string NativePlan(BenchmarkProvider provider) =>
        provider switch
        {
            BenchmarkProvider.Sqlite => $"SEARCH l USING INDEX {IndexName} (status=?)",
            BenchmarkProvider.MongoDb => $$"""
                {
                  "queryPlanner": {
                    "namespace": "fixture.{{PhysicalObject}}",
                    "winningPlan": {
                      "stage": "IXSCAN",
                      "indexName": "{{IndexName}}"
                    }
                  }
                }
                """,
            BenchmarkProvider.PostgreSql => $$"""
                [
                  {
                    "Plan": {
                      "Node Type": "Index Scan",
                      "Relation Name": "{{PhysicalObject}}",
                      "Index Name": "{{IndexName}}"
                    }
                  }
                ]
                """,
            BenchmarkProvider.SqlServer => $$"""
                <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
                  <BatchSequence>
                    <Batch>
                      <Statements>
                        <StmtSimple>
                          <QueryPlan>
                            <RelOp PhysicalOp="Index Seek">
                              <IndexScan>
                                <Object Table="[{{PhysicalObject}}]" Index="[{{IndexName}}]" Alias="[l]" />
                              </IndexScan>
                            </RelOp>
                          </QueryPlan>
                        </StmtSimple>
                      </Statements>
                    </Batch>
                  </BatchSequence>
                </ShowPlanXML>
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };

    private static IReadOnlyList<string> Assertions(
        BenchmarkProvider provider,
        NativePlanAssertionMode assertionMode,
        string nativePlan) =>
        provider switch
        {
            BenchmarkProvider.Sqlite => NativePlanEvidenceAssertions.ForSqlite(assertionMode, IndexName, nativePlan),
            BenchmarkProvider.MongoDb => NativePlanEvidenceAssertions.ForMongoDb(assertionMode, IndexName),
            BenchmarkProvider.PostgreSql => NativePlanEvidenceAssertions.ForPostgreSql(assertionMode),
            BenchmarkProvider.SqlServer => NativePlanEvidenceAssertions.ForSqlServer(assertionMode),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };

    public static async Task ResealIntegrityAsync(string workerRoot, CancellationToken cancellationToken)
    {
        var layout = new ArtifactLayout(workerRoot);
        await using var stream = File.OpenRead(layout.Manifest);
        var manifest = await JsonSerializer.DeserializeAsync<BenchmarkRunManifest>(
                stream,
                BenchmarkJson.Options,
                cancellationToken)
            ?? throw new InvalidOperationException($"Fixture manifest '{layout.Manifest}' is null.");
        var paths = new[]
        {
            layout.RelativePath(layout.Manifest),
            manifest.RawMeasurements,
            manifest.Summary,
            layout.RelativePath(layout.SummaryMarkdown),
            layout.RelativePath(layout.RegressionJson),
            manifest.ElsaMigrationEvidence,
            manifest.MachineMetadata,
            manifest.ProviderMetadata,
            manifest.Configuration
        }.Concat(manifest.ConsumerEvidence is null ? [] : [manifest.ConsumerEvidence])
         .Concat(manifest.PlanArtifacts.SelectMany(plan => new[] { plan, $"{plan}.assertions.json" }))
         .Distinct(StringComparer.Ordinal)
         .Order(StringComparer.Ordinal)
         .Select(relative => new BenchmarkArtifactDigest(
             relative,
             Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(
                 Path.Combine(layout.Root, relative.Replace('/', Path.DirectorySeparatorChar)))))))
         .ToArray();
        await File.WriteAllTextAsync(
            layout.ArtifactIntegrityJson,
            JsonSerializer.Serialize(
                new BenchmarkArtifactIntegrity(BenchmarkArtifactIntegrity.ContractVersion, manifest.RunId, paths),
                BenchmarkJson.Options),
            cancellationToken);
    }
}
