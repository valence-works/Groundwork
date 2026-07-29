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
        var binding = document.RootElement.GetProperty("commandBinding");
        Assert.False(binding.TryGetProperty("parameterizedCommand", out _));
        Assert.Matches("^sha256:[0-9a-f]{64}$", binding.GetProperty("parameterizedCommandDigest").GetString()!);
        var retained = await NativePlanEvidenceSidecar.ReadAsync(sidecarPath, CancellationToken.None);
        Assert.Null(retained.CommandBinding!.ParameterizedCommand);
        Assert.NotNull(retained.CommandBinding.ParameterizedCommandDigest);
    }

    [Fact]
    public async Task Writer_never_serializes_raw_relational_command_text()
    {
        var benchmarkCase = new BenchmarkCase(
            BenchmarkProvider.Sqlite,
            Groundwork.Core.PhysicalStorage.PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkWorkload.IndexedQuery);
        var evidence = Evidence();
        evidence = evidence with
        {
            NativePlan = "SEARCH l USING INDEX fixture_index (status=Secret:synthetic-test-value)",
            CommandBinding = evidence.CommandBinding! with
            {
                ParameterizedCommand = evidence.CommandBinding!.ParameterizedCommand!
                    .Replace("SELECT *", "SELECT *, 0 AS \"Secret:synthetic-test-value\"", StringComparison.Ordinal)
            }
        };
        await using var writer = new BenchmarkArtifactWriter(new ArtifactLayout(root));

        var artifact = await writer.WritePlanAsync(
            benchmarkCase,
            evidence,
            NativePlanAssertionMode.RequireDeclaredIndex,
            CancellationToken.None);

        var sidecar = await File.ReadAllTextAsync(Path.Combine(root, $"{artifact}.assertions.json"));
        var plan = await File.ReadAllTextAsync(Path.Combine(root, artifact));
        Assert.DoesNotContain("synthetic-test-value", sidecar, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-test-value", plan, StringComparison.Ordinal);
        Assert.Contains("predicate-redacted", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("parameterizedCommand\"", sidecar, StringComparison.Ordinal);
        Assert.Contains("\"parameterizedCommandDigest\"", sidecar, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writer_projects_SQL_Server_showplan_to_secret_safe_structure()
    {
        var benchmarkCase = new BenchmarkCase(
            BenchmarkProvider.SqlServer,
            Groundwork.Core.PhysicalStorage.PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkWorkload.IndexedQuery);
        await using var writer = new BenchmarkArtifactWriter(new ArtifactLayout(root));

        var artifact = await writer.WritePlanAsync(
            benchmarkCase,
            SqlServerEvidenceWithSecret(),
            NativePlanAssertionMode.RequireDeclaredIndex,
            CancellationToken.None);

        var plan = await File.ReadAllTextAsync(Path.Combine(root, artifact));
        var sidecar = await File.ReadAllTextAsync(Path.Combine(root, $"{artifact}.assertions.json"));
        foreach (var retained in new[] { plan, sidecar })
        {
            Assert.DoesNotContain("synthetic-test-value", retained, StringComparison.Ordinal);
            Assert.DoesNotContain("comment-payload", retained, StringComparison.Ordinal);
            Assert.DoesNotContain("processing-payload", retained, StringComparison.Ordinal);
            Assert.DoesNotContain("namespace-payload", retained, StringComparison.Ordinal);
            Assert.DoesNotContain("operator-payload", retained, StringComparison.Ordinal);
            Assert.DoesNotContain("Build=", retained, StringComparison.Ordinal);
            Assert.DoesNotContain("StatementText", retained, StringComparison.Ordinal);
            Assert.DoesNotContain("ScalarString", retained, StringComparison.Ordinal);
            Assert.DoesNotContain("Column=", retained, StringComparison.Ordinal);
        }
        Assert.Contains("Table=\"[fixture_table]\"", plan, StringComparison.Ordinal);
        Assert.Contains("Index=\"[fixture_index]\"", plan, StringComparison.Ordinal);
        Assert.Contains("\"parameterizedCommandDigest\"", sidecar, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BenchmarkProvider.PostgreSql)]
    [InlineData(BenchmarkProvider.MongoDb)]
    public async Task Writer_projects_JSON_plans_to_secret_safe_structure(BenchmarkProvider provider)
    {
        var benchmarkCase = new BenchmarkCase(
            provider,
            Groundwork.Core.PhysicalStorage.PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkWorkload.IndexedQuery);
        var evidence = provider switch
        {
            BenchmarkProvider.PostgreSql => PostgreSqlEvidenceWithSecret(),
            BenchmarkProvider.MongoDb => MongoDbEvidenceWithSecret(),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };
        await using var writer = new BenchmarkArtifactWriter(new ArtifactLayout(root));

        var artifact = await writer.WritePlanAsync(
            benchmarkCase,
            evidence,
            NativePlanAssertionMode.RequireDeclaredIndex,
            CancellationToken.None);

        var plan = await File.ReadAllTextAsync(Path.Combine(root, artifact));
        var sidecar = await File.ReadAllTextAsync(Path.Combine(root, $"{artifact}.assertions.json"));
        foreach (var retained in new[] { plan, sidecar })
        {
            Assert.DoesNotContain("synthetic-test-value", retained, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Output\"", retained, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Filter\"", retained, StringComparison.Ordinal);
            Assert.DoesNotContain("\"command\"", retained, StringComparison.Ordinal);
            Assert.DoesNotContain("\"serverInfo\"", retained, StringComparison.Ordinal);
        }
        Assert.Contains("fixture_table", plan, StringComparison.Ordinal);
        Assert.Contains("fixture_index", plan, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BenchmarkProvider.PostgreSql)]
    [InlineData(BenchmarkProvider.MongoDb)]
    public async Task Writer_rejects_unrecognized_JSON_plan_identity(BenchmarkProvider provider)
    {
        var benchmarkCase = new BenchmarkCase(
            provider,
            Groundwork.Core.PhysicalStorage.PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkWorkload.IndexedQuery);
        var evidence = provider switch
        {
            BenchmarkProvider.PostgreSql => PostgreSqlEvidenceWithSecret() with
            {
                NativePlan = """
                    [{ "Plan": { "Node Type": "Append", "Plans": [
                      {
                        "Node Type": "Index Scan",
                        "Relation Name": "fixture_table",
                        "Index Name": "fixture_index"
                      },
                      {
                        "Node Type": "Index Scan",
                        "Relation Name": "fixture_table",
                        "Index Name": "Secret:synthetic-test-value"
                      }
                    ] } }]
                    """
            },
            BenchmarkProvider.MongoDb => MongoDbEvidenceWithSecret() with
            {
                NativePlan = """
                    { "queryPlanner": {
                      "namespace": "fixture.fixture_table",
                      "winningPlan": {
                        "stage": "OR",
                        "inputStages": [
                          { "stage": "IXSCAN", "indexName": "fixture_index" },
                          { "stage": "IXSCAN", "indexName": "Secret:synthetic-test-value" }
                        ]
                      }
                    } }
                    """
            },
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
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

    [Fact]
    public async Task Writer_rejects_SQL_Server_mixed_expected_and_wrong_indexes()
    {
        var benchmarkCase = new BenchmarkCase(
            BenchmarkProvider.SqlServer,
            Groundwork.Core.PhysicalStorage.PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkWorkload.IndexedQuery);
        var evidence = SqlServerEvidenceWithSecret() with
        {
            NativePlan = """
                <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
                  <RelOp PhysicalOp="Index Seek">
                    <Object Table="[fixture_table]" Index="[fixture_index]" />
                  </RelOp>
                  <RelOp PhysicalOp="Index Seek">
                    <Object Table="[fixture_table]" Index="[Secret:synthetic-test-value]" />
                  </RelOp>
                </ShowPlanXML>
                """
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

    [Fact]
    public async Task Writer_rejects_an_unrecognized_PostgreSQL_node_type()
    {
        var benchmarkCase = new BenchmarkCase(
            BenchmarkProvider.PostgreSql,
            Groundwork.Core.PhysicalStorage.PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkWorkload.IndexedQuery);
        var evidence = PostgreSqlEvidenceWithSecret() with
        {
            NativePlan = """
                [{ "Plan": {
                  "Node Type": "Secret:synthetic-test-value",
                  "Relation Name": "fixture_table",
                  "Index Name": "fixture_index"
                } }]
                """
        };
        await using var writer = new BenchmarkArtifactWriter(new ArtifactLayout(root));

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WritePlanAsync(
            benchmarkCase,
            evidence,
            NativePlanAssertionMode.RequireDeclaredIndex,
            CancellationToken.None));
    }

    [Theory]
    [InlineData(BenchmarkProvider.Sqlite)]
    [InlineData(BenchmarkProvider.SqlServer)]
    [InlineData(BenchmarkProvider.PostgreSql)]
    [InlineData(BenchmarkProvider.MongoDb)]
    public async Task Writer_retains_an_alternative_index_without_claiming_its_identity_during_scan_characterization(
        BenchmarkProvider provider)
    {
        const string alternativeIndex = "Secret:synthetic-alternative-index";
        var benchmarkCase = new BenchmarkCase(
            provider,
            Groundwork.Core.PhysicalStorage.PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkWorkload.IndexedQuery);
        var evidence = provider switch
        {
            BenchmarkProvider.Sqlite => Evidence(NativePlanAssertionMode.ScanCharacterization) with
            {
                NativePlan = $"SEARCH l USING INDEX {alternativeIndex} (status=?)"
            },
            BenchmarkProvider.SqlServer => SqlServerEvidenceWithSecret(
                    NativePlanAssertionMode.ScanCharacterization) with
            {
                NativePlan = $"""
                    <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
                      <RelOp PhysicalOp="Index Seek">
                        <Object Table="[fixture_table]" Index="[{alternativeIndex}]" />
                      </RelOp>
                    </ShowPlanXML>
                    """
            },
            BenchmarkProvider.PostgreSql => PostgreSqlEvidenceWithSecret(
                    NativePlanAssertionMode.ScanCharacterization) with
            {
                NativePlan = PostgreSqlEvidenceWithSecret(
                        NativePlanAssertionMode.ScanCharacterization)
                    .NativePlan
                    .Replace("fixture_index", alternativeIndex, StringComparison.Ordinal)
            },
            BenchmarkProvider.MongoDb => MongoDbEvidenceWithSecret(
                    NativePlanAssertionMode.ScanCharacterization) with
            {
                NativePlan = MongoDbEvidenceWithSecret(
                        NativePlanAssertionMode.ScanCharacterization)
                    .NativePlan
                    .Replace("fixture_index", alternativeIndex, StringComparison.Ordinal)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };
        await using var writer = new BenchmarkArtifactWriter(new ArtifactLayout(root));

        var artifact = await writer.WritePlanAsync(
            benchmarkCase,
            evidence,
            NativePlanAssertionMode.ScanCharacterization,
            CancellationToken.None);

        var plan = await File.ReadAllTextAsync(Path.Combine(root, artifact));
        Assert.Contains(ProviderNativePlanRetention.AlternativeIndexRedacted, plan, StringComparison.Ordinal);
        Assert.DoesNotContain(alternativeIndex, plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writer_retains_PostgreSQL_scan_characterization_without_requiring_an_index()
    {
        var benchmarkCase = new BenchmarkCase(
            BenchmarkProvider.PostgreSql,
            Groundwork.Core.PhysicalStorage.PhysicalStorageForm.SharedDocuments,
            BenchmarkWorkload.IndexedQuery);
        await using var writer = new BenchmarkArtifactWriter(new ArtifactLayout(root));

        var artifact = await writer.WritePlanAsync(
            benchmarkCase,
            PostgreSqlEvidenceWithSecret(
                NativePlanAssertionMode.ScanCharacterization,
                usesDeclaredIndex: false) with
            {
                StorageForm = Groundwork.Core.PhysicalStorage.PhysicalStorageForm.SharedDocuments
            },
            NativePlanAssertionMode.ScanCharacterization,
            CancellationToken.None);

        var plan = await File.ReadAllTextAsync(Path.Combine(root, artifact));
        Assert.Contains("\"Node Type\":\"Seq Scan\"", plan, StringComparison.Ordinal);
        Assert.Contains("\"Relation Name\":\"fixture_table\"", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Index Name\"", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-test-value", plan, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("; Server=example; Password=synthetic-test-value")]
    [InlineData(" /* Pwd=synthetic-test-value */")]
    public async Task Writer_rejects_unsafe_evidence_before_writing_either_plan_artifact(string unsafeSuffix)
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
                    $"{evidence.CommandBinding!.ParameterizedCommand}{unsafeSuffix}"
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

    private static NativePlanEvidence Evidence(
        NativePlanAssertionMode assertionMode = NativePlanAssertionMode.RequireDeclaredIndex)
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.IndexedQuery])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        const string command = "SELECT * FROM \"fixture_table\" AS l WHERE l.\"storage_scope\" = @scope AND l.\"document_kind\" = @kind AND l.\"status\" = @q0 LIMIT @take OFFSET @skip";
        var receipt = NativePlanQueryReceipt.FromRelational(
            request,
            assertionMode,
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
                assertionMode,
                "fixture_index",
                "SEARCH l USING INDEX fixture_index (status=?)"))
        {
            CommandBinding = new NativePlanCommandBinding("fixture_table", "l", command, receipt.Shape, receipt)
            {
                Fields = NativePlanTestBindings.CanonicalFields
            }
        };
    }

    private static NativePlanEvidence SqlServerEvidenceWithSecret(
        NativePlanAssertionMode assertionMode = NativePlanAssertionMode.RequireDeclaredIndex)
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.IndexedQuery])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        const string command = "SELECT * FROM [fixture_table] AS l WHERE l.[storage_scope] = @scope AND l.[document_kind] = @kind AND l.[status] = @q0 OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY";
        var receipt = NativePlanQueryReceipt.FromRelational(
            request,
            assertionMode,
            command,
            [
                ("scope", (object?)"tenant-a"),
                ("kind", "benchmark-document"),
                ("q0", "open"),
                ("skip", 0),
                ("take", 20)
            ],
            NativePlanTestBindings.CanonicalFields);
        const string nativePlan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"
                         xmlns:leak="urn:namespace-payload"
                         Version="1.564"
                         Build="Secret:synthetic-test-value">
              <BatchSequence><Batch><Statements>
                <StmtSimple StatementText="SELECT 1 AS [Secret:synthetic-test-value]">
                  <QueryPlan>
                    <!-- comment-payload -->
                    <?groundwork processing-payload?>
                    <RelOp PhysicalOp="Secret:synthetic-test-value" LogicalOp="operator-payload">
                      <Object Table="[Secret:synthetic-test-value]" Index="[Secret:synthetic-test-value]" />
                    </RelOp>
                    <Secret-synthetic-test-value>
                      <Nested-secret-synthetic-test-value />
                    </Secret-synthetic-test-value>
                    <RelOp PhysicalOp="Index Seek">
                    <Nested-secret-synthetic-test-value />
                    <IndexScan>
                    <DefinedValues><DefinedValue><ColumnReference Column="[Secret:synthetic-test-value]" /></DefinedValue></DefinedValues>
                    <Predicate><ScalarOperator ScalarString="[Secret:synthetic-test-value]" /></Predicate>
                    <Object Table="[fixture_table]" Index="[fixture_index]" Alias="[l]" />
                    </IndexScan></RelOp>
                  </QueryPlan>
                </StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;
        return new NativePlanEvidence(
            request,
            BenchmarkProvider.SqlServer,
            Groundwork.Core.PhysicalStorage.PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkModelFactory.QueryIdentityFor(request.Ordered),
            "fixture_table",
            "fixture_index",
            nativePlan,
            NativePlanEvidenceAssertions.ForSqlServer(assertionMode))
        {
            CommandBinding = new NativePlanCommandBinding("fixture_table", "l", command, receipt.Shape, receipt)
            {
                Fields = NativePlanTestBindings.CanonicalFields
            }
        };
    }

    private static NativePlanEvidence PostgreSqlEvidenceWithSecret(
        NativePlanAssertionMode assertionMode = NativePlanAssertionMode.RequireDeclaredIndex,
        bool usesDeclaredIndex = true)
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.IndexedQuery])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        const string command = "SELECT * FROM \"fixture_table\" AS l WHERE l.\"storage_scope\" = @scope AND l.\"document_kind\" = @kind AND l.\"status\" = @q0 LIMIT @take OFFSET @skip";
        var receipt = NativePlanQueryReceipt.FromRelational(
            request,
            assertionMode,
            command,
            [
                ("scope", (object?)"tenant-a"),
                ("kind", "benchmark-document"),
                ("q0", "open"),
                ("skip", 0),
                ("take", 20)
            ],
            NativePlanTestBindings.CanonicalFields);
        var nativePlan = usesDeclaredIndex
            ? """
              [{
                "Plan": {
                "Node Type": "Incremental Sort",
                "Output": ["Secret:synthetic-test-value"],
                "Filter": "status = 'synthetic-test-value'",
                "Plans": [
                  {
                    "Node Type": "Hash Join",
                    "Plans": [
                      {
                        "Node Type": "Seq Scan",
                        "Relation Name": "Secret:synthetic-test-value"
                      },
                      {
                        "Node Type": "Index Scan",
                        "Relation Name": "fixture_table",
                        "Index Name": "fixture_index",
                        "Index Cond": "status = 'synthetic-test-value'"
                      }
                    ]
                  }
                ]
                }
              }]
              """
            : """
              [{
                "Plan": {
                  "Node Type": "Incremental Sort",
                  "Plans": [
                    {
                      "Node Type": "Hash Join",
                      "Plans": [
                        {
                          "Node Type": "Seq Scan",
                          "Relation Name": "Secret:synthetic-test-value"
                        },
                        {
                          "Node Type": "Seq Scan",
                          "Relation Name": "fixture_table",
                          "Filter": "status = 'synthetic-test-value'"
                        }
                      ]
                    }
                  ]
                }
              }]
              """;
        return new NativePlanEvidence(
            request,
            BenchmarkProvider.PostgreSql,
            Groundwork.Core.PhysicalStorage.PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkModelFactory.QueryIdentityFor(request.Ordered),
            "fixture_table",
            "fixture_index",
            nativePlan,
            NativePlanEvidenceAssertions.ForPostgreSql(assertionMode))
        {
            CommandBinding = new NativePlanCommandBinding("fixture_table", "l", command, receipt.Shape, receipt)
            {
                Fields = NativePlanTestBindings.CanonicalFields
            }
        };
    }

    private static NativePlanEvidence MongoDbEvidenceWithSecret(
        NativePlanAssertionMode assertionMode = NativePlanAssertionMode.RequireDeclaredIndex)
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.IndexedQuery])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        var command = new NativePlanMongoCommandReceipt(
            Groundwork.Documents.Store.PhysicalDocumentQueryCommandKind.Page,
            """
            { "aggregate": "fixture_table", "pipeline": [
              { "$match": { "storage_scope": "<redacted>", "document_kind": "<redacted>", "status": "<redacted>" } },
              { "$sort": { "storage_scope": 1, "id_comparison_key": 1 } },
              { "$skip": 0 },
              { "$limit": 20 }
            ] }
            """);
        var receipt = NativePlanQueryReceipt.FromMongoDb(
            request,
            assertionMode,
            command,
            NativePlanTestBindings.CanonicalFields);
        const string nativePlan = """
            {
              "queryPlanner": {
                "namespace": "fixture.fixture_table",
                "winningPlan": {
                  "stage": "FETCH",
                  "inputStage": { "stage": "IXSCAN", "indexName": "fixture_index" }
                },
                "parsedQuery": { "status": "synthetic-test-value" }
              },
              "command": { "filter": { "status": "synthetic-test-value" } },
              "serverInfo": { "host": "synthetic-test-value" }
            }
            """;
        return new NativePlanEvidence(
            request,
            BenchmarkProvider.MongoDb,
            Groundwork.Core.PhysicalStorage.PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkModelFactory.QueryIdentityFor(request.Ordered),
            "fixture_table",
            "fixture_index",
            nativePlan,
            NativePlanEvidenceAssertions.ForMongoDb(
                assertionMode,
                "fixture_index"))
        {
            CommandBinding = new NativePlanCommandBinding("fixture_table", null, null, receipt.Shape, receipt)
            {
                Fields = NativePlanTestBindings.CanonicalFields,
                MongoCommandReceipt = command
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
