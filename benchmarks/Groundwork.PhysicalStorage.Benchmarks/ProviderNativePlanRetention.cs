using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;

namespace Groundwork.PhysicalStorage.Benchmarks;

internal static class ProviderNativePlanRetention
{
    private static readonly ISet<string> PostgreSqlNodeTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Aggregate", "Append", "Bitmap Heap Scan", "Bitmap Index Scan", "Gather", "Gather Merge",
        "Hash", "Hash Join", "Index Only Scan", "Index Scan", "Limit", "Materialize", "Memoize",
        "Merge Join", "Nested Loop", "Result", "Seq Scan", "Sort", "Subquery Scan", "Unique"
    };
    private static readonly ISet<string> PostgreSqlSafeScalarMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "Startup Cost", "Total Cost", "Plan Rows", "Plan Width", "Parallel Aware",
        "Async Capable"
    };

    public static string Retain(
        BenchmarkProvider provider,
        string nativePlan,
        string physicalObject,
        string indexName) =>
        provider switch
        {
            BenchmarkProvider.SqlServer => SqlServerShowplanReader.RetainSafeStructure(nativePlan),
            BenchmarkProvider.PostgreSql => RetainPostgreSql(nativePlan, physicalObject, indexName),
            BenchmarkProvider.MongoDb => RetainMongoDb(nativePlan, physicalObject, indexName),
            _ => nativePlan
        };

    private static string RetainPostgreSql(
        string nativePlan,
        string physicalObject,
        string indexName)
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

        return new JsonArray(new JsonObject
        {
            ["Plan"] = RetainPostgreSqlNode(plan, physicalObject, indexName)
        })
            .ToJsonString(BenchmarkJson.CompactOptions);
    }

    private static JsonObject RetainPostgreSqlNode(
        JsonElement source,
        string physicalObject,
        string indexName)
    {
        if (!source.TryGetProperty("Node Type", out var nodeType) ||
            nodeType.ValueKind != JsonValueKind.String ||
            !PostgreSqlNodeTypes.Contains(nodeType.GetString()!))
        {
            throw new InvalidOperationException("PostgreSQL native-plan evidence has an unsupported node type.");
        }

        var retained = new JsonObject { ["Node Type"] = nodeType.GetString() };
        RetainBoundIdentifier(source, retained, "Relation Name", physicalObject);
        RetainBoundIdentifier(source, retained, "Index Name", indexName);
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
                .Select(child => RetainPostgreSqlNode(child, physicalObject, indexName))
                .Cast<JsonNode?>()
                .ToArray());
        }
        return retained;
    }

    private static void RetainBoundIdentifier(
        JsonElement source,
        JsonObject retained,
        string member,
        string expected)
    {
        if (!source.TryGetProperty(member, out var value))
            return;
        if (value.ValueKind != JsonValueKind.String ||
            !string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"PostgreSQL native-plan evidence has an unexpected '{member}'.");
        }
        retained[member] = expected;
    }

    private static string RetainMongoDb(
        string nativePlan,
        string physicalObject,
        string indexName)
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
                            "MongoDB native-plan evidence has an unexpected index identity.");
                    }
                    retained["indexName"] = indexName;
                }
                return retained;
            })
            .ToArray();
        if (stages.Length == 0)
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
