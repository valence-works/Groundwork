using System.Text.RegularExpressions;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using MongoDB.Bson;

namespace Groundwork.PhysicalStorage.Benchmarks;

/// <summary>
/// Extracts only the benchmark's safe structural facts from provider commands. Predicate values
/// are deliberately excluded: the receipt retains field/operator/parameter roles and the fixed
/// structural pagination values, never document values, connection data, or credentials.
/// </summary>
internal static partial class NativePlanCommandParsing
{
    private enum MongoCommandLocation
    {
        Nested,
        Root,
        Pipeline,
        PipelineStage,
        Sort
    }

    public static bool IsCount(string command) => CountTerminal().IsMatch(command);

    public static IReadOnlyList<NativePlanFilterShape> RelationalFilters(
        string command,
        IReadOnlyList<NativePlanParameterReceipt> parameters,
        NativePlanFieldBinding fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(fields);
        return NativePlanRequestShape.CanonicalFilters.All(filter =>
                ParameterIsPresent(parameters, filter) &&
                RelationalFilter(filter, fields).IsMatch(command))
            ? NativePlanRequestShape.CanonicalFilters
            : [];
    }

    public static IReadOnlyList<NativePlanOrderShape> RelationalOrder(
        string command,
        NativePlanFieldBinding fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(fields);
        return DescendingOrder(fields.Rank).IsMatch(command)
            ? [new NativePlanOrderShape("rank", PhysicalSortDirection.Descending)]
            : [];
    }

    public static (int? Skip, int? Take) RelationalPagination(
        string command,
        IReadOnlyList<NativePlanParameterReceipt> parameters,
        NativePlanTerminalOperation terminal)
    {
        if (terminal != NativePlanTerminalOperation.Documents)
            return (null, null);

        var skip = StructuralParameter(parameters, "skip", "pagination-skip");
        var take = StructuralParameter(parameters, "take", "pagination-take");
        return ParameterUse("skip").IsMatch(command) &&
               ParameterUse("take").IsMatch(command) &&
               skip is not null &&
               take is > 0
            ? (skip == 0 ? null : skip, take)
            : (null, null);
    }

    public static NativePlanRequestShape MongoShape(
        BenchmarkPlanRequest request,
        NativePlanAssertionMode assertionMode,
        NativePlanMongoCommandReceipt receipt,
        NativePlanFieldBinding fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var command = ParseMongoCommand(receipt);
        var terminal = MongoTerminal(command, receipt.Kind);
        var filters = MongoFilters(command, fields);
        var order = MongoOrder(command, fields);
        var pagination = MongoPagination(command, terminal);
        var expected = NativePlanRequestShape.For(request, assertionMode);
        return new NativePlanRequestShape(
            request.Workload,
            terminal == NativePlanTerminalOperation.Count
                ? NativePlanOperation.Count
                : NativePlanOperation.Selection,
            terminal,
            filters,
            order,
            pagination.Skip,
            pagination.Take,
            expected.QuerySelectivityBasisPoints);
    }

    public static bool MongoReceiptIsStrictlyRedacted(NativePlanMongoCommandReceipt receipt)
    {
        try
        {
            var command = ParseMongoCommand(receipt);
            return HasMongoPhysicalObject(command) &&
                   HasOnlyRedactedValues(command, MongoCommandLocation.Root);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static BsonDocument RedactMongoCommand(BsonDocument command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return RedactMongoValue(command, MongoCommandLocation.Root).AsBsonDocument;
    }

    public static bool MongoCommandBindsObject(
        NativePlanMongoCommandReceipt receipt,
        string physicalObject)
    {
        try
        {
            var command = ParseMongoCommand(receipt);
            return command.TryGetValue("find", out var find) && find.IsString &&
                   string.Equals(find.AsString, physicalObject, StringComparison.Ordinal) ||
                   command.TryGetValue("aggregate", out var aggregate) && aggregate.IsString &&
                   string.Equals(aggregate.AsString, physicalObject, StringComparison.Ordinal) ||
                   command.TryGetValue("count", out var count) && count.IsString &&
                   string.Equals(count.AsString, physicalObject, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool MongoCommandContainsSelectedStatus(
        BsonDocument command,
        NativePlanFieldBinding fields)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(fields);
        var found = false;
        VisitMongoPredicateDocuments(command, predicate =>
        {
            if (ContainsSelectedStatus(predicate, fields.Status))
                found = true;
        });
        return found;
    }

    private static bool ParameterIsPresent(
        IReadOnlyList<NativePlanParameterReceipt> parameters,
        NativePlanFilterShape filter) =>
        parameters.Any(parameter =>
            parameter.Name == filter.Parameter &&
            parameter.Role == (filter.Parameter switch
            {
                "scope" => "storage-scope",
                "kind" => "document-kind",
                "q0" => "predicate",
                _ => string.Empty
            }));

    private static int? StructuralParameter(
        IReadOnlyList<NativePlanParameterReceipt> parameters,
        string name,
        string role) =>
        parameters.SingleOrDefault(parameter => parameter.Name == name && parameter.Role == role)
            ?.StructuralValue;

    private static BsonDocument ParseMongoCommand(NativePlanMongoCommandReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (string.IsNullOrWhiteSpace(receipt.RedactedCommand))
            throw new FormatException("MongoDB command receipt is empty.");
        return BsonDocument.Parse(receipt.RedactedCommand);
    }

    private static NativePlanTerminalOperation MongoTerminal(
        BsonDocument command,
        PhysicalDocumentQueryCommandKind kind) =>
        kind switch
        {
            PhysicalDocumentQueryCommandKind.Page when IsMongoDocumentCommand(command) =>
                NativePlanTerminalOperation.Documents,
            PhysicalDocumentQueryCommandKind.Count when IsMongoCountCommand(command) =>
                NativePlanTerminalOperation.Count,
            _ => throw new InvalidOperationException(
                $"MongoDB native command does not match the persisted '{kind}' terminal kind.")
        };

    private static bool IsMongoDocumentCommand(BsonDocument command)
    {
        if (MongoCommandKindCount(command) != 1)
            return false;
        if (command.Contains("find"))
        {
            return command.TryGetValue("limit", out var limit) &&
                   limit.IsNumeric &&
                   limit.ToInt32() > 0 &&
                   (!command.TryGetValue("skip", out var skip) ||
                    skip.IsNumeric && skip.ToInt32() >= 0);
        }
        return command.Contains("aggregate") && AggregatePageStagesAreValid(command);
    }

    private static bool IsMongoCountCommand(BsonDocument command)
    {
        if (MongoCommandKindCount(command) != 1)
            return false;
        if (command.Contains("count"))
        {
            return !command.Contains("skip") &&
                   !command.Contains("limit") &&
                   !command.Contains("sort");
        }
        return command.Contains("aggregate") && AggregateCountStagesAreValid(command);
    }

    private static int MongoCommandKindCount(BsonDocument command) =>
        new[] { "find", "aggregate", "count" }.Count(command.Contains);

    private static bool AggregatePageStagesAreValid(BsonDocument command)
    {
        var stages = MongoPipelineStages(command).ToArray();
        if (stages.Length == 0 || stages.Any(stage => stage.ElementCount != 1))
        {
            return false;
        }

        var names = stages.Select(stage => stage.GetElement(0).Name).ToArray();
        if (names[0] != "$match")
            return false;
        var index = 1;
        if (index < names.Length && names[index] == "$sort")
            index++;
        if (index < names.Length && names[index] == "$skip")
            index++;
        return index == names.Length - 1 && names[index] == "$limit";
    }

    private static bool AggregateCountStagesAreValid(BsonDocument command)
    {
        var stages = MongoPipelineStages(command).ToArray();
        if (stages.Length != 2 || stages.Any(stage => stage.ElementCount != 1))
        {
            return false;
        }

        return stages[0].Contains("$match") && IsMongoCountStage(stages[1]);
    }

    private static bool IsMongoCountStage(BsonDocument stage) =>
        stage.Contains("$count") ||
        stage.TryGetValue("$group", out var group) &&
        group.IsBsonDocument &&
        group.AsBsonDocument.Elements.Any(element =>
            element.Value.IsBsonDocument &&
            element.Value.AsBsonDocument.Contains("$sum"));

    private static IReadOnlyList<NativePlanFilterShape> MongoFilters(
        BsonDocument command,
        NativePlanFieldBinding fields)
    {
        var predicates = command.Contains("aggregate")
            ? MongoPipelineStages(command)
                .Where(stage => stage.TryGetValue("$match", out var value) && value.IsBsonDocument)
                .Select(stage => stage["$match"].AsBsonDocument)
                .ToArray()
            : new[] { "filter", "query" }
                .Where(command.Contains)
                .Select(name => command[name])
                .Where(value => value.IsBsonDocument)
                .Select(value => value.AsBsonDocument)
                .ToArray();
        return predicates.Length == 1 && IsCanonicalMongoPredicate(predicates[0], fields)
            ? NativePlanRequestShape.CanonicalFilters
            : [];
    }

    private static bool IsCanonicalMongoPredicate(
        BsonDocument predicate,
        NativePlanFieldBinding fields)
    {
        var expected = new[] { fields.StorageScope, fields.DocumentKind, fields.Status };
        return expected.Distinct(StringComparer.Ordinal).Count() == expected.Length &&
               predicate.ElementCount == expected.Length &&
               expected.All(field =>
                   predicate.TryGetValue(field, out var value) &&
                   IsMongoEquality(value));
    }

    private static void VisitMongoPredicateDocuments(
        BsonDocument command,
        Action<BsonDocument> visit)
    {
        foreach (var name in new[] { "filter", "query" })
        {
            if (command.TryGetValue(name, out var predicate) && predicate.IsBsonDocument)
                visit(predicate.AsBsonDocument);
        }

        if (!command.TryGetValue("pipeline", out var pipeline) || !pipeline.IsBsonArray)
            return;

        VisitMongoPipelinePredicates(pipeline.AsBsonArray, visit);
    }

    private static void VisitMongoPipelinePredicates(
        BsonArray pipeline,
        Action<BsonDocument> visit)
    {
        foreach (var stage in pipeline.Where(stage => stage.IsBsonDocument).Select(stage => stage.AsBsonDocument))
        {
            if (stage.TryGetValue("$match", out var predicate) && predicate.IsBsonDocument)
                visit(predicate.AsBsonDocument);

            if (!stage.TryGetValue("$facet", out var facet) || !facet.IsBsonDocument)
                continue;

            foreach (var nestedPipeline in facet.AsBsonDocument.Values.Where(value => value.IsBsonArray))
                VisitMongoPipelinePredicates(nestedPipeline.AsBsonArray, visit);
        }
    }

    private static IReadOnlyList<NativePlanOrderShape> MongoOrder(
        BsonDocument command,
        NativePlanFieldBinding fields)
    {
        var descendingRank =
            command.Contains("find") && ContainsDescendingRank(command, "sort", fields.Rank) ||
            command.Contains("aggregate") &&
            MongoPipelineStages(command).Any(stage => ContainsDescendingRank(stage, "$sort", fields.Rank));
        return descendingRank ? [new NativePlanOrderShape("rank", PhysicalSortDirection.Descending)] : [];
    }

    private static bool ContainsDescendingRank(
        BsonDocument document,
        string sortMember,
        string rankField) =>
        document.TryGetValue(sortMember, out var sort) &&
        sort.IsBsonDocument &&
        sort.AsBsonDocument.TryGetValue(rankField, out var rank) &&
        rank.IsNumeric &&
        rank.ToInt32() == -1;

    private static (int? Skip, int? Take) MongoPagination(
        BsonDocument command,
        NativePlanTerminalOperation terminal)
    {
        if (terminal != NativePlanTerminalOperation.Documents)
            return (null, null);

        var skips = new List<int>();
        var takes = new List<int>();
        if (command.Contains("find"))
        {
            AddMongoStructuralValue(command, "skip", skips);
            AddMongoStructuralValue(command, "limit", takes);
        }
        else if (command.Contains("aggregate"))
        {
            foreach (var stage in MongoPipelineStages(command))
            {
                AddMongoStructuralValue(stage, "$skip", skips);
                AddMongoStructuralValue(stage, "$limit", takes);
            }
        }

        var skip = skips.DefaultIfEmpty(0).Distinct().ToArray();
        var take = takes.Distinct().ToArray();
        return skip.Length == 1 && take.Length == 1 && take[0] > 0
            ? (skip[0] == 0 ? null : skip[0], take[0])
            : (null, null);
    }

    private static IEnumerable<BsonDocument> MongoPipelineStages(BsonDocument command) =>
        command.TryGetValue("pipeline", out var pipeline) && pipeline.IsBsonArray
            ? pipeline.AsBsonArray
                .Where(value => value.IsBsonDocument)
                .Select(value => value.AsBsonDocument)
            : [];

    private static void AddMongoStructuralValue(BsonDocument document, string name, ICollection<int> values)
    {
        if (document.TryGetValue(name, out var value) && value.IsNumeric)
            values.Add(value.ToInt32());
    }

    private static bool IsMongoEquality(BsonValue value) =>
        !value.IsBsonDocument ||
        value.AsBsonDocument.ElementCount == 1 && value.AsBsonDocument.Contains("$eq");

    private static bool ContainsSelectedStatus(BsonValue value, string statusField)
    {
        if (value.IsBsonDocument)
        {
            foreach (var element in value.AsBsonDocument.Elements)
            {
                if (string.Equals(element.Name, statusField, StringComparison.Ordinal) &&
                    ContainsOpenValue(element.Value))
                {
                    return true;
                }

                if (ContainsSelectedStatus(element.Value, statusField))
                    return true;
            }
        }
        else if (value.IsBsonArray)
        {
            foreach (var item in value.AsBsonArray)
            {
                if (ContainsSelectedStatus(item, statusField))
                    return true;
            }
        }
        return false;
    }

    private static bool ContainsOpenValue(BsonValue value) =>
        value.IsString && string.Equals(value.AsString, "open", StringComparison.Ordinal) ||
        value.IsBsonDocument && value.AsBsonDocument.Elements.Any(element => ContainsOpenValue(element.Value));

    private static bool HasMongoPhysicalObject(BsonDocument command) =>
        command.Names.Any(name => name is "find" or "aggregate" or "count");

    private static BsonValue RedactMongoValue(
        BsonValue value,
        MongoCommandLocation location)
    {
        if (value.IsBsonDocument)
        {
            var document = new BsonDocument();
            foreach (var element in value.AsBsonDocument.Elements)
            {
                document[element.Name] = PreserveMongoScalar(location, element.Name, element.Value)
                    ? element.Value.DeepClone()
                    : RedactMongoValue(element.Value, ChildLocation(location, element.Name));
            }
            return document;
        }

        if (value.IsBsonArray)
        {
            var itemLocation = location == MongoCommandLocation.Pipeline
                ? MongoCommandLocation.PipelineStage
                : MongoCommandLocation.Nested;
            return new BsonArray(value.AsBsonArray.Select(item => RedactMongoValue(item, itemLocation)));
        }

        return "<redacted>";
    }

    private static bool HasOnlyRedactedValues(
        BsonValue value,
        MongoCommandLocation location)
    {
        if (value.IsBsonDocument)
        {
            return value.AsBsonDocument.Elements.All(element =>
                PreserveMongoScalar(location, element.Name, element.Value)
                    ? true
                    : HasOnlyRedactedValues(
                        element.Value,
                        ChildLocation(location, element.Name)));
        }

        if (value.IsBsonArray)
        {
            var itemLocation = location == MongoCommandLocation.Pipeline
                ? MongoCommandLocation.PipelineStage
                : MongoCommandLocation.Nested;
            return value.AsBsonArray.All(item => HasOnlyRedactedValues(item, itemLocation));
        }

        return value.IsString &&
               string.Equals(value.AsString, "<redacted>", StringComparison.Ordinal);
    }

    private static bool PreserveMongoScalar(
        MongoCommandLocation location,
        string key,
        BsonValue value) =>
        (location == MongoCommandLocation.Root &&
         key is "find" or "aggregate" or "count" &&
         value.IsString &&
         !string.Equals(value.AsString, "<redacted>", StringComparison.Ordinal)) ||
        (location == MongoCommandLocation.Root &&
         key is "skip" or "limit" &&
         value.IsNumeric) ||
        (location == MongoCommandLocation.PipelineStage &&
         key is "$skip" or "$limit" &&
         value.IsNumeric) ||
        (location == MongoCommandLocation.Sort &&
         value.IsNumeric);

    private static MongoCommandLocation ChildLocation(
        MongoCommandLocation location,
        string key) =>
        location switch
        {
            MongoCommandLocation.Root when key == "pipeline" => MongoCommandLocation.Pipeline,
            MongoCommandLocation.Root when key == "sort" => MongoCommandLocation.Sort,
            MongoCommandLocation.PipelineStage when key == "$sort" => MongoCommandLocation.Sort,
            _ => MongoCommandLocation.Nested
        };

    [GeneratedRegex(@"\bCOUNT(?:_BIG)?\s*\(", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CountTerminal();

    private static Regex RelationalFilter(
        NativePlanFilterShape filter,
        NativePlanFieldBinding fields) =>
        filter.Field switch
        {
            "storage-scope" => Equality(fields.StorageScope, filter.Parameter),
            "document-kind" => Equality(fields.DocumentKind, filter.Parameter),
            "status" => Equality(fields.Status, filter.Parameter),
            _ => throw new ArgumentOutOfRangeException(nameof(filter))
        };

    private static Regex ParameterUse(string name) => new(
        $@"(?<![A-Za-z0-9_])@{Regex.Escape(name)}\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static Regex Equality(string field, string parameter) => new(
        $@"(?:{AnyIdentifier()}\.)?{Identifier(field)}\s*=\s*@{Regex.Escape(parameter)}\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static Regex DescendingOrder(string field) => new(
        $@"\bORDER\s+BY\b[^;]*(?:{AnyIdentifier()}\.)?{Identifier(field)}\s+DESC\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static string Identifier(string value)
    {
        var escaped = Regex.Escape(value);
        return $@"(?:\[{escaped}\]|""{escaped}""|(?<![A-Za-z0-9_]){escaped}(?![A-Za-z0-9_]))";
    }

    private static string AnyIdentifier() =>
        @"(?:\[[^\]\r\n]+\]|""[^""\r\n]+""|[A-Za-z_][A-Za-z0-9_]*)";
}
