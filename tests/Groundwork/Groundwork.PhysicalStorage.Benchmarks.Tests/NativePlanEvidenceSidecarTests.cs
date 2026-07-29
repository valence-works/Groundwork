using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class NativePlanEvidenceSidecarTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"groundwork-plan-sidecar-{Guid.NewGuid():N}");

    [Fact]
    public void Schema_is_versioned_strict_and_cataloged()
    {
        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "benchmarks",
            "Groundwork.PhysicalStorage.Benchmarks",
            "schemas",
            "v1",
            "native-plan-assertions.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));

        Assert.Equal(NativePlanEvidence.SidecarSchemaVersion,
            schema.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetString());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("native-plan-assertions.schema.json", File.ReadAllText(Path.Combine(Path.GetDirectoryName(schemaPath)!, "README.md")));
    }

    [Theory]
    [InlineData(BenchmarkProvider.Sqlite, "sqlite")]
    [InlineData(BenchmarkProvider.SqlServer, "sqlServer")]
    [InlineData(BenchmarkProvider.PostgreSql, "postgreSql")]
    [InlineData(BenchmarkProvider.MongoDb, "mongoDb")]
    public void Provider_identity_uses_the_schema_enum(
        BenchmarkProvider provider,
        string expected)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(Evidence() with { Provider = provider }, BenchmarkJson.CompactOptions));

        Assert.Equal(expected, document.RootElement.GetProperty("provider").GetString());
    }

    [Theory]
    [InlineData(Groundwork.Core.PhysicalStorage.PhysicalStorageForm.SharedDocuments, "sharedDocuments")]
    [InlineData(Groundwork.Core.PhysicalStorage.PhysicalStorageForm.DedicatedDocumentTable, "dedicatedDocumentTable")]
    [InlineData(Groundwork.Core.PhysicalStorage.PhysicalStorageForm.PhysicalEntityTable, "physicalEntityTable")]
    public void Storage_form_identity_uses_the_schema_enum(
        Groundwork.Core.PhysicalStorage.PhysicalStorageForm storageForm,
        string expected)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(Evidence() with { StorageForm = storageForm }, BenchmarkJson.CompactOptions));

        Assert.Equal(expected, document.RootElement.GetProperty("storageForm").GetString());
    }

    [Fact]
    public void Validator_rejects_unknown_members_and_unsupported_versions()
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(Evidence(), BenchmarkJson.CompactOptions))!.AsObject();
        node["untrusted"] = true;
        using var unknown = JsonDocument.Parse(node.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => NativePlanEvidenceSidecar.ValidateDocument(unknown.RootElement));

        node.Remove("untrusted");
        node["schemaVersion"] = "groundwork.physical-storage.native-plan-assertions/v2";
        using var unsupported = JsonDocument.Parse(node.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => NativePlanEvidenceSidecar.ValidateDocument(unsupported.RootElement));
    }

    [Fact]
    public async Task Reader_rejects_a_sidecar_with_an_invalid_typed_member()
    {
        Directory.CreateDirectory(root);
        var node = JsonNode.Parse(JsonSerializer.Serialize(Evidence(), BenchmarkJson.CompactOptions))!.AsObject();
        node["provider"] = 42;
        var path = Path.Combine(root, "invalid.assertions.json");
        await File.WriteAllTextAsync(path, node.ToJsonString());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativePlanEvidenceSidecar.ReadAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Writer_emits_a_valid_versioned_sidecar()
    {
        var benchmarkCase = new BenchmarkCase(
            BenchmarkProvider.Sqlite,
            Groundwork.Core.PhysicalStorage.PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkWorkload.IndexedQuery);
        await using var writer = new BenchmarkArtifactWriter(new ArtifactLayout(root));
        var artifact = await writer.WritePlanAsync(
            benchmarkCase,
            Evidence(),
            NativePlanAssertionMode.RequireDeclaredIndex,
            CancellationToken.None);
        var sidecarPath = Path.Combine(root, $"{artifact}.assertions.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sidecarPath));

        NativePlanEvidenceSidecar.ValidateDocument(document.RootElement);
        Assert.Equal(NativePlanEvidence.SidecarSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetString());
        Assert.True(document.RootElement.GetProperty("commandBinding").TryGetProperty("parameterizedCommand", out _));
    }

    [Fact]
    public async Task Writer_rejects_unsafe_evidence_before_writing_either_plan_artifact()
    {
        var benchmarkCase = new BenchmarkCase(
            BenchmarkProvider.Sqlite,
            Groundwork.Core.PhysicalStorage.PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkWorkload.IndexedQuery);
        var evidence = Evidence();
        evidence = evidence with
        {
            CommandBinding = evidence.CommandBinding! with
            {
                ParameterizedCommand =
                    $"{evidence.CommandBinding!.ParameterizedCommand}; Server=example; Password=synthetic-test-value"
            }
        };
        await using var writer = new BenchmarkArtifactWriter(new ArtifactLayout(root));

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WritePlanAsync(
            benchmarkCase,
            evidence,
            NativePlanAssertionMode.RequireDeclaredIndex,
            CancellationToken.None));

        var plans = Path.Combine(root, "plans");
        Assert.False(Directory.Exists(plans) &&
                     Directory.EnumerateFiles(plans, "*", SearchOption.AllDirectories).Any());
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static NativePlanEvidence Evidence()
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.IndexedQuery])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        const string command = "SELECT * FROM \"fixture_table\" AS l WHERE l.\"storage_scope\" = @scope AND l.\"document_kind\" = @kind AND l.\"status\" = @q0 LIMIT @take OFFSET @skip";
        var receipt = NativePlanQueryReceipt.FromRelational(
            request,
            NativePlanAssertionMode.RequireDeclaredIndex,
            command,
            [
                ("scope", (object?)"tenant-a"),
                ("kind", "benchmark-document"),
                ("q0", "open"),
                ("skip", 0),
                ("take", 20)
            ],
            NativePlanTestBindings.CanonicalFields);
        return new NativePlanEvidence(
            request,
            BenchmarkProvider.Sqlite,
            Groundwork.Core.PhysicalStorage.PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkModelFactory.QueryIdentityFor(request.Ordered),
            "fixture_table",
            "fixture_index",
            "SEARCH l USING INDEX fixture_index (status=?)",
            NativePlanEvidenceAssertions.ForSqlite(
                NativePlanAssertionMode.RequireDeclaredIndex,
                "fixture_index",
                "SEARCH l USING INDEX fixture_index (status=?)"))
        {
            CommandBinding = new NativePlanCommandBinding("fixture_table", "l", command, receipt.Shape, receipt)
            {
                Fields = NativePlanTestBindings.CanonicalFields
            }
        };
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
