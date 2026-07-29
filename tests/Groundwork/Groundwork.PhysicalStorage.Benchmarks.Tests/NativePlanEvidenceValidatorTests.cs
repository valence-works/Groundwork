using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using MongoDB.Bson;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class NativePlanEvidenceValidatorTests
{
    private const string PhysicalObject = "expected_table";
    private const string IndexName = "expected_index";

    public static TheoryData<BenchmarkProvider, NativePlanAssertionMode> ProviderModes => new()
    {
        { BenchmarkProvider.Sqlite, NativePlanAssertionMode.RequireDeclaredIndex },
        { BenchmarkProvider.Sqlite, NativePlanAssertionMode.ScanCharacterization },
        { BenchmarkProvider.MongoDb, NativePlanAssertionMode.RequireDeclaredIndex },
        { BenchmarkProvider.MongoDb, NativePlanAssertionMode.ScanCharacterization },
        { BenchmarkProvider.PostgreSql, NativePlanAssertionMode.RequireDeclaredIndex },
        { BenchmarkProvider.PostgreSql, NativePlanAssertionMode.ScanCharacterization },
        { BenchmarkProvider.SqlServer, NativePlanAssertionMode.RequireDeclaredIndex },
        { BenchmarkProvider.SqlServer, NativePlanAssertionMode.ScanCharacterization }
    };

    [Theory]
    [MemberData(nameof(ProviderModes))]
    public void Every_provider_accepts_a_complete_actual_command_receipt(
        BenchmarkProvider provider,
        NativePlanAssertionMode mode)
    {
        foreach (var request in BenchmarkPlanRequests.ForWorkloads(
                     [BenchmarkWorkload.IndexedQuery, BenchmarkWorkload.MixedCompoundOrdering, BenchmarkWorkload.PaginationAndCount]))
        {
            var binding = Binding(provider, request, mode);

            Assert.True(Matches(provider, mode, request, binding, NativePlan(provider)));
        }
    }

    [Theory]
    [MemberData(nameof(ProviderModes))]
    public void Every_provider_rejects_cross_request_substitution(
        BenchmarkProvider provider,
        NativePlanAssertionMode mode)
    {
        var requests = BenchmarkPlanRequests.ForWorkloads(
            [BenchmarkWorkload.IndexedQuery, BenchmarkWorkload.MixedCompoundOrdering, BenchmarkWorkload.PaginationAndCount]);

        foreach (var source in requests)
        {
            var binding = Binding(provider, source, mode);
            foreach (var substituted in requests.Where(request => request != source))
            {
                Assert.False(
                    Matches(provider, mode, substituted, binding, NativePlan(provider)),
                    $"{provider} accepted {source.Workload}/{source.Operation} as {substituted.Workload}/{substituted.Operation}.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(ProviderModes))]
    public void Every_provider_rejects_missing_or_mutated_actual_command_structure(
        BenchmarkProvider provider,
        NativePlanAssertionMode mode)
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.PaginationAndCount])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        var binding = Binding(provider, request, mode);

        var tampered = provider == BenchmarkProvider.MongoDb
            ? binding with
            {
                MongoCommandReceipt = binding.MongoCommandReceipt! with
                {
                    RedactedCommand = binding.MongoCommandReceipt.RedactedCommand.Replace("7", "8", StringComparison.Ordinal)
                }
            }
            : binding with { ParameterizedCommand = binding.ParameterizedCommand!.Replace("@skip", "@forged", StringComparison.Ordinal) };

        Assert.False(Matches(provider, mode, request, tampered, NativePlan(provider)));
    }

    [Fact]
    public void Validator_fails_closed_for_a_programmatic_shape_with_null_collections()
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.IndexedQuery])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        const NativePlanAssertionMode mode = NativePlanAssertionMode.ScanCharacterization;
        var original = Binding(BenchmarkProvider.Sqlite, request, mode);
        var malformed = original with
        {
            RequestShape = original.RequestShape! with { Filters = null! }
        };

        Assert.False(Matches(
            BenchmarkProvider.Sqlite,
            mode,
            request,
            malformed,
            NativePlan(BenchmarkProvider.Sqlite)));
    }

    [Fact]
    public void MongoDB_rejects_a_receipt_that_leaks_a_predicate_value()
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.IndexedQuery]).First();
        const NativePlanAssertionMode mode = NativePlanAssertionMode.ScanCharacterization;
        var binding = Binding(BenchmarkProvider.MongoDb, request, mode) with
        {
            MongoCommandReceipt = new NativePlanMongoCommandReceipt(
                PhysicalDocumentQueryCommandKind.Page,
                """
                { "aggregate": "expected_table", "pipeline": [
                  { "$match": { "storage_scope": "<redacted>", "document_kind": "<redacted>", "status": "open" } },
                  { "$limit": 20 }
                ] }
                """)
        };

        Assert.False(Matches(
            BenchmarkProvider.MongoDb,
            mode,
            request,
            binding,
            NativePlan(BenchmarkProvider.MongoDb)));
    }

    [Fact]
    public void MongoDB_does_not_treat_sort_or_projection_fields_as_predicates()
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.IndexedQuery]).First();
        const NativePlanAssertionMode mode = NativePlanAssertionMode.ScanCharacterization;
        var binding = Binding(BenchmarkProvider.MongoDb, request, mode) with
        {
            MongoCommandReceipt = new NativePlanMongoCommandReceipt(
                PhysicalDocumentQueryCommandKind.Page,
                """
                { "aggregate": "expected_table", "pipeline": [
                  { "$match": { "storage_scope": "<redacted>", "document_kind": "<redacted>" } },
                  { "$sort": { "status": -1 } },
                  { "$project": { "status": "<redacted>" } },
                  { "$limit": 20 }
                ] }
                """)
        };

        Assert.False(Matches(
            BenchmarkProvider.MongoDb,
            mode,
            request,
            binding,
            NativePlan(BenchmarkProvider.MongoDb)));
    }

    [Fact]
    public void MongoDB_capture_does_not_treat_a_projected_status_value_as_the_selected_predicate()
    {
        var explain = BsonDocument.Parse(
            """
            { "command": { "aggregate": "expected_table", "pipeline": [
              { "$match": { "storage_scope": "tenant-a", "document_kind": "benchmark-document" } },
              { "$project": { "status": "open" } },
              { "$limit": 20 }
            ] } }
            """);

        Assert.Throws<InvalidOperationException>(() =>
            NativePlanMongoCommandReceipt.FromExplain(
                explain,
                PhysicalDocumentQueryCommandKind.Page,
                NativePlanTestBindings.CanonicalFields));
    }

    [Fact]
    public void MongoDB_rejects_a_page_command_resealed_as_a_count_terminal()
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.PaginationAndCount])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        const NativePlanAssertionMode mode = NativePlanAssertionMode.ScanCharacterization;
        var original = Binding(BenchmarkProvider.MongoDb, request, mode);
        var binding = original with
        {
            MongoCommandReceipt = original.MongoCommandReceipt! with
            {
                Kind = PhysicalDocumentQueryCommandKind.Count
            }
        };

        Assert.False(Matches(
            BenchmarkProvider.MongoDb,
            mode,
            request,
            binding,
            NativePlan(BenchmarkProvider.MongoDb)));
    }

    [Fact]
    public void Relational_receipt_rejects_suffix_matched_forged_physical_fields()
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.MixedCompoundOrdering])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        const NativePlanAssertionMode mode = NativePlanAssertionMode.ScanCharacterization;
        var original = Binding(BenchmarkProvider.Sqlite, request, mode);
        var forgedCommand = original.ParameterizedCommand!
            .Replace("storage_scope", "forged_storage_scope", StringComparison.Ordinal)
            .Replace("document_kind", "forged_document_kind", StringComparison.Ordinal)
            .Replace("status", "forged_status", StringComparison.Ordinal)
            .Replace("rank", "forged_rank", StringComparison.Ordinal);

        Assert.False(Matches(
            BenchmarkProvider.Sqlite,
            mode,
            request,
            original with { ParameterizedCommand = forgedCommand },
            NativePlan(BenchmarkProvider.Sqlite)));
    }

    [Theory]
    [InlineData(BenchmarkProvider.PostgreSql, "\"")]
    [InlineData(BenchmarkProvider.SqlServer, "[")]
    public void Relational_receipt_accepts_provider_quoted_aliases(
        BenchmarkProvider provider,
        string aliasQuote)
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.MixedCompoundOrdering])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        const NativePlanAssertionMode mode = NativePlanAssertionMode.ScanCharacterization;
        var original = Binding(provider, request, mode);
        var quotedAlias = aliasQuote == "[" ? "[l]" : "\"l\"";
        var command = original.ParameterizedCommand!
            .Replace(" AS l ", $" AS {quotedAlias} ", StringComparison.Ordinal)
            .Replace("l.", $"{quotedAlias}.", StringComparison.Ordinal);
        var receipt = NativePlanQueryReceipt.FromRelational(
            request,
            mode,
            command,
            Parameters(request),
            NativePlanTestBindings.CanonicalFields);
        var binding = original with
        {
            ParameterizedCommand = command,
            RequestShape = receipt.Shape,
            QueryReceipt = receipt
        };

        Assert.True(Matches(provider, mode, request, binding, NativePlan(provider)));
    }

    [Fact]
    public void MongoDB_capture_redacts_nested_numeric_limit_and_skip_fields()
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.IndexedQuery])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        const NativePlanAssertionMode mode = NativePlanAssertionMode.ScanCharacterization;
        var explain = BsonDocument.Parse(
            """
            { "command": { "aggregate": "expected_table", "pipeline": [
              { "$match": { "storage_scope": "tenant-a", "document_kind": "benchmark-document", "status": "open" } },
              { "$project": { "metadata": { "limit": 999, "skip": 777 } } },
              { "$limit": 20 }
            ] } }
            """);
        var command = NativePlanMongoCommandReceipt.FromExplain(
            explain,
            PhysicalDocumentQueryCommandKind.Page,
            NativePlanTestBindings.CanonicalFields);
        var receipt = NativePlanQueryReceipt.FromMongoDb(
            request,
            mode,
            command,
            NativePlanTestBindings.CanonicalFields);
        var binding = new NativePlanCommandBinding(
            PhysicalObject,
            null,
            null,
            receipt.Shape,
            receipt)
        {
            Fields = NativePlanTestBindings.CanonicalFields,
            MongoCommandReceipt = command
        };

        Assert.DoesNotContain("999", command.RedactedCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("777", command.RedactedCommand, StringComparison.Ordinal);
        Assert.True(Matches(
            BenchmarkProvider.MongoDb,
            mode,
            request,
            binding,
            NativePlan(BenchmarkProvider.MongoDb)));
    }

    [Fact]
    public void MongoDB_rejects_nested_skip_impersonating_page_structure()
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.PaginationAndCount])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        const NativePlanAssertionMode mode = NativePlanAssertionMode.ScanCharacterization;
        var command = new NativePlanMongoCommandReceipt(
            PhysicalDocumentQueryCommandKind.Page,
            """
            { "find": "expected_table",
              "filter": {
                "storage_scope": "<redacted>",
                "document_kind": "<redacted>",
                "status": "<redacted>",
                "metadata": { "skip": 7 }
              },
              "sort": { "rank": -1 },
              "limit": 20
            }
            """);
        var receipt = NativePlanQueryReceipt.FromMongoDb(
            request,
            mode,
            command,
            NativePlanTestBindings.CanonicalFields);
        var binding = new NativePlanCommandBinding(
            PhysicalObject,
            null,
            null,
            receipt.Shape,
            receipt)
        {
            Fields = NativePlanTestBindings.CanonicalFields,
            MongoCommandReceipt = command
        };

        Assert.Null(receipt.Shape.Skip);
        Assert.False(Matches(
            BenchmarkProvider.MongoDb,
            mode,
            request,
            binding,
            NativePlan(BenchmarkProvider.MongoDb)));
    }

    [Fact]
    public void MongoDB_rejects_root_skip_impersonating_an_aggregate_pipeline_stage()
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.PaginationAndCount])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        const NativePlanAssertionMode mode = NativePlanAssertionMode.ScanCharacterization;
        var command = new NativePlanMongoCommandReceipt(
            PhysicalDocumentQueryCommandKind.Page,
            """
            { "aggregate": "expected_table",
              "skip": 7,
              "pipeline": [
                { "$match": { "storage_scope": "<redacted>", "document_kind": "<redacted>", "status": "<redacted>" } },
                { "$sort": { "rank": -1 } },
                { "$limit": 20 }
              ]
            }
            """);
        var receipt = NativePlanQueryReceipt.FromMongoDb(
            request,
            mode,
            command,
            NativePlanTestBindings.CanonicalFields);
        var binding = new NativePlanCommandBinding(
            PhysicalObject,
            null,
            null,
            receipt.Shape,
            receipt)
        {
            Fields = NativePlanTestBindings.CanonicalFields,
            MongoCommandReceipt = command
        };

        Assert.Null(receipt.Shape.Skip);
        Assert.False(Matches(
            BenchmarkProvider.MongoDb,
            mode,
            request,
            binding,
            NativePlan(BenchmarkProvider.MongoDb)));
    }

    [Fact]
    public void MongoDB_rejects_nested_sort_impersonating_order_structure()
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.MixedCompoundOrdering])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        const NativePlanAssertionMode mode = NativePlanAssertionMode.ScanCharacterization;
        var command = new NativePlanMongoCommandReceipt(
            PhysicalDocumentQueryCommandKind.Page,
            """
            { "find": "expected_table",
              "filter": {
                "storage_scope": "<redacted>",
                "document_kind": "<redacted>",
                "status": "<redacted>",
                "metadata": { "sort": { "rank": -1 } }
              },
              "limit": 20
            }
            """);
        var receipt = NativePlanQueryReceipt.FromMongoDb(
            request,
            mode,
            command,
            NativePlanTestBindings.CanonicalFields);
        var binding = new NativePlanCommandBinding(
            PhysicalObject,
            null,
            null,
            receipt.Shape,
            receipt)
        {
            Fields = NativePlanTestBindings.CanonicalFields,
            MongoCommandReceipt = command
        };

        Assert.Empty(receipt.Shape.Order);
        Assert.False(Matches(
            BenchmarkProvider.MongoDb,
            mode,
            request,
            binding,
            NativePlan(BenchmarkProvider.MongoDb)));
    }

    [Fact]
    public void MongoDB_rejects_root_sort_impersonating_an_aggregate_pipeline_stage()
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.MixedCompoundOrdering])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        const NativePlanAssertionMode mode = NativePlanAssertionMode.ScanCharacterization;
        var command = new NativePlanMongoCommandReceipt(
            PhysicalDocumentQueryCommandKind.Page,
            """
            { "aggregate": "expected_table",
              "sort": { "rank": -1 },
              "pipeline": [
                { "$match": { "storage_scope": "<redacted>", "document_kind": "<redacted>", "status": "<redacted>" } },
                { "$limit": 20 }
              ]
            }
            """);
        var receipt = NativePlanQueryReceipt.FromMongoDb(
            request,
            mode,
            command,
            NativePlanTestBindings.CanonicalFields);
        var binding = new NativePlanCommandBinding(
            PhysicalObject,
            null,
            null,
            receipt.Shape,
            receipt)
        {
            Fields = NativePlanTestBindings.CanonicalFields,
            MongoCommandReceipt = command
        };

        Assert.Empty(receipt.Shape.Order);
        Assert.False(Matches(
            BenchmarkProvider.MongoDb,
            mode,
            request,
            binding,
            NativePlan(BenchmarkProvider.MongoDb)));
    }

    [Fact]
    public void MongoDB_rejects_an_aggregate_page_with_sort_after_limit()
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.PaginationAndCount])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        const NativePlanAssertionMode mode = NativePlanAssertionMode.ScanCharacterization;
        var original = Binding(BenchmarkProvider.MongoDb, request, mode);
        var binding = original with
        {
            MongoCommandReceipt = new NativePlanMongoCommandReceipt(
                PhysicalDocumentQueryCommandKind.Page,
                """
                { "aggregate": "expected_table", "pipeline": [
                  { "$match": { "storage_scope": "<redacted>", "document_kind": "<redacted>", "status": "<redacted>" } },
                  { "$limit": 20 },
                  { "$sort": { "rank": -1 } }
                ] }
                """)
        };

        Assert.False(Matches(
            BenchmarkProvider.MongoDb,
            mode,
            request,
            binding,
            NativePlan(BenchmarkProvider.MongoDb)));
    }

    [Fact]
    public void MongoDB_rejects_an_aggregate_page_that_contains_a_count_terminal()
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.PaginationAndCount])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        const NativePlanAssertionMode mode = NativePlanAssertionMode.ScanCharacterization;
        var original = Binding(BenchmarkProvider.MongoDb, request, mode);
        var binding = original with
        {
            MongoCommandReceipt = new NativePlanMongoCommandReceipt(
                PhysicalDocumentQueryCommandKind.Page,
                """
                { "aggregate": "expected_table", "pipeline": [
                  { "$match": { "storage_scope": "<redacted>", "document_kind": "<redacted>", "status": "<redacted>" } },
                  { "$count": "<redacted>" },
                  { "$limit": 20 }
                ] }
                """)
        };

        Assert.False(Matches(
            BenchmarkProvider.MongoDb,
            mode,
            request,
            binding,
            NativePlan(BenchmarkProvider.MongoDb)));
    }

    [Fact]
    public void MongoDB_rejects_an_aggregate_count_with_page_stages()
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.PaginationAndCount])
            .Single(candidate => candidate.Operation == NativePlanOperation.Count);
        const NativePlanAssertionMode mode = NativePlanAssertionMode.ScanCharacterization;
        var original = Binding(BenchmarkProvider.MongoDb, request, mode);
        var binding = original with
        {
            MongoCommandReceipt = new NativePlanMongoCommandReceipt(
                PhysicalDocumentQueryCommandKind.Count,
                """
                { "aggregate": "expected_table", "pipeline": [
                  { "$match": { "storage_scope": "<redacted>", "document_kind": "<redacted>", "status": "<redacted>" } },
                  { "$limit": 20 },
                  { "$count": "<redacted>" }
                ] }
                """)
        };

        Assert.False(Matches(
            BenchmarkProvider.MongoDb,
            mode,
            request,
            binding,
            NativePlan(BenchmarkProvider.MongoDb)));
    }

    [Fact]
    public void MongoDB_receipt_binds_exact_provider_physical_fields()
    {
        var request = BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.IndexedQuery])
            .Single(candidate => candidate.Operation == NativePlanOperation.Selection);
        const NativePlanAssertionMode mode = NativePlanAssertionMode.ScanCharacterization;
        var fields = new NativePlanFieldBinding(
            "gw_storage_scope",
            "gw_document_kind",
            "gw_status",
            "gw_rank");
        var command = new NativePlanMongoCommandReceipt(
            PhysicalDocumentQueryCommandKind.Page,
            """
            { "aggregate": "expected_table", "pipeline": [
              { "$match": { "gw_storage_scope": "<redacted>", "gw_document_kind": "<redacted>", "gw_status": "<redacted>" } },
              { "$limit": 20 }
            ] }
            """);
        var receipt = NativePlanQueryReceipt.FromMongoDb(request, mode, command, fields);
        var binding = new NativePlanCommandBinding(
            PhysicalObject,
            null,
            null,
            receipt.Shape,
            receipt)
        {
            Fields = fields,
            MongoCommandReceipt = command
        };

        Assert.True(Matches(
            BenchmarkProvider.MongoDb,
            mode,
            request,
            binding,
            NativePlan(BenchmarkProvider.MongoDb)));
        Assert.False(Matches(
            BenchmarkProvider.MongoDb,
            mode,
            request,
            binding with { Fields = NativePlanTestBindings.CanonicalFields },
            NativePlan(BenchmarkProvider.MongoDb)));
    }

    private static bool Matches(
        BenchmarkProvider provider,
        NativePlanAssertionMode mode,
        BenchmarkPlanRequest request,
        NativePlanCommandBinding binding,
        string nativePlan)
    {
        var assertions = provider switch
        {
            BenchmarkProvider.Sqlite => NativePlanEvidenceAssertions.ForSqlite(mode, IndexName, nativePlan),
            BenchmarkProvider.MongoDb => NativePlanEvidenceAssertions.ForMongoDb(mode, IndexName),
            BenchmarkProvider.PostgreSql => NativePlanEvidenceAssertions.ForPostgreSql(mode),
            BenchmarkProvider.SqlServer => NativePlanEvidenceAssertions.ForSqlServer(mode),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };
        return NativePlanEvidenceAssertions.Matches(
            mode,
            request,
            provider,
            IndexName,
            PhysicalObject,
            binding,
            nativePlan,
            assertions);
    }

    private static NativePlanCommandBinding Binding(
        BenchmarkProvider provider,
        BenchmarkPlanRequest request,
        NativePlanAssertionMode mode)
    {
        if (provider == BenchmarkProvider.MongoDb)
        {
            var command = MongoCommand(request);
            var receipt = new NativePlanMongoCommandReceipt(
                request.Operation == NativePlanOperation.Selection
                    ? PhysicalDocumentQueryCommandKind.Page
                    : PhysicalDocumentQueryCommandKind.Count,
                command);
            var mongoQueryReceipt = NativePlanQueryReceipt.FromMongoDb(
                request,
                mode,
                receipt,
                NativePlanTestBindings.CanonicalFields);
            return new NativePlanCommandBinding(
                PhysicalObject,
                null,
                null,
                mongoQueryReceipt.Shape,
                mongoQueryReceipt)
            {
                Fields = NativePlanTestBindings.CanonicalFields,
                MongoCommandReceipt = receipt
            };
        }

        var sql = RelationalCommand(provider, request);
        var relationalQueryReceipt = NativePlanQueryReceipt.FromRelational(
            request,
            mode,
            sql,
            Parameters(request),
            NativePlanTestBindings.CanonicalFields);
        return new NativePlanCommandBinding(
            PhysicalObject,
            "l",
            sql,
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
        var objectName = $"{quote.Item1}{PhysicalObject}{quote.Item2}";
        var status = $"l.{quote.Item1}status{quote.Item2}";
        var filters = $"l.{quote.Item1}storage_scope{quote.Item2} = @scope AND l.{quote.Item1}document_kind{quote.Item2} = @kind AND {status} = @q0";
        if (request.Operation == NativePlanOperation.Count)
            return $"SELECT COUNT(*) FROM {objectName} AS l WHERE {filters}";

        var order = request.Ordered ? $" ORDER BY l.{quote.Item1}rank{quote.Item2} DESC" : string.Empty;
        var paging = provider == BenchmarkProvider.SqlServer
            ? " OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY"
            : " LIMIT @take OFFSET @skip";
        return $"SELECT * FROM {objectName} AS l WHERE {filters}{order}{paging}";
    }

    private static string MongoCommand(BenchmarkPlanRequest request)
    {
        var order = request.Operation == NativePlanOperation.Selection && request.Ordered
            ? ", { \"$sort\": { \"rank\": -1 } }"
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
                    "winningPlan": { "stage": "IXSCAN", "indexName": "{{IndexName}}" }
                  }
                }
                """,
            BenchmarkProvider.PostgreSql => $$"""
                [{ "Plan": { "Node Type": "Index Scan", "Relation Name": "{{PhysicalObject}}", "Index Name": "{{IndexName}}" } }]
                """,
            BenchmarkProvider.SqlServer => $$"""
                <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements><StmtSimple><QueryPlan><RelOp PhysicalOp="Index Seek"><IndexScan><Object Table="[{{PhysicalObject}}]" Index="[{{IndexName}}]" Alias="[l]" /></IndexScan></RelOp></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };
}
