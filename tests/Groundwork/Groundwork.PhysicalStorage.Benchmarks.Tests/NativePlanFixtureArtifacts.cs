using System.Security.Cryptography;
using System.Text.Json;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

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
                    benchmarkCase.Provider.ToString(),
                    benchmarkCase.StorageForm.ToString(),
                    BenchmarkModelFactory.QueryIdentity,
                    PhysicalObject,
                    IndexName,
                    nativePlan,
                    Assertions(benchmarkCase.Provider, assertionMode, nativePlan))
                {
                    CommandBinding = CommandBinding(benchmarkCase.Provider)
                },
                cancellationToken));
        }
        return artifacts;
    }

    private static NativePlanCommandBinding CommandBinding(BenchmarkProvider provider) =>
        provider switch
        {
            BenchmarkProvider.Sqlite => new(
                PhysicalObject,
                "l",
                $"SELECT * FROM \"{PhysicalObject}\" AS l INDEXED BY \"{IndexName}\" WHERE l.\"status\" = @value"),
            BenchmarkProvider.PostgreSql => new(
                PhysicalObject,
                "l",
                $"SELECT * FROM \"{PhysicalObject}\" l WHERE l.\"status\" = @value"),
            BenchmarkProvider.SqlServer => new(
                PhysicalObject,
                "l",
                $"SELECT * FROM [{PhysicalObject}] AS l WITH (INDEX([{IndexName}])) WHERE l.[status] = @value"),
            BenchmarkProvider.MongoDb => new(PhysicalObject, null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };

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
