using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace Groundwork.PhysicalStorage.Benchmarks;

internal static class ProviderNativePlanRetention
{
    private static readonly ISet<string> PostgreSqlNodeTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Aggregate", "Append", "Bitmap Heap Scan", "Bitmap Index Scan", "Gather", "Gather Merge",
        "Hash", "Hash Join", "Incremental Sort", "Index Only Scan", "Index Scan", "Limit",
        "Materialize", "Memoize", "Merge Join", "Nested Loop", "Result", "Seq Scan", "Sort",
        "Subquery Scan", "Unique"
    };
    private static readonly ISet<string> PostgreSqlSafeScalarMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "Startup Cost", "Total Cost", "Plan Rows", "Plan Width", "Parallel Aware",
        "Async Capable"
    };
    private static readonly Regex SqliteSearch = new(
        @"^SEARCH (?<alias>[A-Za-z_][A-Za-z0-9_]*) USING (?:(?:COVERING )?INDEX (?<index>[^\s(]+)|(?<primary>INTEGER PRIMARY KEY)) \(.+\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SqliteScan = new(
        @"^SCAN (?<alias>[A-Za-z_][A-Za-z0-9_]*)(?: USING (?:COVERING )?INDEX (?<index>[^\s]+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SqliteCoRoutine = new(
        @"^CO-ROUTINE (?<alias>[A-Za-z_][A-Za-z0-9_]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SqliteTemporary = new(
        @"^USE TEMP B-TREE FOR (?<purpose>(?:LAST TERM OF )?ORDER BY|GROUP BY|DISTINCT)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SqliteBloom = new(
        @"^BLOOM FILTER ON (?<alias>[A-Za-z_][A-Za-z0-9_]*) \(.+\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string Retain(
        BenchmarkProvider provider,
        string nativePlan,
        string physicalObject,
        string indexName,
        string? alias,
        NativePlanAssertionMode assertionMode) =>
        provider switch
        {
            BenchmarkProvider.Sqlite => RetainSqlite(nativePlan, indexName, alias),
            BenchmarkProvider.SqlServer => SqlServerShowplanReader.RetainSafeStructure(
                nativePlan,
                physicalObject,
                indexName),
            BenchmarkProvider.PostgreSql => RetainPostgreSql(
                nativePlan,
                physicalObject,
                indexName,
                assertionMode),
            BenchmarkProvider.MongoDb => RetainMongoDb(
                nativePlan,
                physicalObject,
                indexName,
                assertionMode),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };

    private static string RetainSqlite(
        string nativePlan,
        string indexName,
        string? boundAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boundAlias);
        var retained = nativePlan.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => RetainSqliteLine(line.Trim(), indexName, boundAlias))
            .ToArray();
        if (retained.Length == 0)
            throw new InvalidOperationException("SQLite native-plan evidence is empty.");
        return string.Join(Environment.NewLine, retained);
    }

    private static string RetainSqliteLine(
        string line,
        string indexName,
        string boundAlias)
    {
        var search = SqliteSearch.Match(line);
        if (search.Success)
        {
            var alias = search.Groups["alias"].Value;
            if (search.Groups["primary"].Success)
                return $"SEARCH {alias} USING INTEGER PRIMARY KEY (predicate-redacted)";
            return $"SEARCH {alias} USING INDEX {RetainSqliteIndex(search, alias, boundAlias, indexName)} (predicate-redacted)";
        }

        var scan = SqliteScan.Match(line);
        if (scan.Success)
        {
            var alias = scan.Groups["alias"].Value;
            return scan.Groups["index"].Success
                ? $"SCAN {alias} USING INDEX {RetainSqliteIndex(scan, alias, boundAlias, indexName)}"
                : $"SCAN {alias}";
        }

        var coRoutine = SqliteCoRoutine.Match(line);
        if (coRoutine.Success)
            return $"CO-ROUTINE {coRoutine.Groups["alias"].Value}";
        var temporary = SqliteTemporary.Match(line);
        if (temporary.Success)
            return $"USE TEMP B-TREE FOR {temporary.Groups["purpose"].Value.ToUpperInvariant()}";
        var bloom = SqliteBloom.Match(line);
        if (bloom.Success)
            return $"BLOOM FILTER ON {bloom.Groups["alias"].Value} (predicate-redacted)";
        throw new InvalidOperationException("SQLite native-plan evidence contains an unsupported detail row.");
    }

    private static string RetainSqliteIndex(
        Match match,
        string observedAlias,
        string boundAlias,
        string indexName)
    {
        var observedIndex = match.Groups["index"].Value;
        if (!string.Equals(observedAlias, boundAlias, StringComparison.Ordinal))
            return "unrelated-index-redacted";
        if (!string.Equals(observedIndex, indexName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQLite native-plan evidence selected an unexpected bound index.");
        return indexName;
    }

    private static string RetainPostgreSql(
        string nativePlan,
        string physicalObject,
        string indexName,
        NativePlanAssertionMode assertionMode)
    {
        using var document = JsonDocument.Parse(nativePlan);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() != 1 ||
            document.RootElement[0].ValueKind != JsonValueKind.Object ||
            !document.RootElement[0].TryGetProperty("Plan", out var plan) ||
            plan.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("PostgreSQL native-plan evidence is not a canonical EXPLAIN JSON plan.");
        }
        var nodes = PostgreSqlNodes(plan).ToArray();
        if (!nodes.Any(node => IdentifierMatches(node, "Relation Name", physicalObject)) ||
            assertionMode == NativePlanAssertionMode.RequireDeclaredIndex &&
            !ContainsBoundPostgreSqlIndex(
                plan,
                physicalObject,
                indexName,
                withinBoundRelation: false))
        {
            throw new InvalidOperationException(
                "PostgreSQL native-plan evidence does not bind the required relation and index.");
        }

        return new JsonArray(new JsonObject
        {
            ["Plan"] = RetainPostgreSqlNode(
                plan,
                physicalObject,
                indexName,
                withinBoundRelation: false)
        })
            .ToJsonString(BenchmarkJson.CompactOptions);
    }

    private static JsonObject RetainPostgreSqlNode(
        JsonElement source,
        string physicalObject,
        string indexName,
        bool withinBoundRelation)
    {
        if (!source.TryGetProperty("Node Type", out var nodeType) ||
            nodeType.ValueKind != JsonValueKind.String ||
            !PostgreSqlNodeTypes.Contains(nodeType.GetString()!))
        {
            throw new InvalidOperationException("PostgreSQL native-plan evidence has an unsupported node type.");
        }

        var retained = new JsonObject { ["Node Type"] = nodeType.GetString() };
        var bindsRelation = IdentifierMatches(source, "Relation Name", physicalObject);
        var boundScope = withinBoundRelation || bindsRelation;
        if (bindsRelation)
            retained["Relation Name"] = physicalObject;
        if (source.TryGetProperty("Index Name", out var observedIndex))
        {
            var matchesIndex = observedIndex.ValueKind == JsonValueKind.String &&
                               string.Equals(
                                   observedIndex.GetString(),
                                   indexName,
                                   StringComparison.OrdinalIgnoreCase);
            if (boundScope && !matchesIndex)
            {
                throw new InvalidOperationException(
                    "PostgreSQL native-plan evidence selected an unexpected index on the bound relation.");
            }
            if (boundScope && matchesIndex)
                retained["Index Name"] = indexName;
        }
        foreach (var member in PostgreSqlSafeScalarMembers)
        {
            if (source.TryGetProperty(member, out var value) &&
                value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                retained[member] = JsonNode.Parse(value.GetRawText());
            }
        }
        if (source.TryGetProperty("Plans", out var children))
        {
            if (children.ValueKind != JsonValueKind.Array ||
                children.EnumerateArray().Any(child => child.ValueKind != JsonValueKind.Object))
            {
                throw new InvalidOperationException("PostgreSQL native-plan children are not canonical plan objects.");
            }
            retained["Plans"] = new JsonArray(children.EnumerateArray()
                .Select(child => RetainPostgreSqlNode(
                    child,
                    physicalObject,
                    indexName,
                    boundScope))
                .Cast<JsonNode?>()
                .ToArray());
        }
        return retained;
    }

    private static string RetainMongoDb(
        string nativePlan,
        string physicalObject,
        string indexName,
        NativePlanAssertionMode assertionMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalObject);
        var source = BsonDocument.Parse(nativePlan);
        var planners = Descendants(source)
            .Where(document => document.TryGetValue("queryPlanner", out var value) &&
                               value.IsBsonDocument)
            .Select(document => document["queryPlanner"].AsBsonDocument)
            .Where(planner => planner.TryGetValue("winningPlan", out var winningPlan) &&
                              winningPlan.IsBsonDocument)
            .ToArray();
        if (planners.Length != 1 ||
            !planners[0].TryGetValue("namespace", out var namespaceValue) ||
            !namespaceValue.IsString ||
            !NamespaceMatches(namespaceValue.AsString, physicalObject))
        {
            throw new InvalidOperationException("MongoDB native-plan evidence does not bind one physical namespace.");
        }

        var hasExpectedIndex = false;
        var stages = Descendants(planners[0]["winningPlan"])
            .Where(document => document.TryGetValue("stage", out var stage) && stage.IsString)
            .Select(document =>
            {
                var retained = new BsonDocument("stage", document["stage"].AsString);
                if (document.TryGetValue("indexName", out var observedIndex))
                {
                    if (!observedIndex.IsString ||
                        !string.Equals(observedIndex.AsString, indexName, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "MongoDB native-plan evidence selected an unexpected index.");
                    }
                    hasExpectedIndex = true;
                    retained["indexName"] = indexName;
                }
                return retained;
            })
            .ToArray();
        if (stages.Length == 0 ||
            assertionMode == NativePlanAssertionMode.RequireDeclaredIndex && !hasExpectedIndex)
            throw new InvalidOperationException("MongoDB native-plan evidence does not contain a winning-plan stage.");

        var winning = stages[0];
        if (stages.Length > 1)
            winning["inputStages"] = new BsonArray(stages.Skip(1));
        return new BsonDocument("queryPlanner", new BsonDocument
        {
            ["namespace"] = $"retained.{physicalObject}",
            ["winningPlan"] = winning
        }).ToJson();
    }

    private static bool IdentifierMatches(
        JsonElement source,
        string member,
        string expected) =>
        source.TryGetProperty(member, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsBoundPostgreSqlIndex(
        JsonElement node,
        string physicalObject,
        string indexName,
        bool withinBoundRelation)
    {
        var boundScope = withinBoundRelation ||
                         IdentifierMatches(node, "Relation Name", physicalObject);
        if (boundScope && IdentifierMatches(node, "Index Name", indexName))
            return true;
        return node.TryGetProperty("Plans", out var children) &&
               children.ValueKind == JsonValueKind.Array &&
               children.EnumerateArray().Any(child =>
                   child.ValueKind == JsonValueKind.Object &&
                   ContainsBoundPostgreSqlIndex(
                       child,
                       physicalObject,
                       indexName,
                       boundScope));
    }

    private static IEnumerable<JsonElement> PostgreSqlNodes(JsonElement node)
    {
        yield return node;
        if (!node.TryGetProperty("Plans", out var children) ||
            children.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var child in children.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("PostgreSQL native-plan children are not canonical plan objects.");
            foreach (var descendant in PostgreSqlNodes(child))
                yield return descendant;
        }
    }

    private static bool NamespaceMatches(string value, string physicalObject)
    {
        var separator = value.LastIndexOf('.');
        return separator >= 0 &&
               string.Equals(value[(separator + 1)..], physicalObject, StringComparison.Ordinal);
    }

    private static IEnumerable<BsonDocument> Descendants(BsonValue value)
    {
        if (value.IsBsonDocument)
        {
            var document = value.AsBsonDocument;
            yield return document;
            foreach (var element in document.Elements)
                foreach (var descendant in Descendants(element.Value))
                    yield return descendant;
        }
        else if (value.IsBsonArray)
        {
            foreach (var item in value.AsBsonArray)
                foreach (var descendant in Descendants(item))
                    yield return descendant;
        }
    }
}
