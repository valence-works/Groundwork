using System.Data;
using System.Data.SqlTypes;
using System.Xml;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class SqlServerShowplanReaderTests
{
    private const string NativeShowplan = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.564">
          <BatchSequence />
        </ShowPlanXML>
        """;

    [Fact]
    public async Task Reads_the_string_shape_returned_by_Microsoft_Data_SqlClient()
    {
        var queryResults = Table("document", typeof(string), "{\"status\":\"open\"}");
        var showplanResults = Table("Microsoft SQL Server 2005 XML Showplan", typeof(string), NativeShowplan);
        await using var reader = new DataTableReader([queryResults, showplanResults]);

        var plans = await SqlServerShowplanReader.ReadAsync(reader, CancellationToken.None);

        Assert.Contains("ShowPlanXML", Assert.Single(plans), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ignores_showplan_xml_in_an_ordinary_string_result()
    {
        var queryResults = Table("document", typeof(string), NativeShowplan);
        await using var reader = new DataTableReader(queryResults);

        var plans = await SqlServerShowplanReader.ReadAsync(reader, CancellationToken.None);

        Assert.Empty(plans);
    }

    [Fact]
    public async Task Rejects_non_showplan_xml_from_the_showplan_column()
    {
        const string applicationXml = """
            <payload xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <ShowPlanXML />
            </payload>
            """;
        var result = Table("Microsoft SQL Server 2005 XML Showplan", typeof(string), applicationXml);
        await using var reader = new DataTableReader(result);

        var plans = await SqlServerShowplanReader.ReadAsync(reader, CancellationToken.None);

        Assert.Empty(plans);
    }

    [Fact]
    public async Task Ignores_showplan_xml_in_an_ordinary_SqlXml_result()
    {
        var queryResults = Table("document", typeof(SqlXml), ToSqlXml(NativeShowplan));
        await using var reader = new DataTableReader(queryResults);

        var plans = await SqlServerShowplanReader.ReadAsync(reader, CancellationToken.None);

        Assert.Empty(plans);
    }

    [Fact]
    public async Task Reads_SqlString_from_the_showplan_column()
    {
        var showplanResults = Table(
            "Microsoft SQL Server 2005 XML Showplan",
            typeof(SqlString),
            new SqlString(NativeShowplan));
        await using var reader = new DataTableReader(showplanResults);

        var plans = await SqlServerShowplanReader.ReadAsync(reader, CancellationToken.None);

        Assert.Contains("ShowPlanXML", Assert.Single(plans), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retains_only_safe_structural_showplan_content()
    {
        const string secret = "Secret:synthetic-test-value";
        const string plan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"
                         xmlns:leak="urn:namespace-payload"
                         Version="1.564"
                         Build="Secret:synthetic-test-value">
              <BatchSequence><Batch><Statements>
                <StmtSimple StatementText="SELECT 1 AS [Secret:synthetic-test-value]">
                  <QueryPlan>
                    <!-- comment-payload -->
                    <?groundwork processing-payload?>
                    <RelOp PhysicalOp="Secret:synthetic-test-value" LogicalOp="operator-payload" />
                    <RelOp NodeId="0" PhysicalOp="Index Seek">
                      <IndexScan>
                        <DefinedValues><DefinedValue>
                          <ColumnReference Column="[Secret:synthetic-test-value]" />
                        </DefinedValue></DefinedValues>
                        <Predicate>
                          <ScalarOperator ScalarString="[Secret:synthetic-test-value]" />
                        </Predicate>
                        <Object Database="[unsafe]" Schema="[unsafe]" Table="[expected_table]" Index="[declared_index]" Alias="[unsafe]" />
                      </IndexScan>
                    </RelOp>
                  </QueryPlan>
                </StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;
        var result = Table("Microsoft SQL Server 2005 XML Showplan", typeof(string), plan);
        await using var reader = new DataTableReader(result);

        var retained = Assert.Single(await SqlServerShowplanReader.ReadAsync(reader, CancellationToken.None));

        Assert.DoesNotContain(secret, retained, StringComparison.Ordinal);
        Assert.DoesNotContain("comment-payload", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("processing-payload", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("namespace-payload", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("operator-payload", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("Build=", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("StatementText", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("ScalarString", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("Column=", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("Database=", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("Schema=", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("Alias=", retained, StringComparison.Ordinal);
        Assert.Contains("Table=\"[expected_table]\"", retained, StringComparison.Ordinal);
        Assert.Contains("Index=\"[declared_index]\"", retained, StringComparison.Ordinal);
        SqlServerShowplanReader.EnsureScaleBearingIndex(retained, "declared_index", "expected_table");
    }

    [Fact]
    public void Rejects_a_declared_index_mentioned_only_in_statistics_metadata()
    {
        const string plan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <StatisticsInfo Statistics="[declared_index]" />
              <RelOp PhysicalOp="Index Seek"><Object Table="[expected_table]" Index="[other_index]" /></RelOp>
            </ShowPlanXML>
            """;

        var exception = Assert.Throws<InvalidOperationException>(
            () => SqlServerShowplanReader.EnsureScaleBearingIndex(plan, "declared_index", "expected_table"));

        Assert.Contains("declared_index", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_an_Index_Seek_on_the_declared_index()
    {
        const string plan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <RelOp PhysicalOp="Index Seek"><Object Table="[expected_table]" Index="[declared_index]" /></RelOp>
            </ShowPlanXML>
            """;

        SqlServerShowplanReader.EnsureScaleBearingIndex(plan, "declared_index", "expected_table");
    }

    [Fact]
    public void Selects_one_compatible_declared_Index_Seek()
    {
        const string plan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <RelOp PhysicalOp="Index Seek"><Object Table="[expected_table]" Index="[compatible_index]" /></RelOp>
            </ShowPlanXML>
            """;

        var selected = SqlServerShowplanReader.SelectScaleBearingIndex(
            plan,
            ["declared_index", "compatible_index"],
            "expected_table");

        Assert.Equal("compatible_index", selected);
    }

    [Fact]
    public void Rejects_a_scan_even_when_the_plan_also_seeks_the_declared_index()
    {
        const string plan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <RelOp PhysicalOp="Index Seek"><Object Table="[expected_table]" Index="[declared_index]" /></RelOp>
              <RelOp PhysicalOp="Table Scan"><Object /></RelOp>
            </ShowPlanXML>
            """;

        var exception = Assert.Throws<InvalidOperationException>(
            () => SqlServerShowplanReader.EnsureScaleBearingIndex(plan, "declared_index", "expected_table"));

        Assert.Contains("Table Scan", exception.Message, StringComparison.Ordinal);
    }

    private static DataTable Table(string columnName, Type columnType, object value)
    {
        var table = new DataTable();
        table.Columns.Add(columnName, columnType);
        table.Rows.Add(value);
        return table;
    }

    private static SqlXml ToSqlXml(string value)
    {
        using var input = new StringReader(value);
        using var reader = XmlReader.Create(input);
        return new SqlXml(reader);
    }
}
