namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed class ArtifactLayout
{
    public ArtifactLayout(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = BenchmarkArtifactPaths.RequireRoot(root, create: true);
    }

    public string Root { get; }
    public string Manifest => Path.Combine(Root, "manifest.json");
    public string RawMeasurements => Path.Combine(Root, "raw", "measurements.jsonl");
    public string SummaryJson => Path.Combine(Root, "reports", "summary.json");
    public string SummaryMarkdown => Path.Combine(Root, "reports", "summary.md");
    public string RegressionJson => Path.Combine(Root, "reports", "regression.json");
    public string ElsaMigrationEvidenceJson => Path.Combine(Root, "reports", "elsa-migration-evidence.json");
    public string ConsumerEvidenceJson => Path.Combine(Root, "reports", "consumer-evidence.json");
    public string ArtifactIntegrityJson => Path.Combine(Root, "reports", "artifact-integrity.json");
    public string MachineMetadata => Path.Combine(Root, "metadata", "machine.json");
    public string ProviderMetadata => Path.Combine(Root, "metadata", "providers.json");
    public string Configuration => Path.Combine(Root, "metadata", "configuration.json");

    public string RelativePath(string path) =>
        BenchmarkArtifactPaths.Relative(Root, path, "Artifact").Replace(Path.DirectorySeparatorChar, '/');

    public string ExistingPath(string path, string description) =>
        BenchmarkArtifactPaths.ResolveAbsoluteExisting(Root, path, description);

    public string WritablePath(string path, string description) =>
        BenchmarkArtifactPaths.ResolveAbsoluteForWrite(Root, path, description);

    public void RequireEmptyOutput()
    {
        if (Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any())
            throw new InvalidOperationException($"Benchmark output directory '{Root}' is not empty.");
    }

    public string Plan(BenchmarkCase benchmarkCase, NativePlanOperation operation, string extension) => Path.Combine(
        Root,
        "plans",
        benchmarkCase.Provider.ToString().ToLowerInvariant(),
        benchmarkCase.StorageForm.ToString().ToLowerInvariant(),
        $"{benchmarkCase.Workload.ToString().ToLowerInvariant()}-{operation.ToString().ToLowerInvariant()}.{extension.TrimStart('.')}");

    public void CreateDirectories()
    {
        foreach (var file in new[]
                 {
                     Manifest, RawMeasurements, SummaryJson, SummaryMarkdown, RegressionJson, ElsaMigrationEvidenceJson,
                     ConsumerEvidenceJson, ArtifactIntegrityJson,
                     MachineMetadata, ProviderMetadata, Configuration,
                     Plan(new BenchmarkCase(BenchmarkProvider.Sqlite,
                         Groundwork.Core.PhysicalStorage.PhysicalStorageForm.SharedDocuments,
                         BenchmarkWorkload.IndexedQuery), NativePlanOperation.Selection, "txt")
                 })
        {
            _ = WritablePath(file, "Artifact output");
        }
    }
}
