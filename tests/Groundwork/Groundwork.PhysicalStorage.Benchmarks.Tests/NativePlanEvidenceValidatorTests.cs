using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class NativePlanEvidenceValidatorTests
{
    private const string PhysicalObject = "expected_table";
    private const string IndexName = "expected_index";

    public static TheoryData<BenchmarkProvider, NativePlanCommandBinding, string> ValidPlans => new()
    {
        {
            BenchmarkProvider.Sqlite,
            new NativePlanCommandBinding(
                PhysicalObject,
                "l",
                "SELECT * FROM \"expected_table\" AS l INDEXED BY \"expected_index\" WHERE l.\"status\" = @value"),
            "SEARCH l USING INDEX expected_index (status=?)"
        },
        {
            BenchmarkProvider.MongoDb,
            new NativePlanCommandBinding(PhysicalObject, null, null),
            """
            {
              "queryPlanner": {
                "namespace": "fixture.expected_table",
                "winningPlan": { "stage": "COLLSCAN" }
              }
            }
            """
        },
        {
            BenchmarkProvider.PostgreSql,
            new NativePlanCommandBinding(
                PhysicalObject,
                "l",
                "SELECT * FROM \"expected_table\" l WHERE l.\"status\" = @value"),
            """
            [
              {
                "Plan": {
                  "Node Type": "Seq Scan",
                  "Relation Name": "expected_table"
                }
              }
            ]
            """
        },
        {
            BenchmarkProvider.SqlServer,
            new NativePlanCommandBinding(
                PhysicalObject,
                "l",
                "SELECT * FROM [expected_table] AS l WHERE l.[status] = @value"),
            """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence>
                <Batch>
                  <Statements>
                    <StmtSimple>
                      <QueryPlan>
                        <RelOp PhysicalOp="Table Scan">
                          <TableScan><Object Table="[expected_table]" Alias="[l]" /></TableScan>
                        </RelOp>
                      </QueryPlan>
                    </StmtSimple>
                  </Statements>
                </Batch>
              </BatchSequence>
            </ShowPlanXML>
            """
        }
    };

    [Theory]
    [MemberData(nameof(ValidPlans))]
    public void Scan_characterization_requires_valid_provider_native_structure_bound_to_the_expected_object(
        BenchmarkProvider provider,
        NativePlanCommandBinding binding,
        string nativePlan)
    {
        Assert.True(Matches(provider, binding, nativePlan));
        Assert.False(Matches(provider, binding with { PhysicalObject = "drifted_table" }, nativePlan));
        Assert.False(Matches(provider, binding, "not a provider-native plan"));
    }

    [Fact]
    public void SQLite_rejects_a_literal_bearing_command_and_an_unbound_plan_alias()
    {
        var binding = new NativePlanCommandBinding(
            PhysicalObject,
            "l",
            "SELECT * FROM \"expected_table\" AS l WHERE l.\"status\" = 'open'");

        Assert.False(Matches(
            BenchmarkProvider.Sqlite,
            binding,
            "SEARCH l USING INDEX expected_index (status=?)"));
        Assert.False(Matches(
            BenchmarkProvider.Sqlite,
            binding with
            {
                ParameterizedCommand =
                    "SELECT * FROM \"expected_table\" AS l INDEXED BY \"expected_index\" WHERE l.\"status\" = @value"
            },
            "SEARCH forged USING INDEX expected_index (status=?)"));
    }

    [Fact]
    public void Require_index_rejects_a_PostgreSQL_index_selected_on_another_relation()
    {
        var binding = new NativePlanCommandBinding(
            PhysicalObject,
            "l",
            "SELECT * FROM \"expected_table\" l WHERE l.\"status\" = @value");
        const string nativePlan = """
            [
              {
                "Plan": {
                  "Node Type": "Nested Loop",
                  "Plans": [
                    {
                      "Node Type": "Seq Scan",
                      "Relation Name": "expected_table"
                    },
                    {
                      "Node Type": "Index Scan",
                      "Relation Name": "other_table",
                      "Index Name": "expected_index"
                    }
                  ]
                }
              }
            ]
            """;

        Assert.False(NativePlanEvidenceAssertions.Matches(
            NativePlanAssertionMode.RequireDeclaredIndex,
            BenchmarkProvider.PostgreSql,
            IndexName,
            PhysicalObject,
            binding,
            nativePlan,
            NativePlanEvidenceAssertions.ForPostgreSql(NativePlanAssertionMode.RequireDeclaredIndex)));
    }

    private static bool Matches(
        BenchmarkProvider provider,
        NativePlanCommandBinding binding,
        string nativePlan) =>
        NativePlanEvidenceAssertions.Matches(
            NativePlanAssertionMode.ScanCharacterization,
            provider,
            IndexName,
            PhysicalObject,
            binding,
            nativePlan,
            NativePlanEvidenceAssertions.For(NativePlanAssertionMode.ScanCharacterization, []));
}
