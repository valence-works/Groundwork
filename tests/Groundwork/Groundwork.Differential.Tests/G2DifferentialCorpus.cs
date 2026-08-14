using System.Globalization;
using System.Text.Json;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.Text;
using Groundwork.Documents.Store;

namespace Groundwork.Differential.Tests;

public enum G2SemanticDecision
{
    Normalize,
    Refuse
}

public enum G2ShapeKind
{
    Predicate,
    Order,
    Compound
}

public sealed record G2MixedTypeCandidate(
    string Identity,
    IndexValueKind LogicalKind,
    PortablePhysicalType PhysicalType,
    G2SemanticDecision Decision,
    string Rationale);

public sealed record G2Row(
    string Id,
    string? Text,
    decimal? Number,
    bool? Flag,
    DateTimeOffset? Instant,
    Guid? Guid,
    byte[]? Binary,
    bool OmitNullProperties = false,
    bool Accepted = true,
    string? RejectionReason = null);

public sealed record G2QueryShape(
    int Number,
    G2ShapeKind Kind,
    string QueryIdentity,
    IReadOnlyList<DocumentQueryClause> Clauses,
    IReadOnlyList<DocumentQueryOrder> Order,
    int? Skip,
    int? Take,
    G2SemanticDecision Decision,
    string DecisionId,
    string Description)
{
    public DocumentQuery ToDocumentQuery()
    {
        if (Decision == G2SemanticDecision.Refuse)
            throw new G2SemanticRefusalException(DecisionId, Description);
        return new DocumentQuery(
            G2DifferentialCorpus.DocumentKind,
            QueryIdentity,
            Clauses.Select(clause => new DocumentQueryClause(clause.Comparisons.Select(comparison =>
                new DocumentQueryComparison(
                    comparison.Path,
                    comparison.Operator,
                    comparison.Values.Select(value => G2DifferentialCorpus.ProviderValue(comparison.Path, value)).ToArray())).ToArray())).ToArray(),
            Order,
            Skip,
            Take);
    }
}

public sealed class G2SemanticRefusalException(string decisionId, string description)
    : InvalidOperationException($"G2 semantic decision '{decisionId}' refuses '{description}' before provider I/O.");

/// <summary>
/// The committed, provider-neutral edge corpus for issue #230. The provider matrix consumes only
/// the accepted rows and the normalized query projections; rejected rows and refused shapes remain
/// first-class evidence, so adding a provider cannot silently turn a refusal into a best-effort read.
/// </summary>
public static class G2DifferentialCorpus
{
    public const int ExpectedRowCount = 40;
    public const int ExpectedShapeCount = 300;
    // 16 UTF-16 units keeps the normalized Unicode search key plus SQL Server's required
    // 1,350-byte identity tie-break below the 1,700-byte nonclustered-index limit.
    public const int StringMaximumCodeUnits = 16;
    public const int DecimalPrecision = 18;
    public const int DecimalScale = 4;

    private static readonly string?[] TextValues =
    [
        null,
        string.Empty,
        " ",
        "  \t",
        "I",
        "i",
        "İ",
        "ı",
        "Straße",
        "STRASSE",
        "e\u0301",
        "é",
        "A😀",
        "😀A",
        new string('x', StringMaximumCodeUnits),
        "alpha/beta"
    ];

    private static readonly decimal?[] NumberValues =
    [
        null,
        0m,
        -1m,
        1.2345m,
        1.2344m,
        99999999999999.9999m,
        -99999999999999.9998m,
        42m
    ];

    private static readonly DateTimeOffset?[] InstantValues =
    [
        null,
        DateTimeOffset.UnixEpoch.AddTicks(-1),
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch.AddTicks(1),
        new DateTimeOffset(2024, 3, 31, 1, 59, 59, TimeSpan.FromHours(1)).AddTicks(9),
        new DateTimeOffset(2024, 10, 27, 1, 59, 59, TimeSpan.FromHours(2)).AddTicks(9),
        new DateTimeOffset(DateTime.MinValue, TimeSpan.Zero),
        new DateTimeOffset(DateTime.MaxValue, TimeSpan.Zero)
    ];

    private static readonly Guid?[] GuidValues =
    [
        null,
        Guid.Empty,
        Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
        Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"),
        Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
        Guid.Parse("deadbeef-dead-beef-dead-beefdeadbeef")
    ];

    private static readonly byte[]?[] BinaryValues =
    [
        null,
        [],
        [0],
        [0, 255],
        [1, 2, 3, 4, 5],
        [255, 0]
    ];

    private static readonly ProjectedColumnDefinition RawTextDefinition = new(
        "rawText",
        "rawText",
        PortablePhysicalType.String,
        Length: StringMaximumCodeUnits);
    private static readonly Lazy<IReadOnlyList<G2Row>> RowsValue = new(CreateRows);
    private static readonly Lazy<IReadOnlyList<G2Row>> AcceptedRowsValue = new(
        () => Rows.Where(row => row.Accepted).ToArray());
    private static readonly Lazy<IReadOnlyList<G2Row>> RejectedRowsValue = new(CreateRejectedRows);
    private static readonly Lazy<IReadOnlyList<G2QueryShape>> ShapesValue = new(CreateShapes);
    private static readonly IReadOnlyList<G2MixedTypeCandidate> MixedTypeCandidatesValue =
    [
        new(
            "number-vs-string",
            IndexValueKind.Number,
            PortablePhysicalType.String,
            G2SemanticDecision.Refuse,
            "String coercion into a numeric comparison is provider-specific."),
        new(
            "number-vs-json",
            IndexValueKind.Number,
            PortablePhysicalType.Json,
            G2SemanticDecision.Refuse,
            "Untyped JSON/BSON numeric comparison would expose provider coercion and BSON type rules.")
    ];

    public static IReadOnlyList<G2Row> Rows => RowsValue.Value;

    public static IReadOnlyList<G2Row> AcceptedRows => AcceptedRowsValue.Value;

    public static IReadOnlyList<G2Row> RejectedRows => RejectedRowsValue.Value;

    public static IReadOnlyList<G2QueryShape> Shapes => ShapesValue.Value;

    public static IReadOnlyList<G2MixedTypeCandidate> MixedTypeCandidates => MixedTypeCandidatesValue;

    public static string Serialize(G2Row row)
    {
        ValidateForAdmission(row);
        var values = new Dictionary<string, object?>
        {
            ["rawText"] = row.Text,
            ["textSearch"] = row.Text is null ? null : PortableStringComparison.CreateSearchKey(
            row.Text,
            PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase),
            ["textOrderKey"] = row.Text is null
                ? "0"
                : "1" + PortableStringComparison.CreateSearchKey(
                row.Text,
                PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase),
            ["numberValue"] = row.Number,
            ["boolValue"] = row.Flag is null ? null : row.Flag.Value ? "2" : "1",
            ["dateTicks"] = row.Instant?.UtcTicks,
            ["guidKey"] = row.Guid is null ? null : GuidKey(row.Guid.Value),
            ["binaryValue"] = row.Binary is null ? null : Convert.ToBase64String(row.Binary)
        };
        if (row.OmitNullProperties)
        {
            foreach (var key in values.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray())
                values.Remove(key);
        }
        return JsonSerializer.Serialize(values);
    }

    private static void ValidateForAdmission(G2Row row)
    {
        if (row.Text is not null)
        {
            PhysicalProjectionValueValidation.ValidateStringLength(
                row.Text,
                RawTextDefinition);
        }
        if (row.Number is { } number)
        {
            _ = ExactNumericLiteral.Parse(number.ToString(CultureInfo.InvariantCulture)).ToDecimal(
                DecimalPrecision,
                DecimalScale,
                "numberValue");
        }
    }

    public static StorageManifest CreateManifest(string instance)
    {
        var paths = new[]
        {
            new G2Path("textSearch", IndexValueKind.Keyword, PortablePhysicalType.String, StringMaximumCodeUnits * 8),
            new G2Path("textOrderKey", IndexValueKind.Keyword, PortablePhysicalType.String, StringMaximumCodeUnits * 8),
            new G2Path("numberValue", IndexValueKind.Number, PortablePhysicalType.Decimal),
            new G2Path("boolValue", IndexValueKind.Keyword, PortablePhysicalType.String, 2),
            new G2Path("dateTicks", IndexValueKind.Number, PortablePhysicalType.Int64),
            new G2Path("guidKey", IndexValueKind.Keyword, PortablePhysicalType.String, 64),
            // Base64 is the provider-neutral equality/member key. Range, prefix, and order
            // operations remain refused even though the physical representation is text.
            new G2Path("binaryValue", IndexValueKind.Keyword, PortablePhysicalType.String, 32)
        };
        var indexes = paths.Select(path => new LogicalIndexDeclaration(
            "by-" + path.Path,
            [new IndexField(path.Path)],
            path.ValueKind,
            false,
            MissingValueBehavior.IncludedAsNull)).ToArray();
        var orderIndexes = paths
            .Where(path => path.Path != "binaryValue")
            .SelectMany(path => new[]
            {
                new LogicalIndexDeclaration(
                    "order-" + path.Path + "-asc",
                    [new IndexField(path.Path)],
                    path.ValueKind,
                    false,
                    MissingValueBehavior.IncludedAsNull),
                new LogicalIndexDeclaration(
                    "order-" + path.Path + "-desc",
                    [new IndexField(path.Path)],
                    path.ValueKind,
                    false,
                    MissingValueBehavior.IncludedAsNull)
            })
            .ToArray();
        var compound = new LogicalIndexDeclaration(
            "by-text-number",
            [new IndexField("textSearch"), new IndexField("numberValue", IndexValueKind.Number)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.IncludedAsNull);
        var allIndexes = indexes.Concat(orderIndexes).Append(compound).ToArray();

        var queries = new List<BoundedQueryDeclaration>();
        foreach (var path in paths)
        {
            var operations = Operations(path.ValueKind, path.Path);
            queries.Add(new BoundedQueryDeclaration(
                "q-" + path.Path,
                "by-" + path.Path,
                operations,
                QuerySortSupport.None,
                QueryPagingSupport.None,
                BoundedQueryExecutionClass.ScaleBearing,
                supportsDisjunction: true,
                supportsTotalCount: true));
            if (path.Path == "binaryValue")
                continue;
            foreach (var direction in new[] { PhysicalSortDirection.Ascending, PhysicalSortDirection.Descending })
            {
                var suffix = direction == PhysicalSortDirection.Ascending ? "asc" : "desc";
                queries.Add(new BoundedQueryDeclaration(
                    "q-order-" + path.Path + "-" + suffix,
                    "order-" + path.Path + "-" + suffix,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    direction == PhysicalSortDirection.Ascending ? QuerySortSupport.Ascending : QuerySortSupport.Descending,
                    QueryPagingSupport.Offset,
                    BoundedQueryExecutionClass.ScaleBearing,
                    supportsDisjunction: false,
                    supportsTotalCount: true,
                    sortFields: [new BoundedQuerySortField(path.Path, direction)],
                    predicateFields: []));
            }
        }

        queries.Add(new BoundedQueryDeclaration(
            "q-compound",
            compound.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal, PortableQueryOperation.In },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsDisjunction: true,
            supportsTotalCount: true,
            predicateFields:
            [
                new BoundedQueryPredicateField("textSearch", new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal, PortableQueryOperation.In }),
                new BoundedQueryPredicateField("numberValue", new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal, PortableQueryOperation.In })
            ]));

        var projected = paths.Select(path => new ProjectedColumnDefinition(
            path.Path,
            path.Path,
            path.PhysicalType,
            path.Length,
            path.PhysicalType == PortablePhysicalType.Decimal ? DecimalPrecision : null,
            path.PhysicalType == PortablePhysicalType.Decimal ? DecimalScale : null,
            IsNullable: true)).ToArray();
        var physicalIndexes = allIndexes.Select(index =>
        {
            var columns = index.Fields.Select((field, order) => new PhysicalIndexColumnDefinition(
                    field.Path,
                    order,
                    index.Identity.EndsWith("-desc", StringComparison.Ordinal)
                        ? PhysicalSortDirection.Descending
                        : PhysicalSortDirection.Ascending))
                .ToList();
            if (index.Identity.StartsWith("order-", StringComparison.Ordinal))
            {
                columns.Add(new PhysicalIndexColumnDefinition(
                    "id_comparison_key",
                    columns.Count,
                    PhysicalSortDirection.Ascending));
            }
            return new PhysicalIndexDefinition(
                index.Identity,
                columns,
                missingValueBehavior: MissingValueBehavior.IncludedAsNull);
        }).ToArray();
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "g2_differential",
            projected,
            indexes: physicalIndexes);
        var unit = new StorageUnit(
            new StorageUnitIdentity(DocumentKind),
            "G2 differential corpus",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Global,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                allIndexes,
                queries));
        return new StorageManifest(
            new StorageManifestIdentity("g2-differential." + instance),
            new StorageManifestOwner("Groundwork issue #230"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            []);
    }

    public static string GuidKey(Guid value)
    {
        var source = value.ToByteArray();
        Span<byte> network = stackalloc byte[16];
        network[0] = source[3];
        network[1] = source[2];
        network[2] = source[1];
        network[3] = source[0];
        network[4] = source[5];
        network[5] = source[4];
        network[6] = source[7];
        network[7] = source[6];
        source.AsSpan(8).CopyTo(network[8..]);
        return Convert.ToHexString(network);
    }

    public static string? ProviderValue(string path, string? value) => path switch
    {
        "textSearch" => value is null ? null : PortableStringComparison.CreateSearchKey(
            value,
            PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase),
        "numberValue" or "dateTicks" or "guidKey" or "binaryValue" => value,
        "boolValue" => value is null ? null : value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "2" : "1",
        _ => value ?? string.Empty
    };

    private static IReadOnlyList<G2Row> CreateRows()
    {
        var rows = new List<G2Row>(ExpectedRowCount);
        for (var index = 0; index < ExpectedRowCount; index++)
        {
            rows.Add(new G2Row(
                $"row-{index:00}",
                TextValues[index % TextValues.Length],
                NumberValues[index % NumberValues.Length],
                index % 3 == 0 ? null : index % 2 == 0,
                InstantValues[index % InstantValues.Length],
                GuidValues[index % GuidValues.Length],
                BinaryValues[index % BinaryValues.Length],
                OmitNullProperties: index == 0));
        }
        return rows;
    }

    private static IReadOnlyList<G2Row> CreateRejectedRows() =>
    [
        new G2Row(
            "rejected-overlength",
            new string('z', StringMaximumCodeUnits + 1),
            1m,
            true,
            DateTimeOffset.UnixEpoch,
            Guid.Empty,
            [9],
            OmitNullProperties: false,
            Accepted: false,
            RejectionReason: "textSearch exceeds the declared UTF-16 maximum length"),
        new G2Row(
            "rejected-lone-surrogate",
            "\uD800",
            1m,
            true,
            DateTimeOffset.UnixEpoch,
            Guid.Empty,
            [9],
            OmitNullProperties: false,
            Accepted: false,
            RejectionReason: "implicit Unicode normalization and malformed UTF-16 are refused"),
        new G2Row(
            "rejected-excess-decimal-scale",
            "decimal",
            1.23456m,
            true,
            DateTimeOffset.UnixEpoch,
            Guid.Empty,
            [9],
            OmitNullProperties: false,
            Accepted: false,
            RejectionReason: "decimal scale exceeds the declared decimal(18,4) domain"),
        new G2Row(
            "rejected-decimal-max-value",
            "decimal",
            decimal.MaxValue,
            true,
            DateTimeOffset.UnixEpoch,
            Guid.Empty,
            [9],
            OmitNullProperties: false,
            Accepted: false,
            RejectionReason: "decimal value exceeds the declared decimal(18,4) precision")
    ];

    private static IReadOnlyList<G2QueryShape> CreateShapes()
    {
        var shapes = new List<G2QueryShape>(ExpectedShapeCount);
        var number = 1;
        AddPredicateShapes(shapes, ref number, "textSearch", "q-textSearch", [
            QueryComparisonOperator.Equal,
            QueryComparisonOperator.In,
            QueryComparisonOperator.Contains,
            QueryComparisonOperator.NotEqual,
            QueryComparisonOperator.StartsWith,
            QueryComparisonOperator.NotContains],
            [null, string.Empty, "I", "i", "İ", "ı", "Straße", "e\u0301", "é"],
            "portable-string-search-key");
        AddPredicateShapes(shapes, ref number, "numberValue", "q-numberValue", [
            QueryComparisonOperator.Equal,
            QueryComparisonOperator.In,
            QueryComparisonOperator.GreaterThan,
            QueryComparisonOperator.GreaterThanOrEqual,
            QueryComparisonOperator.LessThan,
            QueryComparisonOperator.LessThanOrEqual,
            QueryComparisonOperator.NotEqual],
            NumberValues.Take(6).Select(value => value?.ToString(CultureInfo.InvariantCulture)).ToArray(),
            "typed-decimal-18-4");
        AddPredicateShapes(shapes, ref number, "boolValue", "q-boolValue", [
            QueryComparisonOperator.Equal,
            QueryComparisonOperator.In,
            QueryComparisonOperator.NotEqual],
            [null, "true", "false", "True", "False", "TRUE"],
            "total-boolean-null-complement");
        AddPredicateShapes(shapes, ref number, "dateTicks", "q-dateTicks", [
            QueryComparisonOperator.Equal,
            QueryComparisonOperator.In,
            QueryComparisonOperator.GreaterThan,
            QueryComparisonOperator.GreaterThanOrEqual,
            QueryComparisonOperator.LessThan,
            QueryComparisonOperator.LessThanOrEqual,
            QueryComparisonOperator.NotEqual],
            InstantValues.Take(6).Select(value => value?.UtcTicks.ToString(CultureInfo.InvariantCulture)).ToArray(),
            "utc-ticks");
        AddPredicateShapes(shapes, ref number, "guidKey", "q-guidKey", [
            QueryComparisonOperator.Equal,
            QueryComparisonOperator.In,
            QueryComparisonOperator.NotEqual],
            RepeatToSix(GuidValues.Select(value => value is null ? null : GuidKey(value.Value))),
            "rfc4122-network-guid-key");
        AddPredicateShapes(shapes, ref number, "binaryValue", "q-binaryValue", [
            QueryComparisonOperator.Equal,
            QueryComparisonOperator.In,
            QueryComparisonOperator.GreaterThan,
            QueryComparisonOperator.LessThan,
            QueryComparisonOperator.StartsWith],
            RepeatToSix(BinaryValues.Select(value => value is null ? null : Convert.ToBase64String(value))),
            "binary-equality-membership");

        var orderPaths = new[] { "textOrderKey", "numberValue", "boolValue", "dateTicks", "guidKey", "binaryValue" };
        foreach (var path in orderPaths)
        {
            foreach (var direction in new[] { PhysicalSortDirection.Ascending, PhysicalSortDirection.Descending })
            {
                for (var page = 0; page < 6; page++)
                {
                    var refused = path == "binaryValue";
                    var suffix = direction == PhysicalSortDirection.Ascending ? "asc" : "desc";
                    shapes.Add(new G2QueryShape(
                        number++,
                        G2ShapeKind.Order,
                        "q-order-" + path + "-" + suffix,
                        [],
                        [new DocumentQueryOrder(path, direction)],
                        page * 2,
                        2,
                        refused ? G2SemanticDecision.Refuse : G2SemanticDecision.Normalize,
                        refused ? "binary-order-refused" : path == "textOrderKey" ? "explicit-null-ordering" : "normalized-ordering",
                        $"{path} {direction} page {page}"));
                }
            }
        }

        var textValues = TextValues
            .Where(value => value is not null)
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
        var numericValues = NumberValues.Where(value => value is not null).Select(value => value!.Value.ToString(CultureInfo.InvariantCulture)).Take(6).ToArray();
        for (var index = 0; index < 18; index++)
        {
            var pair = index % 9;
            var text = textValues[pair % textValues.Length];
            var numeric = numericValues[pair / textValues.Length];
            var disjunction = index >= 9;
            IReadOnlyList<DocumentQueryClause> clauses;
            if (disjunction)
            {
                clauses = new[]
                {
                    DocumentQueryClause.AnyOf(
                        DocumentQueryComparison.Equal("textSearch", text),
                        DocumentQueryComparison.Equal("numberValue", numeric))
                };
            }
            else
            {
                clauses = new[]
                {
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("textSearch", text)),
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("numberValue", numeric))
                };
            }
            shapes.Add(new G2QueryShape(
                number++,
                G2ShapeKind.Compound,
                "q-compound",
                clauses,
                [],
                null,
                null,
                G2SemanticDecision.Normalize,
                disjunction ? "compound-disjunction" : "compound-conjunction",
                disjunction ? "text OR number" : "text AND number"));
        }
        if (shapes.Count != ExpectedShapeCount)
            throw new InvalidOperationException($"G2 corpus generated {shapes.Count} shapes, expected {ExpectedShapeCount}.");
        return shapes;
    }

    private static void AddPredicateShapes(
        ICollection<G2QueryShape> shapes,
        ref int number,
        string path,
        string queryIdentity,
        IReadOnlyList<QueryComparisonOperator> operators,
        IReadOnlyList<string?> values,
        string decisionId)
    {
        foreach (var @operator in operators)
        {
            foreach (var value in values)
            {
                var nullRange = value is null && @operator is
                    (QueryComparisonOperator.GreaterThan or QueryComparisonOperator.GreaterThanOrEqual or
                    QueryComparisonOperator.LessThan or QueryComparisonOperator.LessThanOrEqual);
                var nullSearch = value is null && @operator is
                    (QueryComparisonOperator.Contains or QueryComparisonOperator.NotContains or QueryComparisonOperator.StartsWith);
                var invalidNull = nullRange || nullSearch;
                var comparison = CreateComparison(path, @operator, invalidNull ? "\u0000" : value);
                var refused = invalidNull ||
                    path == "binaryValue" && @operator is not (QueryComparisonOperator.Equal or QueryComparisonOperator.In) ||
                    path == "textSearch" && @operator is QueryComparisonOperator.StartsWith or QueryComparisonOperator.NotContains;
                var refusalId = nullRange
                    ? "null-range-refused"
                    : nullSearch
                        ? "null-search-refused"
                        : path == "binaryValue"
                            ? "binary-range-prefix-order-refused"
                            : "cross-provider-index-certification-refused";
                shapes.Add(new G2QueryShape(
                    number++,
                    G2ShapeKind.Predicate,
                    queryIdentity,
                    [DocumentQueryClause.Of(comparison)],
                    [],
                    null,
                    null,
                    refused ? G2SemanticDecision.Refuse : G2SemanticDecision.Normalize,
                    refused ? refusalId : decisionId,
                    $"{path} {@operator} {(value is null ? "NULL" : value)}"));
            }

            if (@operator == QueryComparisonOperator.In)
            {
                shapes.Add(new G2QueryShape(
                    number++,
                    G2ShapeKind.Predicate,
                    queryIdentity,
                    [DocumentQueryClause.Of(DocumentQueryComparison.In(path, []))],
                    [],
                    null,
                    null,
                    G2SemanticDecision.Normalize,
                    decisionId,
                    $"{path} In [] (empty membership is false)"));
            }
        }
    }

    private static DocumentQueryComparison CreateComparison(string path, QueryComparisonOperator @operator, string? value)
    {
        if (@operator == QueryComparisonOperator.In)
            return DocumentQueryComparison.In(path, value is null ? [null] : [value, null]);
        if (@operator is QueryComparisonOperator.Contains or QueryComparisonOperator.NotContains or QueryComparisonOperator.StartsWith)
            return DocumentQueryComparisonComparisonNonNull(path, @operator, value);
        return new DocumentQueryComparison(path, @operator, [value]);
    }

    private static DocumentQueryComparison DocumentQueryComparisonComparisonNonNull(
        string path,
        QueryComparisonOperator @operator,
        string? value) => new(path, @operator, [value ?? string.Empty]);

    private sealed record G2Path(string Path, IndexValueKind ValueKind, PortablePhysicalType PhysicalType, int? Length = null);

    private static IReadOnlyList<string?> RepeatToSix(IEnumerable<string?> values)
    {
        var source = values.ToArray();
        return Enumerable.Range(0, 6).Select(index => source[index % source.Length]).ToArray();
    }

    private static IReadOnlySet<PortableQueryOperation> Operations(IndexValueKind kind, string path) =>
        path == "textOrderKey"
            ? new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }
            : path == "textSearch"
                ? new HashSet<PortableQueryOperation>
                {
                    PortableQueryOperation.Equal,
                    PortableQueryOperation.In,
                    PortableQueryOperation.Contains,
                    PortableQueryOperation.NotEqual
                }
            : path == "binaryValue"
            ? new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal, PortableQueryOperation.In, PortableQueryOperation.NotEqual }
            : path == "boolValue"
                ? new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal, PortableQueryOperation.In, PortableQueryOperation.NotEqual }
                : kind == IndexValueKind.Keyword && path == "guidKey"
                    ? new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal, PortableQueryOperation.In, PortableQueryOperation.NotEqual }
                    : kind == IndexValueKind.Keyword
                        ? new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal, PortableQueryOperation.In, PortableQueryOperation.Contains, PortableQueryOperation.NotEqual, PortableQueryOperation.StartsWith, PortableQueryOperation.NotContains }
                        : new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal, PortableQueryOperation.In, PortableQueryOperation.NotEqual, PortableQueryOperation.GreaterThan, PortableQueryOperation.GreaterThanOrEqual, PortableQueryOperation.LessThan, PortableQueryOperation.LessThanOrEqual };

    public const string DocumentKind = "g2-edge-row";
}

public static class G2Oracle
{
    private static readonly IReadOnlyDictionary<string, string> PinnedUnicodeComparisonKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [string.Empty] = string.Empty,
            [" "] = "000020",
            ["  \t"] = "000020000020000009",
            ["I"] = "000049",
            ["i"] = "000049",
            ["İ"] = "000130",
            ["ı"] = "000131",
            ["Straße"] = "0000530000540000520000410000DF000045",
            ["STRASSE"] = "000053000054000052000041000053000053000045",
            ["e\u0301"] = "000045000301",
            ["é"] = "0000C9",
            ["A😀"] = "00004101F600",
            ["😀A"] = "01F600000041",
            ["xxxxxxxxxxxxxxxx"] = "000058000058000058000058000058000058000058000058000058000058000058000058000058000058000058000058",
            ["alpha/beta"] = "00004100004C00005000004800004100002F000042000045000054000041"
        };

    public static IReadOnlyList<string> Evaluate(IReadOnlyList<G2Row> rows, G2QueryShape shape)
    {
        var candidates = rows.Where(row => Matches(row, shape.Clauses)).ToArray();
        foreach (var order in shape.Order)
        {
            var ascending = order.Direction == PhysicalSortDirection.Ascending;
            var key = candidates.Select(row => (Row: row, Value: OrderValue(row, order.Path))).ToArray();
            candidates = (ascending
                    ? key.OrderBy(item => item.Value, StringComparer.Ordinal)
                    : key.OrderByDescending(item => item.Value, StringComparer.Ordinal))
                .ThenBy(item => PortableStringComparison.CreateOrdinal(item.Row.Id), StringComparer.Ordinal)
                .Select(item => item.Row)
                .ToArray();
        }
        if (shape.Order.Count == 0)
            candidates = candidates.OrderBy(row => PortableStringComparison.CreateOrdinal(row.Id), StringComparer.Ordinal).ToArray();
        return candidates
            .Skip(shape.Skip ?? 0)
            .Take(shape.Take ?? int.MaxValue)
            .Select(row => row.Id)
            .ToArray();
    }

    private static bool Matches(G2Row row, IReadOnlyList<DocumentQueryClause> clauses) => clauses.All(clause =>
        clause.Comparisons.Count != 0 && clause.Comparisons.Any(comparison => Match(row, comparison)));

    private static bool Match(G2Row row, DocumentQueryComparison comparison)
    {
        var actual = comparison.Path switch
        {
            "textSearch" => row.Text is null
                ? null
                : IndependentUnicodeOrdinalIgnoreCase(row.Text),
            "numberValue" => row.Number?.ToString(CultureInfo.InvariantCulture),
            "boolValue" => row.Flag is null ? null : row.Flag.Value ? "2" : "1",
            "dateTicks" => row.Instant?.UtcTicks.ToString(CultureInfo.InvariantCulture),
            "guidKey" => row.Guid is null ? null : G2DifferentialCorpus.GuidKey(row.Guid.Value),
            "binaryValue" => row.Binary is null ? null : Convert.ToBase64String(row.Binary),
            _ => throw new InvalidOperationException($"Unknown G2 path '{comparison.Path}'.")
        };
        var values = comparison.Values.Select(value => OracleValue(comparison.Path, value)).ToArray();
        return comparison.Operator switch
        {
            QueryComparisonOperator.Equal => StringEquals(actual, values[0]),
            QueryComparisonOperator.NotEqual => !StringEquals(actual, values[0]),
            QueryComparisonOperator.In => values.Any(value => StringEquals(actual, value)),
            QueryComparisonOperator.Contains => actual is not null && actual.Contains(values[0]!, StringComparison.Ordinal),
            QueryComparisonOperator.NotContains => actual is null || !actual.Contains(values[0]!, StringComparison.Ordinal),
            QueryComparisonOperator.StartsWith => actual is not null && actual.StartsWith(values[0]!, StringComparison.Ordinal),
            QueryComparisonOperator.GreaterThan => actual is not null && Compare(actual, values[0], comparison.Path) > 0,
            QueryComparisonOperator.GreaterThanOrEqual => actual is not null && Compare(actual, values[0], comparison.Path) >= 0,
            QueryComparisonOperator.LessThan => actual is not null && Compare(actual, values[0], comparison.Path) < 0,
            QueryComparisonOperator.LessThanOrEqual => actual is not null && Compare(actual, values[0], comparison.Path) <= 0,
            _ => throw new InvalidOperationException($"Unsupported G2 oracle operation '{comparison.Operator}'.")
        };
    }

    private static int Compare(string? left, string? right, string path)
    {
        if (left is null && right is null)
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;
        if (path is "numberValue" or "dateTicks")
            return decimal.Parse(left, CultureInfo.InvariantCulture)
                .CompareTo(decimal.Parse(right, CultureInfo.InvariantCulture));
        return StringComparer.Ordinal.Compare(left, right);
    }

    private static bool StringEquals(string? left, string? right) =>
        left is null || right is null ? left is null && right is null : StringComparer.Ordinal.Equals(left, right);

    private static string? OracleValue(string path, string? value) => path switch
    {
        "textSearch" => value is null ? null : IndependentUnicodeOrdinalIgnoreCase(value),
        "numberValue" or "dateTicks" or "guidKey" or "binaryValue" => value,
        "boolValue" => value is null ? null : value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "2" : "1",
        _ => value ?? string.Empty
    };

    private static string OrderValue(G2Row row, string path) => path switch
    {
        "textOrderKey" => row.Text is null
            ? "0"
            : "1" + IndependentUnicodeOrdinalIgnoreCase(row.Text),
        "numberValue" => row.Number is { } number
            ? (number + 100000000000000000m).ToString("000000000000000000.0000", CultureInfo.InvariantCulture)
            : "000000000000000000.0000",
        "boolValue" => row.Flag is null ? "0" : row.Flag.Value ? "2" : "1",
        "dateTicks" => row.Instant is { } instant
            ? (instant.UtcTicks + 10000000000000000000m).ToString("00000000000000000000", CultureInfo.InvariantCulture)
            : "00000000000000000000",
        "guidKey" => row.Guid is null ? string.Empty : G2DifferentialCorpus.GuidKey(row.Guid.Value),
        "binaryValue" => row.Binary is null ? string.Empty : Convert.ToHexString(row.Binary),
        _ => throw new InvalidOperationException($"Unknown G2 order path '{path}'.")
    };

    // Deliberately independent of Groundwork's runtime key encoder and Unicode tables. These
    // reviewed golden vectors pin the finite edge corpus, so a runtime/framework mapping change
    // cannot silently move both the providers and oracle together.
    private static string IndependentUnicodeOrdinalIgnoreCase(string value) =>
        PinnedUnicodeComparisonKeys.TryGetValue(value, out var key)
            ? key
            : throw new InvalidOperationException($"No pinned G2 Unicode comparison vector exists for '{value}'.");
}
