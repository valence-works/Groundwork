using System.Text.Json;

namespace Groundwork.PhysicalStorage.Benchmarks;

/// <summary>
/// Enforces the native-plan assertion-sidecar contract before it is persisted and whenever a
/// retained artifact is consumed. The JSON schema is the portable interchange document; this
/// validator makes the harness fail closed without relying on a schema package at runtime.
/// </summary>
internal static class NativePlanEvidenceSidecar
{
    private static readonly ISet<string> RootMembers = Members(
        "schemaVersion", "request", "provider", "storageForm", "queryIdentity", "physicalObject",
        "indexName", "nativePlan", "assertions", "commandBinding");
    private static readonly ISet<string> RequestMembers = Members("workload", "operation", "ordered", "skip", "take");
    private static readonly ISet<string> BindingMembers = Members(
        "physicalObject", "alias", "parameterizedCommandDigest", "requestShape", "queryReceipt", "fields", "mongoCommandReceipt");
    private static readonly ISet<string> FieldBindingMembers = Members(
        "storageScope", "documentKind", "status", "rank", "identityComparison");
    private static readonly ISet<string> ShapeMembers = Members(
        "workload", "operation", "terminal", "filters", "order", "skip", "take", "querySelectivityBasisPoints");
    private static readonly ISet<string> FilterMembers = Members("field", "operator", "parameter");
    private static readonly ISet<string> OrderMembers = Members("field", "direction");
    private static readonly ISet<string> ReceiptMembers = Members("shape", "parameters");
    private static readonly ISet<string> ParameterMembers = Members("name", "role", "structuralValue", "valueClassification");
    private static readonly ISet<string> MongoMembers = Members("kind", "redactedCommand");

    public static void ValidateForWrite(NativePlanEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(evidence, BenchmarkJson.CompactOptions));
        ValidateDocument(document.RootElement);
    }

    public static async Task<NativePlanEvidence> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        ValidateDocument(document.RootElement);
        return document.RootElement.Deserialize<NativePlanEvidence>(BenchmarkJson.Options)
            ?? throw new InvalidOperationException($"Native-plan sidecar '{path}' is null.");
    }

    public static void ValidateDocument(JsonElement root)
    {
        RequireObject(root, RootMembers, RootMembers, "Native-plan sidecar");
        RequireString(root, "schemaVersion", NativePlanEvidence.SidecarSchemaVersion, "Native-plan sidecar");
        RequireEnum(root, "provider", ["sqlite", "sqlServer", "postgreSql", "mongoDb"], "Native-plan sidecar");
        RequireEnum(root, "storageForm", ["sharedDocuments", "dedicatedDocumentTable", "physicalEntityTable"], "Native-plan sidecar");
        RequireNonEmptyString(root, "queryIdentity", "Native-plan sidecar");
        RequireNonEmptyString(root, "physicalObject", "Native-plan sidecar");
        RequireNonEmptyString(root, "indexName", "Native-plan sidecar");
        RequireNonEmptyString(root, "nativePlan", "Native-plan sidecar");
        RequireStringArray(root.GetProperty("assertions"), "Native-plan assertion collection");

        var request = root.GetProperty("request");
        RequireObject(request, RequestMembers, ["workload", "operation", "ordered"], "Native-plan request");
        RequireEnum(request, "workload", ["indexedQuery", "mixedCompoundOrdering", "paginationAndCount"], "Native-plan request");
        RequireEnum(request, "operation", ["selection", "count"], "Native-plan request");
        RequireBoolean(request, "ordered", "Native-plan request");
        RequireNullableNonNegativeInteger(request, "skip", "Native-plan request");
        RequireNullablePositiveInteger(request, "take", "Native-plan request");

        RequireObject(root.GetProperty("commandBinding"), BindingMembers,
            ["physicalObject", "requestShape", "queryReceipt", "fields"], "Native-plan command binding");
        var binding = root.GetProperty("commandBinding");
        var hasSql = binding.TryGetProperty("parameterizedCommandDigest", out var digest) &&
                     digest.ValueKind == JsonValueKind.String;
        var hasMongo = binding.TryGetProperty("mongoCommandReceipt", out var mongo) && mongo.ValueKind == JsonValueKind.Object;
        if (hasSql == hasMongo)
            throw new InvalidOperationException("Native-plan command binding must contain exactly one provider command receipt.");
        RequireNonEmptyString(binding, "physicalObject", "Native-plan command binding");
        RequireNullableNonEmptyString(binding, "alias", "Native-plan command binding");
        if (hasSql)
            RequireCommandDigest(binding, "parameterizedCommandDigest", "Native-plan command binding");
        else
            RequireNullOrMissing(binding, "parameterizedCommandDigest", "Native-plan command binding");

        var fields = binding.GetProperty("fields");
        RequireObject(fields, FieldBindingMembers, FieldBindingMembers, "Native-plan field binding");
        foreach (var member in FieldBindingMembers)
            RequireNonEmptyString(fields, member, "Native-plan field binding");

        ValidateShape(binding.GetProperty("requestShape"), "Native-plan request shape");
        var receipt = binding.GetProperty("queryReceipt");
        RequireObject(receipt, ReceiptMembers, ReceiptMembers, "Native-plan query receipt");
        ValidateShape(receipt.GetProperty("shape"), "Native-plan query-receipt shape");
        RequireArray(receipt.GetProperty("parameters"), "Native-plan parameter receipt collection");
        var parameters = receipt.GetProperty("parameters");
        if (parameters.GetArrayLength() is < 3 or > 5)
            throw new InvalidOperationException("Native-plan parameter receipt collection must contain three to five entries.");
        foreach (var parameter in parameters.EnumerateArray())
        {
            RequireObject(parameter, ParameterMembers, ["name", "role", "valueClassification"], "Native-plan parameter receipt");
            RequireEnum(parameter, "name", ["scope", "kind", "q0", "skip", "take"], "Native-plan parameter receipt");
            RequireEnum(parameter, "role", ["storage-scope", "document-kind", "predicate", "pagination-skip", "pagination-take"], "Native-plan parameter receipt");
            RequireEnum(parameter, "valueClassification", ["benchmark-tenant", "benchmark-document-kind", "benchmark-selected-status", "structural"], "Native-plan parameter receipt");
            RequireNullableNonNegativeInteger(parameter, "structuralValue", "Native-plan parameter receipt");
        }

        if (hasMongo)
        {
            RequireObject(mongo, MongoMembers, MongoMembers, "MongoDB native-plan command receipt");
            RequireEnum(mongo, "kind", ["page", "count"], "MongoDB native-plan command receipt");
            RequireNonEmptyString(mongo, "redactedCommand", "MongoDB native-plan command receipt");
        }
    }

    private static void ValidateShape(JsonElement shape, string label)
    {
        RequireObject(shape, ShapeMembers,
            ["workload", "operation", "terminal", "filters", "order", "querySelectivityBasisPoints"], label);
        RequireArray(shape.GetProperty("filters"), $"{label} filters");
        var filters = shape.GetProperty("filters");
        if (filters.GetArrayLength() != 3)
            throw new InvalidOperationException($"{label} filters must contain the three canonical predicates.");
        foreach (var filter in filters.EnumerateArray())
        {
            RequireObject(filter, FilterMembers, FilterMembers, $"{label} filter");
            RequireEnum(filter, "field", ["storage-scope", "document-kind", "status"], $"{label} filter");
            RequireString(filter, "operator", "equal", $"{label} filter");
            RequireEnum(filter, "parameter", ["scope", "kind", "q0"], $"{label} filter");
        }
        RequireArray(shape.GetProperty("order"), $"{label} order");
        var order = shape.GetProperty("order");
        if (order.GetArrayLength() > 1)
            throw new InvalidOperationException($"{label} order may contain at most one term.");
        foreach (var orderTerm in order.EnumerateArray())
        {
            RequireObject(orderTerm, OrderMembers, OrderMembers, $"{label} order term");
            RequireString(orderTerm, "field", "rank", $"{label} order term");
            RequireString(orderTerm, "direction", "descending", $"{label} order term");
        }
        RequireNullableNonNegativeInteger(shape, "skip", label);
        RequireNullablePositiveInteger(shape, "take", label);
        RequireEnum(shape, "workload", ["indexedQuery", "mixedCompoundOrdering", "paginationAndCount"], label);
        RequireEnum(shape, "operation", ["selection", "count"], label);
        RequireEnum(shape, "terminal", ["documents", "count"], label);
        if (!shape.TryGetProperty("querySelectivityBasisPoints", out var selectivity) ||
            selectivity.ValueKind != JsonValueKind.Number ||
            !selectivity.TryGetInt32(out var basisPoints) || basisPoints is not (1_000 or 5_000))
        {
            throw new InvalidOperationException($"{label} has an unsupported selectivity basis point value.");
        }
    }

    private static void RequireCommandDigest(JsonElement element, string name, string label)
    {
        RequireNonEmptyString(element, name, label);
        var value = element.GetProperty(name).GetString()!;
        var digest = value.StartsWith("sha256:", StringComparison.Ordinal)
            ? value["sha256:".Length..]
            : string.Empty;
        if (digest.Length != 64 || digest.Any(character => !"0123456789abcdef".Contains(character)))
        {
            throw new InvalidOperationException($"{label} member '{name}' must be a lowercase SHA-256 identity.");
        }
    }

    private static void RequireObject(
        JsonElement element,
        ISet<string> allowed,
        IEnumerable<string> required,
        string label)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{label} must be an object.");
        var present = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (present.Any(member => !allowed.Contains(member)))
            throw new InvalidOperationException($"{label} contains an unknown member.");
        if (required.Any(member => !present.Contains(member)))
            throw new InvalidOperationException($"{label} is missing a required member.");
    }

    private static void RequireArray(JsonElement element, string label)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"{label} must be an array.");
    }

    private static void RequireString(JsonElement element, string member, string expected, string label)
    {
        if (!element.TryGetProperty(member, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !string.Equals(value.GetString(), expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{label} has an unsupported schema version.");
        }
    }

    private static void RequireEnum(JsonElement element, string member, IReadOnlyCollection<string> values, string label)
    {
        if (!element.TryGetProperty(member, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !values.Contains(value.GetString()!))
        {
            throw new InvalidOperationException($"{label} has an unsupported '{member}' value.");
        }
    }

    private static void RequireNonEmptyString(JsonElement element, string member, string label)
    {
        if (!element.TryGetProperty(member, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"{label} requires a non-empty '{member}' value.");
        }
    }

    private static void RequireNullableNonEmptyString(JsonElement element, string member, string label)
    {
        if (!element.TryGetProperty(member, out var value) || value.ValueKind == JsonValueKind.Null)
            return;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException($"{label} has an invalid '{member}' value.");
    }

    private static void RequireNullOrMissing(JsonElement element, string member, string label)
    {
        if (element.TryGetProperty(member, out var value) && value.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException($"{label} cannot retain '{member}' with a MongoDB command receipt.");
    }

    private static void RequireBoolean(JsonElement element, string member, string label)
    {
        if (!element.TryGetProperty(member, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidOperationException($"{label} requires a Boolean '{member}' value.");
        }
    }

    private static void RequireNullableNonNegativeInteger(JsonElement element, string member, string label)
    {
        if (!element.TryGetProperty(member, out var value) || value.ValueKind == JsonValueKind.Null)
            return;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 0)
            throw new InvalidOperationException($"{label} has an invalid '{member}' value.");
    }

    private static void RequireNullablePositiveInteger(JsonElement element, string member, string label)
    {
        if (!element.TryGetProperty(member, out var value) || value.ValueKind == JsonValueKind.Null)
            return;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 1)
            throw new InvalidOperationException($"{label} has an invalid '{member}' value.");
    }

    private static void RequireStringArray(JsonElement element, string label)
    {
        RequireArray(element, label);
        if (element.GetArrayLength() == 0 || element.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
        {
            throw new InvalidOperationException($"{label} must contain only non-empty strings.");
        }
    }

    private static ISet<string> Members(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
}
