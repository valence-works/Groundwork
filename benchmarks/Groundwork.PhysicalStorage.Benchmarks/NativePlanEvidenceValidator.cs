using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using MongoDB.Bson;

namespace Groundwork.PhysicalStorage.Benchmarks;

internal static partial class NativePlanEvidenceValidator
{
    private const string ShowplanNamespace = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static readonly HashSet<string> MongoStages = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND_HASH", "AND_SORTED", "COLLSCAN", "COUNT", "COUNT_SCAN", "DISTINCT_SCAN", "EOF",
        "EXPRESS_CLUSTERED_IXSCAN", "EXPRESS_IXSCAN", "FETCH", "GROUP", "IDHACK", "IXSCAN",
        "LIMIT", "OR", "PROJECTION_COVERED", "PROJECTION_DEFAULT", "PROJECTION_SIMPLE",
        "QUEUED_DATA", "RETURN_KEY", "SHARDING_FILTER", "SKIP", "SORT", "SORT_KEY_GENERATOR",
        "SUBPLAN"
    };

    public static bool Matches(
        BenchmarkProvider provider,
        string physicalObject,
        string indexName,
        NativePlanCommandBinding? binding,
        string nativePlan)
    {
        if (binding is null ||
            string.IsNullOrWhiteSpace(binding.PhysicalObject) ||
            !string.Equals(binding.PhysicalObject, physicalObject, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return provider switch
            {
                BenchmarkProvider.Sqlite => MatchesSqlite(binding, indexName, nativePlan),
                BenchmarkProvider.MongoDb => MatchesMongoDb(binding, indexName, nativePlan),
                BenchmarkProvider.PostgreSql => MatchesPostgreSql(binding, indexName, nativePlan),
                BenchmarkProvider.SqlServer => MatchesSqlServer(binding, indexName, nativePlan),
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
            };
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or XmlException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool MatchesSqlite(
        NativePlanCommandBinding binding,
        string indexName,
        string nativePlan)
    {
        if (!HasSafeParameterizedCommand(binding) ||
            string.IsNullOrWhiteSpace(binding.Alias) ||
            !CommandBindsObject(binding, '"', '"'))
        {
            return false;
        }

        var observedBoundAlias = false;
        foreach (var line in nativePlan.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var detail = line.Trim();
            var access = SqliteAccess().Match(detail);
            if (access.Success)
            {
                var alias = access.Groups["alias"].Value;
                observedBoundAlias |= string.Equals(alias, binding.Alias, StringComparison.Ordinal);
                var observedIndex = access.Groups["index"].Success
                    ? access.Groups["index"].Value
                    : null;
                if (observedIndex is not null &&
                    string.Equals(alias, binding.Alias, StringComparison.Ordinal) &&
                    !string.Equals(observedIndex, indexName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                continue;
            }

            if (!SqliteAuxiliary().IsMatch(detail))
                return false;
        }

        return observedBoundAlias;
    }

    private static bool MatchesMongoDb(
        NativePlanCommandBinding binding,
        string indexName,
        string nativePlan)
    {
        if (binding.Alias is not null || binding.ParameterizedCommand is not null)
            return false;

        var explain = BsonDocument.Parse(nativePlan);
        var planners = Descendants(explain)
            .Where(document => document.TryGetValue("queryPlanner", out var value) && value.IsBsonDocument)
            .Select(document => document["queryPlanner"].AsBsonDocument)
            .Where(planner => planner.TryGetValue("winningPlan", out var winningPlan) &&
                              winningPlan.IsBsonDocument)
            .ToArray();
        if (planners.Length != 1 ||
            !planners[0].TryGetValue("namespace", out var namespaceValue) ||
            !namespaceValue.IsString ||
            !NamespaceMatches(namespaceValue.AsString, binding.PhysicalObject))
        {
            return false;
        }

        var stages = Descendants(planners[0]["winningPlan"])
            .Where(document => document.TryGetValue("stage", out var stage) && stage.IsString)
            .ToArray();
        if (stages.Length == 0 ||
            stages.Any(document => !MongoStages.Contains(document["stage"].AsString)))
        {
            return false;
        }

        return stages
            .Where(document =>
                document.TryGetValue("indexName", out var observedIndex) &&
                observedIndex.IsString)
            .All(document => document["indexName"].AsString.Equals(indexName, StringComparison.Ordinal));
    }

    private static bool MatchesPostgreSql(
        NativePlanCommandBinding binding,
        string indexName,
        string nativePlan)
    {
        if (!HasSafeParameterizedCommand(binding) ||
            string.IsNullOrWhiteSpace(binding.Alias) ||
            !CommandBindsObject(binding, '"', '"'))
        {
            return false;
        }

        using var document = JsonDocument.Parse(nativePlan);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() != 1 ||
            document.RootElement[0].ValueKind != JsonValueKind.Object ||
            !document.RootElement[0].TryGetProperty("Plan", out var rootPlan) ||
            rootPlan.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var nodes = PostgreSqlPlanNodes(rootPlan).ToArray();
        if (nodes.Length == 0 ||
            nodes.Any(node =>
                !node.TryGetProperty("Node Type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(type.GetString())))
        {
            return false;
        }

        var matchingRelations = nodes.Where(node =>
            node.TryGetProperty("Relation Name", out var relation) &&
            relation.ValueKind == JsonValueKind.String &&
            string.Equals(relation.GetString(), binding.PhysicalObject, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matchingRelations.Length > 0 &&
               matchingRelations.All(node =>
                   PostgreSqlIndexNames(node).All(observedIndex =>
                       string.Equals(observedIndex, indexName, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool MatchesSqlServer(
        NativePlanCommandBinding binding,
        string indexName,
        string nativePlan)
    {
        if (!HasSafeParameterizedCommand(binding) ||
            string.IsNullOrWhiteSpace(binding.Alias) ||
            !CommandBindsObject(binding, '[', ']'))
        {
            return false;
        }

        var document = ParseXml(nativePlan);
        if (document.Root?.Name != XName.Get("ShowPlanXML", ShowplanNamespace) ||
            !document.Descendants().Any(element => element.Name.LocalName == "RelOp"))
        {
            return false;
        }

        var objects = document.Descendants()
            .Where(element => element.Name.LocalName == "Object" &&
                              IdentifierMatches(element.Attribute("Table")?.Value, binding.PhysicalObject))
            .ToArray();
        return objects.Length > 0 &&
               objects.All(element =>
                   element.Attribute("Index") is not { } observedIndex ||
                   IdentifierMatches(observedIndex.Value, indexName));
    }

    private static bool HasSafeParameterizedCommand(NativePlanCommandBinding binding) =>
        !string.IsNullOrWhiteSpace(binding.ParameterizedCommand) &&
        !ConnectionData().IsMatch(binding.ParameterizedCommand) &&
        !SqlStringLiteral().IsMatch(binding.ParameterizedCommand);

    private static bool CommandBindsObject(
        NativePlanCommandBinding binding,
        char quoteStart,
        char quoteEnd)
    {
        var quotedObject = $"{quoteStart}{binding.PhysicalObject}{quoteEnd}";
        return Regex.IsMatch(
            binding.ParameterizedCommand!,
            $@"\b(?:FROM|JOIN)\s+{Regex.Escape(quotedObject)}\s+(?:AS\s+)?{Regex.Escape(binding.Alias!)}\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
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

    private static IEnumerable<JsonElement> PostgreSqlPlanNodes(JsonElement node)
    {
        yield return node;
        if (!node.TryGetProperty("Plans", out var children) || children.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var child in children.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.Object)
                throw new JsonException("PostgreSQL plan children must be objects.");
            foreach (var descendant in PostgreSqlPlanNodes(child))
                yield return descendant;
        }
    }

    private static IEnumerable<string> PostgreSqlIndexNames(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("Index Name", out var index) &&
                index.ValueKind == JsonValueKind.String &&
                index.GetString() is { } value)
            {
                yield return value;
            }
            if (node.TryGetProperty("Plans", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                    foreach (var descendant in PostgreSqlIndexNames(child))
                        yield return descendant;
            }
        }
    }

    private static XDocument ParseXml(string value)
    {
        using var input = new StringReader(value);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static bool IdentifierMatches(string? candidate, string expected)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        var last = candidate.Split('.').Last().Trim();
        return string.Equals(last.Trim('[', ']', '"'), expected, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        @"^(?:(?:SEARCH (?<alias>[A-Za-z_][A-Za-z0-9_]*) USING (?:(?:COVERING )?INDEX (?<index>[^\s(]+)|INTEGER PRIMARY KEY) \(.+\))|(?:SCAN (?<alias>[A-Za-z_][A-Za-z0-9_]*)(?: USING (?:COVERING )?INDEX (?<index>[^\s]+))?))$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SqliteAccess();

    [GeneratedRegex(
        @"^(?:CO-ROUTINE [A-Za-z_][A-Za-z0-9_]*|USE TEMP B-TREE FOR (?:(?:LAST TERM OF )?ORDER BY|GROUP BY|DISTINCT)|BLOOM FILTER ON [A-Za-z_][A-Za-z0-9_]* \(.+\))$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SqliteAuxiliary();

    [GeneratedRegex(
        @"(?:Data\s+Source|Initial\s+Catalog|Server|Password|User\s+ID)\s*=|mongodb(?:\+srv)?://",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionData();

    [GeneratedRegex(@"'(?:''|[^'])*'", RegexOptions.CultureInvariant)]
    private static partial Regex SqlStringLiteral();
}
