using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Documents.Store;

/// <summary>
/// The closed set of comparison operators a document query may use against a declared bounded query.
/// </summary>
public enum QueryComparisonOperator
{
    /// <summary>Exact equality. A <see langword="null"/> value matches documents whose field is null/absent.</summary>
    Equal,

    /// <summary>Set membership (SQL <c>IN</c>). An empty value set matches nothing.</summary>
    In,

    /// <summary>Case-insensitive substring match (SQL <c>LIKE '%v%'</c>). A null/absent field yields no match.</summary>
    Contains,

    /// <summary>
    /// The exact complement of <see cref="Equal"/>: a document matches this precisely when it does not
    /// match <see cref="Equal"/> for the same value. A null/absent field therefore matches a non-null
    /// value, and matches nothing when the value is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// This is deliberately not SQL's three-valued <c>&lt;&gt;</c>, which is unknown for null and drops
    /// those rows. Taking the complement keeps <see cref="Equal"/> and <see cref="NotEqual"/> a partition
    /// of the documents for every value, matches the reading <see cref="NotContains"/> already has, and
    /// means no document can fall through both halves of a two-branch query. Relational providers render
    /// the null branch explicitly to get there.
    /// </remarks>
    NotEqual,
    StartsWith,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,

    /// <summary>Case-insensitive complement of <see cref="Contains"/>. A null/absent field matches.</summary>
    NotContains,
    CollectionContains,
    CollectionContainsAll
}

/// <summary>One runtime comparison addressed by the stable path declared by a bounded query.</summary>
public sealed class DocumentQueryComparison
{
    public DocumentQueryComparison(string path, QueryComparisonOperator @operator, IReadOnlyList<string?> values)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A stable predicate path is required.", nameof(path));
        ArgumentNullException.ThrowIfNull(values);
        var snapshot = values.ToArray();
        if (@operator is not (QueryComparisonOperator.In or QueryComparisonOperator.CollectionContainsAll) && snapshot.Length != 1)
            throw new ArgumentException($"{@operator} requires exactly one value.", nameof(values));
        if (@operator == QueryComparisonOperator.CollectionContainsAll && snapshot.Length == 0)
            throw new ArgumentException("CollectionContainsAll requires at least one value.", nameof(values));
        if ((@operator is QueryComparisonOperator.Contains or QueryComparisonOperator.NotContains or QueryComparisonOperator.StartsWith or
             QueryComparisonOperator.GreaterThan or QueryComparisonOperator.GreaterThanOrEqual or
             QueryComparisonOperator.LessThan or QueryComparisonOperator.LessThanOrEqual or
             QueryComparisonOperator.CollectionContains or QueryComparisonOperator.CollectionContainsAll) &&
            snapshot.Any(value => value is null))
        {
            throw new ArgumentException($"{@operator} does not accept a null value.", nameof(values));
        }

        Path = path;
        Operator = @operator;
        if (@operator == QueryComparisonOperator.CollectionContainsAll)
            snapshot = snapshot.Distinct(StringComparer.Ordinal).ToArray();
        Values = Array.AsReadOnly(snapshot);
    }

    public string Path { get; }
    public QueryComparisonOperator Operator { get; }
    public IReadOnlyList<string?> Values { get; }

    public static DocumentQueryComparison Equal(string path, string? value) => new(path, QueryComparisonOperator.Equal, [value]);
    public static DocumentQueryComparison In(string path, IEnumerable<string?> values) =>
        new(path, QueryComparisonOperator.In, (values ?? throw new ArgumentNullException(nameof(values))).ToArray());
    public static DocumentQueryComparison Contains(string path, string value) => new(path, QueryComparisonOperator.Contains, [value]);
    public static DocumentQueryComparison NotContains(string path, string value) => new(path, QueryComparisonOperator.NotContains, [value]);
    public static DocumentQueryComparison NotEqual(string path, string? value) => new(path, QueryComparisonOperator.NotEqual, [value]);
    public static DocumentQueryComparison StartsWith(string path, string value) => new(path, QueryComparisonOperator.StartsWith, [value]);
    public static DocumentQueryComparison GreaterThan(string path, string value) => new(path, QueryComparisonOperator.GreaterThan, [value]);
    public static DocumentQueryComparison GreaterThanOrEqual(string path, string value) => new(path, QueryComparisonOperator.GreaterThanOrEqual, [value]);
    public static DocumentQueryComparison LessThan(string path, string value) => new(path, QueryComparisonOperator.LessThan, [value]);
    public static DocumentQueryComparison LessThanOrEqual(string path, string value) => new(path, QueryComparisonOperator.LessThanOrEqual, [value]);
    public static DocumentQueryComparison CollectionContains(string path, string value) => new(path, QueryComparisonOperator.CollectionContains, [value]);
    public static DocumentQueryComparison CollectionContainsAll(string path, IEnumerable<string> values) =>
        new(path, QueryComparisonOperator.CollectionContainsAll,
            (values ?? throw new ArgumentNullException(nameof(values))).Cast<string?>().ToArray());
}

/// <summary>An OR clause in the bounded runtime query; clauses compose with AND.</summary>
public sealed class DocumentQueryClause
{
    public DocumentQueryClause(IReadOnlyList<DocumentQueryComparison> comparisons) =>
        Comparisons = Array.AsReadOnly((comparisons ?? throw new ArgumentNullException(nameof(comparisons))).ToArray());

    public IReadOnlyList<DocumentQueryComparison> Comparisons { get; }

    public static DocumentQueryClause MatchNone { get; } = new(Array.Empty<DocumentQueryComparison>());
    public static DocumentQueryClause AnyOf(params DocumentQueryComparison[] comparisons) => new(comparisons);
    public static DocumentQueryClause Of(DocumentQueryComparison comparison) => new([comparison]);
}

public sealed record DocumentQueryOrder(string Path, PhysicalSortDirection Direction = PhysicalSortDirection.Ascending);

/// <summary>
/// The single runtime request model for one declared bounded document query. Scope is not a caller
/// field: it is always inherited from the document-store session and injected by physical planning.
/// </summary>
public sealed class DocumentQuery
{
    public DocumentQuery(
        string documentKind,
        string queryIdentity,
        IReadOnlyList<DocumentQueryClause>? clauses = null,
        IReadOnlyList<DocumentQueryOrder>? order = null,
        int? skip = null,
        int? take = null,
        string? continuation = null,
        string? latestPerKeyPath = null,
        BoundedQueryResultOperation resultOperation = BoundedQueryResultOperation.Documents)
    {
        if (string.IsNullOrWhiteSpace(documentKind))
            throw new ArgumentException("Document kind must be provided.", nameof(documentKind));
        if (string.IsNullOrWhiteSpace(queryIdentity))
            throw new ArgumentException("Bounded query identity must be provided.", nameof(queryIdentity));
        if (skip is < 0)
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip must be greater than or equal to 0.");
        if (take is < 0)
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be greater than or equal to 0.");
        if (skip is not null && continuation is not null)
            throw new ArgumentException("Offset and keyset paging cannot be requested together.", nameof(continuation));
        if (continuation is not null && string.IsNullOrWhiteSpace(continuation))
            throw new ArgumentException("A keyset continuation cannot be empty.", nameof(continuation));
        if (latestPerKeyPath is not null && string.IsNullOrWhiteSpace(latestPerKeyPath))
            throw new ArgumentException("A latest-per-key stable path cannot be empty.", nameof(latestPerKeyPath));
        if (order is not null &&
            (order.Any(field => string.IsNullOrWhiteSpace(field.Path)) ||
             order.Select(field => field.Path).Distinct(StringComparer.Ordinal).Count() != order.Count))
            throw new ArgumentException("Compound order paths must be unique.", nameof(order));

        DocumentKind = documentKind;
        QueryIdentity = queryIdentity;
        Clauses = Array.AsReadOnly(clauses?.ToArray() ?? []);
        Order = Array.AsReadOnly(order?.ToArray() ?? []);
        Skip = skip;
        Take = take;
        Continuation = continuation;
        LatestPerKeyPath = latestPerKeyPath;
        ResultOperation = resultOperation;
    }

    public string DocumentKind { get; }
    public string QueryIdentity { get; }
    public IReadOnlyList<DocumentQueryClause> Clauses { get; }
    public IReadOnlyList<DocumentQueryOrder> Order { get; }
    public int? Skip { get; }
    public int? Take { get; }
    public string? Continuation { get; }
    public string? LatestPerKeyPath { get; }
    public BoundedQueryResultOperation ResultOperation { get; }

    public DocumentQuery Where(DocumentQueryClause clause) =>
        Copy(clauses: Clauses.Concat([clause]).ToArray());

    public DocumentQuery OrderBy(DocumentQueryOrder order) => Copy(order: [order]);

    public DocumentQuery ThenBy(DocumentQueryOrder order) => Copy(order: Order.Append(order).ToArray());

    public DocumentQuery Page(int? skip, int? take) =>
        new(DocumentKind, QueryIdentity, Clauses, Order, skip, take, null, LatestPerKeyPath, ResultOperation);

    public DocumentQuery ContinueAfter(string continuation) =>
        new(
            DocumentKind, QueryIdentity, Clauses, Order, null, Take,
            continuation ?? throw new ArgumentNullException(nameof(continuation)), LatestPerKeyPath, ResultOperation);

    public DocumentQuery LatestPerKey(string path) =>
        Copy(latestPerKeyPath: path ?? throw new ArgumentNullException(nameof(path)));

    public DocumentQuery Select(BoundedQueryResultOperation operation) => Copy(resultOperation: operation);

    private DocumentQuery Copy(
        IReadOnlyList<DocumentQueryClause>? clauses = null,
        IReadOnlyList<DocumentQueryOrder>? order = null,
        string? latestPerKeyPath = null,
        BoundedQueryResultOperation? resultOperation = null) =>
        new(
            DocumentKind,
            QueryIdentity,
            clauses ?? Clauses,
            order ?? Order,
            Skip,
            Take,
            Continuation,
            latestPerKeyPath ?? LatestPerKeyPath,
            resultOperation ?? ResultOperation);
}

/// <summary>
/// The result of a bounded query: the page window, the total predicate count, and an opaque
/// continuation when the declared cursor route has another page.
/// </summary>
public sealed record DocumentQueryResult(
    IReadOnlyList<DocumentEnvelope> Documents,
    long TotalCount,
    string? NextContinuation = null)
{
    public static DocumentQueryResult Empty { get; } = new(Array.Empty<DocumentEnvelope>(), 0);
}
