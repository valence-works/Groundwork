using System.Security.Cryptography;
using System.Text.Json;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

internal static class NativePlanFixtureArtifacts
{
    private const string PhysicalObject = "fixture-physical-object";
    private const string IndexName = "fixture_index";
    private const string NativePlan = "SEARCH fixture_index";

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
            artifacts.Add(await writer.WritePlanAsync(
                benchmarkCase,
                new NativePlanEvidence(
                    request,
                    benchmarkCase.Provider.ToString(),
                    benchmarkCase.StorageForm.ToString(),
                    BenchmarkModelFactory.QueryIdentity,
                    PhysicalObject,
                    IndexName,
                    NativePlan,
                    NativePlanEvidenceAssertions.ForSqlite(assertionMode, IndexName, NativePlan)),
                cancellationToken));
        }
        return artifacts;
    }

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
