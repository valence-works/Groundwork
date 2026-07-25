using System.Globalization;

namespace Groundwork.DiagnosticRecords.Relational;

/// <summary>
/// Builds one bounded provider-native grouped reduction. The returned rows are only a serialization
/// of the already reduced page; raw records or raw groups are never returned for client evaluation.
/// </summary>
internal sealed class RelationalDiagnosticGroupQueryBuilder(
    DiagnosticRecordStreamDefinition definition,
    RelationalDiagnosticRecordDialect dialect)
{
    private readonly Dictionary<string, object> parameters = new(StringComparer.Ordinal);
    private int parameterIndex;

    public RelationalDiagnosticCommand Build(
        DiagnosticRecordGroupQuery query,
        DiagnosticGroupReductionProfile profile,
        long snapshotHighWater)
    {
        AddNamed("tenant", query.Scope.TenantId);
        AddNamed("scope", query.Scope.ScopeId);
        AddNamed("stream", query.Stream.Value);
        AddNamed("snapshot", snapshotHighWater);
        AddNamed("inputLimit", query.InputRecordLimit);
        AddNamed("take", query.Take + 1);
        AddNamed("returnedTake", query.Take);
        AddNamed("groupField", profile.GroupKeyField);
        AddNamed("groupFieldType", (int)DiagnosticFieldType.String);
        AddNamed("maxUnion", profile.MaxUnionValues);

        var ctes = new List<string>
        {
            $"""
            input_window AS (
                {dialect.ApplyLimit(
                    $"""
                    SELECT r.tenant_id, r.scope_id, r.stream_id, r.cursor, r.occurred_at_ticks
                    FROM {dialect.TableReference(RelationalDiagnosticRecordSchema.RecordsTable, "r")}
                    WHERE r.tenant_id = {dialect.Parameter("tenant")}
                      AND r.scope_id = {dialect.Parameter("scope")}
                      AND r.stream_id = {dialect.Parameter("stream")}
                      AND r.cursor <= {dialect.Parameter("snapshot")}
                    ORDER BY r.cursor DESC
                    """,
                    "inputLimit")}
            )
            """,
            $"""
            base_records AS (
                SELECT r.tenant_id, r.scope_id, r.stream_id, r.cursor, r.occurred_at_ticks,
                       g.canonical_value AS group_key, g.comparison_key AS group_key_order
                FROM input_window r
                JOIN {dialect.TableReference(RelationalDiagnosticRecordSchema.FieldsTable, "g")}
                  ON {FieldJoin("g", "r")}
                 AND g.field_name = {dialect.Parameter("groupField")}
                 AND g.field_type = {dialect.Parameter("groupFieldType")}
                 AND g.value_ordinal = 0
            )
            """,
            """
            group_keys_ranked AS (
                SELECT group_key_order, group_key,
                       ROW_NUMBER() OVER (
                           PARTITION BY group_key_order
                           ORDER BY cursor, group_key
                       ) AS key_rank
                FROM base_records
            )
            """,
            """
            group_keys AS (
                SELECT group_key_order, group_key
                FROM group_keys_ranked
                WHERE key_rank = 1
            )
            """
        };

        var reducers = profile.Reducers.Select((reducer, index) =>
            BuildReducer(reducer, index, ctes)).ToArray();
        var joins = string.Join(Environment.NewLine, reducers
            .Where(reducer => !reducer.IsUnion)
            .Select(reducer =>
                $"LEFT JOIN {reducer.CteName} {reducer.JoinAlias} ON {reducer.JoinAlias}.group_key_order = g.group_key_order"));
        var projections = string.Join(", ", reducers
            .Where(reducer => !reducer.IsUnion)
            .SelectMany(reducer => new[]
            {
                $"{reducer.JoinAlias}.value AS {reducer.ValueColumn}",
                $"{reducer.JoinAlias}.value_key AS {reducer.KeyColumn}",
                $"{reducer.JoinAlias}.search_key AS {reducer.SearchColumn}"
            }));
        ctes.Add($"""
            reduced AS (
                SELECT g.group_key_order, g.group_key{(projections.Length == 0 ? "" : $", {projections}")}
                FROM group_keys g
                {joins}
            )
            """);

        var orderReducer = reducers.Single(reducer =>
            StringComparer.Ordinal.Equals(reducer.Definition.Alias, query.Order.Alias));
        var orderField = DiagnosticGroupReductionProfileValidator.OutputField(
            definition,
            profile,
            query.Order.Alias);
        var orderValue = orderReducer.IsUnion
            ? throw new InvalidOperationException("Set-union outputs cannot order grouped pages.")
            : orderReducer.ValueColumn;
        var orderKey = orderReducer.KeyColumn;
        var predicates = new List<string> { $"{orderValue} IS NOT NULL" };
        if (query.Predicate is not null)
            predicates.Add(BuildPredicate(query.Predicate, reducers));
        if (query.Continuation is not null)
            predicates.Add(BuildContinuation(
                query.Continuation,
                query.Order,
                orderReducer.Definition,
                orderField,
                DiagnosticRecordFieldResolver.Resolve(definition, profile.GroupKeyField)!,
                orderKey));

        var primaryDirection = query.Order.Direction == DiagnosticSortDirection.Ascending ? "ASC" : "DESC";
        const string tieDirection = "ASC";
        var totalOrder = $"{orderKey} {primaryDirection}, group_key_order {tieDirection}";
        ctes.Add($"""
            selected_groups AS (
                SELECT *
                FROM reduced
                WHERE {string.Join(" AND ", predicates)}
            )
            """);
        var pageSql = dialect.ApplyLimit(
            $"SELECT * FROM selected_groups ORDER BY {totalOrder}",
            "take");
        ctes.Add($"""
            page_base AS (
                {pageSql}
            )
            """);
        ctes.Add($"""
            page_numbered AS (
                SELECT page_base.*, ROW_NUMBER() OVER (ORDER BY {totalOrder}) AS page_ordinal
                FROM page_base
            )
            """);

        var overflowChecks = reducers
            .Where(reducer => reducer.IsUnion)
            .Select(reducer =>
                $"""
                EXISTS (
                    SELECT 1
                    FROM {reducer.CountCteName} c
                    JOIN page_numbered p ON p.group_key_order = c.group_key_order
                    WHERE p.page_ordinal <= {dialect.Parameter("returnedTake")}
                      AND c.value_count > {dialect.Parameter("maxUnion")}
                )
                """)
            .ToArray();
        var overflowExpression = overflowChecks.Length == 0
            ? "0"
            : $"CASE WHEN {string.Join(" OR ", overflowChecks)} THEN 1 ELSE 0 END";

        var rows = new List<string>
        {
            $"""
            SELECT 0 AS row_kind, 0 AS page_ordinal, NULL AS group_key, NULL AS alias,
                   {dialect.Int32Null} AS field_type, NULL AS canonical_value, NULL AS order_value,
                   {overflowExpression} AS union_overflow
            """,
            $"""
            SELECT 1 AS row_kind, p.page_ordinal, p.group_key, NULL AS alias,
                   {dialect.Int32Null} AS field_type, NULL AS canonical_value, p.{orderValue} AS order_value,
                   0 AS union_overflow
            FROM page_numbered p
            """
        };
        foreach (var reducer in reducers)
        {
            var aliasParameter = Add("alias", reducer.Definition.Alias);
            var typeParameter = Add("resultType", (int)reducer.OutputField.Type);
            if (reducer.IsUnion)
            {
                rows.Add($"""
                    SELECT 2 AS row_kind, p.page_ordinal, p.group_key, {aliasParameter} AS alias,
                           {typeParameter} AS field_type, u.canonical_value, p.{orderValue} AS order_value,
                           0 AS union_overflow
                    FROM page_numbered p
                    JOIN {reducer.ValuesCteName} u
                      ON u.group_key_order = p.group_key_order
                     AND u.value_rank <= {dialect.Parameter("maxUnion")}
                    """);
            }
            else
            {
                rows.Add($"""
                    SELECT 2 AS row_kind, p.page_ordinal, p.group_key, {aliasParameter} AS alias,
                           {typeParameter} AS field_type, p.{reducer.ValueColumn} AS canonical_value,
                           p.{orderValue} AS order_value, 0 AS union_overflow
                    FROM page_numbered p
                    WHERE p.{reducer.ValueColumn} IS NOT NULL
                    """);
            }
        }

        var sql = $"""
            WITH {string.Join($",{Environment.NewLine}", ctes)}
            {string.Join($"{Environment.NewLine}UNION ALL{Environment.NewLine}", rows)}
            ORDER BY row_kind, page_ordinal, alias, canonical_value;
            """;
        return new(sql, new Dictionary<string, object>(parameters, StringComparer.Ordinal));
    }

    private ReducerProjection BuildReducer(
        DiagnosticGroupReducerDefinition reducer,
        int index,
        List<string> ctes)
    {
        var field = DiagnosticRecordFieldResolver.Resolve(definition, reducer.Field)!;
        var fieldName = Add("reducerField", field.Name);
        var fieldType = Add("reducerType", (int)field.Type);
        var cte = $"reducer_{index}";
        var alias = $"a{index}";
        var projection = new ReducerProjection(reducer, field, index, cte, alias);

        switch (reducer.Kind)
        {
            case DiagnosticGroupReducerKind.MinTimestamp:
            case DiagnosticGroupReducerKind.MaxTimestamp:
                {
                    var aggregate = reducer.Kind == DiagnosticGroupReducerKind.MinTimestamp ? "MIN" : "MAX";
                    if (StringComparer.Ordinal.Equals(field.Name, DiagnosticRecordFieldNames.OccurredAt))
                    {
                        ctes.Add($"""
                        {cte} AS (
                            SELECT b.group_key_order,
                                   {dialect.Int64Canonical($"{aggregate}(b.occurred_at_ticks)")} AS value,
                                   {aggregate}(b.occurred_at_ticks) AS value_key,
                                   {dialect.Int64Canonical($"{aggregate}(b.occurred_at_ticks)")} AS search_key
                            FROM base_records b
                            GROUP BY b.group_key_order
                        )
                        """);
                    }
                    else
                    {
                        ctes.Add($"""
                        {cte} AS (
                            SELECT b.group_key_order,
                                   {aggregate}(f.canonical_value) AS value,
                                   {aggregate}(f.comparison_key) AS value_key,
                                   {aggregate}(f.search_key) AS search_key
                            FROM base_records b
                            LEFT JOIN {dialect.TableReference(RelationalDiagnosticRecordSchema.FieldsTable, "f")}
                              ON {FieldJoin("f", "b")}
                             AND f.field_name = {fieldName}
                             AND f.field_type = {fieldType}
                             AND f.value_ordinal = 0
                            GROUP BY b.group_key_order
                        )
                        """);
                    }
                    break;
                }
            case DiagnosticGroupReducerKind.SumInt64:
            case DiagnosticGroupReducerKind.MaxInt64:
                {
                    var native = dialect.Int64Value("f.canonical_value");
                    var aggregate = reducer.Kind == DiagnosticGroupReducerKind.SumInt64
                        ? dialect.SumInt64("f.canonical_value")
                        : $"MAX({native})";
                    ctes.Add($"""
                    {cte}_native AS (
                        SELECT b.group_key_order, {aggregate} AS native_value
                        FROM base_records b
                        LEFT JOIN {dialect.TableReference(RelationalDiagnosticRecordSchema.FieldsTable, "f")}
                          ON {FieldJoin("f", "b")}
                         AND f.field_name = {fieldName}
                         AND f.field_type = {fieldType}
                         AND f.value_ordinal = 0
                        GROUP BY b.group_key_order
                    )
                    """);
                    ctes.Add($"""
                    {cte} AS (
                        SELECT group_key_order,
                               {dialect.Int64Canonical("native_value")} AS value,
                               native_value AS value_key,
                               {dialect.Int64Canonical("native_value")} AS search_key
                        FROM {cte}_native
                    )
                    """);
                    break;
                }
            case DiagnosticGroupReducerKind.SetUnionString:
                {
                    ctes.Add($"""
                    {projection.DistinctCteName}_ranked AS (
                        SELECT b.group_key_order, f.comparison_key,
                               f.canonical_value, f.search_key,
                               ROW_NUMBER() OVER (
                                   PARTITION BY b.group_key_order, f.comparison_key
                                   ORDER BY b.cursor, f.value_ordinal
                               ) AS identity_rank
                        FROM base_records b
                        JOIN {dialect.TableReference(RelationalDiagnosticRecordSchema.FieldsTable, "f")}
                          ON {FieldJoin("f", "b")}
                         AND f.field_name = {fieldName}
                         AND f.field_type = {fieldType}
                    )
                    """);
                    ctes.Add($"""
                    {projection.DistinctCteName} AS (
                        SELECT group_key_order, comparison_key, canonical_value, search_key
                        FROM {projection.DistinctCteName}_ranked
                        WHERE identity_rank = 1
                    )
                    """);
                    ctes.Add($"""
                    {projection.ValuesCteName} AS (
                        SELECT group_key_order, comparison_key, canonical_value, search_key,
                               ROW_NUMBER() OVER (
                                   PARTITION BY group_key_order
                                   ORDER BY comparison_key, canonical_value
                               ) AS value_rank
                        FROM {projection.DistinctCteName}
                    )
                    """);
                    ctes.Add($"""
                    {projection.CountCteName} AS (
                        SELECT group_key_order, COUNT(*) AS value_count
                        FROM {projection.ValuesCteName}
                        WHERE value_rank <= {dialect.Parameter("maxUnion")} + 1
                        GROUP BY group_key_order
                    )
                    """);
                    break;
                }
            case DiagnosticGroupReducerKind.FirstBy:
                {
                    var order = DiagnosticRecordFieldResolver.Resolve(definition, reducer.OrderField!)!;
                    var direction = reducer.OrderDirection == DiagnosticSortDirection.Ascending ? "ASC" : "DESC";
                    var orderJoin = "";
                    var orderExpression = "b.occurred_at_ticks";
                    if (!StringComparer.Ordinal.Equals(order.Name, DiagnosticRecordFieldNames.OccurredAt))
                    {
                        var orderField = Add("firstOrderField", order.Name);
                        var orderType = Add("firstOrderType", (int)order.Type);
                        orderJoin = $"""
                        JOIN {dialect.TableReference(RelationalDiagnosticRecordSchema.FieldsTable, "o")}
                          ON {FieldJoin("o", "b")}
                         AND o.field_name = {orderField}
                         AND o.field_type = {orderType}
                         AND o.value_ordinal = 0
                        """;
                        orderExpression = "o.comparison_key";
                    }
                    var valueExpression = "f.canonical_value";
                    var valueKeyExpression = "f.comparison_key";
                    var searchExpression = "f.search_key";
                    var valueJoin = $"""
                    LEFT JOIN {dialect.TableReference(RelationalDiagnosticRecordSchema.FieldsTable, "f")}
                      ON {FieldJoin("f", "b")}
                     AND f.field_name = {fieldName}
                     AND f.field_type = {fieldType}
                     AND f.value_ordinal = 0
                    """;
                    if (StringComparer.Ordinal.Equals(field.Name, DiagnosticRecordFieldNames.OccurredAt))
                    {
                        valueExpression = dialect.Int64Canonical("b.occurred_at_ticks");
                        valueKeyExpression = "b.occurred_at_ticks";
                        searchExpression = dialect.Int64Canonical("b.occurred_at_ticks");
                        valueJoin = "";
                    }
                    ctes.Add($"""
                    {cte}_ranked AS (
                        SELECT b.group_key_order, {valueExpression} AS value,
                               {valueKeyExpression} AS value_key, {searchExpression} AS search_key,
                               ROW_NUMBER() OVER (
                                   PARTITION BY b.group_key_order
                                   ORDER BY {orderExpression} {direction}, b.cursor ASC
                               ) AS value_rank
                        FROM base_records b
                        {orderJoin}
                        {valueJoin}
                    )
                    """);
                    ctes.Add($"""
                    {cte} AS (
                        SELECT group_key_order, value, value_key, search_key
                        FROM {cte}_ranked
                        WHERE value_rank = 1
                    )
                    """);
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(reducer.Kind));
        }

        return projection;
    }

    private string BuildPredicate(
        DiagnosticRecordGroupPredicate predicate,
        IReadOnlyList<ReducerProjection> reducers) => predicate switch
        {
            DiagnosticRecordGroupPredicate.All all =>
                $"({string.Join(" AND ", all.Predicates.Select(child => BuildPredicate(child, reducers)))})",
            DiagnosticRecordGroupPredicate.Any any =>
                $"({string.Join(" OR ", any.Predicates.Select(child => BuildPredicate(child, reducers)))})",
            DiagnosticRecordGroupPredicate.Comparison comparison =>
                BuildComparison(comparison, reducers.Single(reducer =>
                    StringComparer.Ordinal.Equals(reducer.Definition.Alias, comparison.Alias))),
            _ => throw new ArgumentOutOfRangeException(nameof(predicate))
        };

    private string BuildComparison(
        DiagnosticRecordGroupPredicate.Comparison comparison,
        ReducerProjection reducer)
    {
        string expression;
        string? valueAlias = null;
        if (reducer.IsUnion)
        {
            valueAlias = $"u{reducer.Index}";
            expression = comparison.Operator == DiagnosticPredicateOperator.Contains
                ? $"{valueAlias}.search_key"
                : $"{valueAlias}.comparison_key";
        }
        else
        {
            expression = comparison.Operator == DiagnosticPredicateOperator.Contains
                ? $"reduced.{reducer.SearchColumn}"
                : $"reduced.{reducer.KeyColumn}";
        }

        var values = comparison.Values.Select(value =>
            Add(
                comparison.Operator == DiagnosticPredicateOperator.Contains ? "search" : "groupValue",
                QueryValue(
                    value,
                    reducer.OutputField,
                    comparison.Operator,
                    reducer.Definition.Kind == DiagnosticGroupReducerKind.FirstBy))).ToArray();
        var condition = comparison.Operator switch
        {
            DiagnosticPredicateOperator.Equal => $"{expression} = {values[0]}",
            DiagnosticPredicateOperator.In => $"{expression} IN ({string.Join(", ", values)})",
            DiagnosticPredicateOperator.RangeInclusive => $"{expression} BETWEEN {values[0]} AND {values[1]}",
            DiagnosticPredicateOperator.Contains when comparison.Values[0].CanonicalValue.Length == 0 => "1 = 1",
            DiagnosticPredicateOperator.Contains => dialect.Contains(expression, values[0][1..]),
            _ => throw new ArgumentOutOfRangeException(nameof(comparison.Operator))
        };
        return reducer.IsUnion
            ? $"EXISTS (SELECT 1 FROM {reducer.DistinctCteName} {valueAlias} WHERE {valueAlias}.group_key_order = reduced.group_key_order AND {condition})"
            : condition;
    }

    private string BuildContinuation(
        DiagnosticRecordGroupContinuation continuation,
        DiagnosticRecordGroupOrder order,
        DiagnosticGroupReducerDefinition orderReducer,
        DiagnosticFieldDefinition orderField,
        DiagnosticFieldDefinition groupField,
        string orderKey)
    {
        var comparison = order.Direction == DiagnosticSortDirection.Ascending ? ">" : "<";
        var lastOrder = Add("lastGroupOrder", QueryValue(
            continuation.LastOrderValue,
            orderField,
            DiagnosticPredicateOperator.Equal,
            orderReducer.Kind == DiagnosticGroupReducerKind.FirstBy));
        var lastGroup = Add(
            "lastGroupKey",
            DiagnosticStringComparisonKey.Create(
                continuation.LastGroupKey,
                groupField.CasePolicy));
        return $"(reduced.{orderKey} {comparison} {lastOrder} OR (reduced.{orderKey} = {lastOrder} AND reduced.group_key_order > {lastGroup}))";
    }

    private static object QueryValue(
        DiagnosticFieldValue value,
        DiagnosticFieldDefinition field,
        DiagnosticPredicateOperator operation,
        bool useStoredComparisonKey = false)
    {
        if (field.Type == DiagnosticFieldType.String)
        {
            var projection = DiagnosticStringComparisonKey.Project(value.CanonicalValue, field.CasePolicy);
            return operation == DiagnosticPredicateOperator.Contains
                ? projection.SearchKey
                : projection.ComparisonKey;
        }
        if (field.Type == DiagnosticFieldType.Int64)
            return useStoredComparisonKey
                ? DiagnosticComparisonKeys.Create(value, field.CasePolicy)
                : long.Parse(value.CanonicalValue, CultureInfo.InvariantCulture);
        if (StringComparer.Ordinal.Equals(field.Name, DiagnosticRecordFieldNames.OccurredAt))
            return DateTimeOffset.Parse(
                value.CanonicalValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind).UtcTicks;
        return DiagnosticComparisonKeys.Create(value, field.CasePolicy);
    }

    private void AddNamed(string name, object value) => parameters.Add(name, value);

    private string Add(string prefix, object value)
    {
        var name = $"{prefix}{parameterIndex++}";
        parameters.Add(name, value);
        return dialect.Parameter(name);
    }

    private static string FieldJoin(string fieldAlias, string recordAlias) =>
        $"{fieldAlias}.tenant_id = {recordAlias}.tenant_id AND " +
        $"{fieldAlias}.scope_id = {recordAlias}.scope_id AND " +
        $"{fieldAlias}.stream_id = {recordAlias}.stream_id AND " +
        $"{fieldAlias}.cursor = {recordAlias}.cursor";

    private sealed record ReducerProjection(
        DiagnosticGroupReducerDefinition Definition,
        DiagnosticFieldDefinition OutputField,
        int Index,
        string CteName,
        string JoinAlias)
    {
        public bool IsUnion => Definition.Kind == DiagnosticGroupReducerKind.SetUnionString;
        public string ValueColumn => $"r{Index}_value";
        public string KeyColumn => $"r{Index}_key";
        public string SearchColumn => $"r{Index}_search";
        public string DistinctCteName => $"{CteName}_distinct";
        public string ValuesCteName => $"{CteName}_values";
        public string CountCteName => $"{CteName}_counts";
    }
}
