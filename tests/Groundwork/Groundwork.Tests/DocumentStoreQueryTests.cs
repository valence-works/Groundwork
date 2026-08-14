using Groundwork.Core.Indexing;
using Groundwork.Documents.Store;
using Xunit;

namespace Groundwork.Tests;

public sealed class DocumentStoreQueryTests
{
    [Fact]
    public void NotContains_appends_to_public_enums_without_reinterpreting_existing_values()
    {
        Assert.Equal(8, (int)PortableQueryOperation.In);
        Assert.Equal(9, (int)PortableQueryOperation.NotContains);
        Assert.Equal(8, (int)QueryComparisonOperator.LessThanOrEqual);
        Assert.Equal(9, (int)QueryComparisonOperator.NotContains);
    }

    [Fact]
    public void DocumentQueryIsTheImmutableRuntimeContractForOneBoundedDeclaration()
    {
        var clauses = new List<DocumentQueryClause>
        {
            DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))
        };
        var query = new DocumentQuery(
            "workflowTriggerBinding",
            "list-by-stimulus-type",
            clauses,
            [new DocumentQueryOrder("stimulusType")],
            skip: 10,
            take: 20);

        clauses.Clear();

        Assert.Equal("workflowTriggerBinding", query.DocumentKind);
        Assert.Equal("list-by-stimulus-type", query.QueryIdentity);
        Assert.Single(query.Clauses);
        Assert.Equal(10, query.Skip);
        Assert.Equal(20, query.Take);
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<DocumentQueryClause>>(query.Clauses).Clear());
    }

    [Fact]
    public void DocumentQueryExpressesTheFullPlannedRuntimeShape()
    {
        var query = new DocumentQuery(
                "workflowTriggerBinding",
                "latest-by-stimulus-type",
                [
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http")),
                    DocumentQueryClause.Of(DocumentQueryComparison.In("stimulusType", ["http", "timer"])),
                    DocumentQueryClause.Of(DocumentQueryComparison.Contains("stimulusType", "ttp")),
                    DocumentQueryClause.Of(DocumentQueryComparison.NotContains("stimulusType", "signal")),
                    DocumentQueryClause.Of(DocumentQueryComparison.NotEqual("stimulusType", "signal")),
                    DocumentQueryClause.Of(DocumentQueryComparison.GreaterThanOrEqual("stimulusType", "http")),
                    DocumentQueryClause.Of(DocumentQueryComparison.StartsWith("stimulusType", "route")),
                    DocumentQueryClause.Of(DocumentQueryComparison.GreaterThan("stimulusType", "a")),
                    DocumentQueryClause.Of(DocumentQueryComparison.LessThan("stimulusType", "z")),
                    DocumentQueryClause.Of(DocumentQueryComparison.LessThanOrEqual("stimulusType", "zz")),
                    DocumentQueryClause.Of(DocumentQueryComparison.CollectionContains("redirectUris", "https://one.example")),
                    DocumentQueryClause.Of(DocumentQueryComparison.CollectionContainsAll(
                        "redirectUris",
                        ["https://one.example", "https://two.example"]))
                ],
                [new DocumentQueryOrder("stimulusType")],
                take: 25)
            .ThenBy(new DocumentQueryOrder("createdAt", Groundwork.Core.PhysicalStorage.PhysicalSortDirection.Descending))
            .ContinueAfter("opaque-keyset")
            .LatestPerKey("stimulusType")
            .Select(Groundwork.Core.PhysicalStorage.BoundedQueryResultOperation.First);

        Assert.Equal(2, query.Order.Count);
        Assert.Equal("opaque-keyset", query.Continuation);
        Assert.Equal("stimulusType", query.LatestPerKeyPath);
        Assert.Equal(Groundwork.Core.PhysicalStorage.BoundedQueryResultOperation.First, query.ResultOperation);
        Assert.Equal(
            Enum.GetValues<QueryComparisonOperator>().Order(),
            query.Clauses.SelectMany(clause => clause.Comparisons).Select(comparison => comparison.Operator).Order());
    }

    [Fact]
    public void Collection_contains_all_requires_a_non_empty_deduplicated_exact_set()
    {
        var comparison = DocumentQueryComparison.CollectionContainsAll("redirectUris", ["b", "a", "b"]);

        Assert.Equal(["b", "a"], comparison.Values);
        Assert.Throws<ArgumentException>(() =>
            DocumentQueryComparison.CollectionContainsAll("redirectUris", []));
        Assert.Throws<ArgumentException>(() =>
            new DocumentQueryComparison(
                "redirectUris",
                QueryComparisonOperator.CollectionContainsAll,
                ["a", null]));
    }

    [Theory]
    [InlineData(-1, null, "skip")]
    [InlineData(null, -1, "take")]
    public void NegativePagingValuesFailClearly(int? skip, int? take, string parameterName)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DocumentQuery(
                "configurationDocument",
                "find-by-key",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("key", "alpha"))],
                skip: skip,
                take: take));

        Assert.Equal(parameterName, exception.ParamName);
    }
}
