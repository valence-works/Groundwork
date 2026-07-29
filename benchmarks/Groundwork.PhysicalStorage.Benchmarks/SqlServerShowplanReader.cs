using System.Data.Common;
using System.Data.SqlTypes;
using System.Xml;
using System.Xml.Linq;

namespace Groundwork.PhysicalStorage.Benchmarks;

internal static class SqlServerShowplanReader
{
    private const string ShowplanColumnMarker = "XML Showplan";
    private const string ShowplanNamespace = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private static readonly ISet<string> RetainedPhysicalOperators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Index Seek", "Table Scan", "Index Scan", "Clustered Index Scan"
    };

    public static async Task<IReadOnlyList<string>> ReadAsync(
        DbDataReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var plans = new List<string>();
        do
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.FieldCount != 1)
                    continue;

                var value = reader.GetValue(0);
                var isShowplanColumn = reader.GetName(0).Contains(
                    ShowplanColumnMarker,
                    StringComparison.OrdinalIgnoreCase);
                var text = value switch
                {
                    SqlXml xml when isShowplanColumn && !xml.IsNull => xml.Value,
                    SqlString sqlString when isShowplanColumn && !sqlString.IsNull => sqlString.Value,
                    string stringValue when isShowplanColumn => stringValue,
                    _ => null
                };
                if (text is not null && TryRetainSafeStructure(text, null, null, out var retained))
                    plans.Add(retained);
            }
        } while (await reader.NextResultAsync(cancellationToken));
        return plans;
    }

    public static void EnsureScaleBearingIndex(
        string value,
        string indexName,
        string physicalObject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalObject);
        var document = Parse(value);
        var operators = document
            .Descendants()
            .Where(element => element.Name.LocalName == "RelOp")
            .ToArray();
        var forbiddenScans = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Table Scan",
            "Index Scan",
            "Clustered Index Scan"
        };
        var hasForbiddenScan = operators.Any(element =>
            forbiddenScans.Contains(element.Attribute("PhysicalOp")?.Value ?? string.Empty));
        var usesDeclaredIndex = operators.Any(element =>
            string.Equals(element.Attribute("PhysicalOp")?.Value, "Index Seek", StringComparison.OrdinalIgnoreCase) &&
            element.Descendants().Any(candidate =>
                candidate.Name.LocalName == "Object" &&
                IsIndex(candidate.Attribute("Index")?.Value, indexName) &&
                IsIdentifier(candidate.Attribute("Table")?.Value, physicalObject)));
        if (!usesDeclaredIndex || hasForbiddenScan)
        {
            throw new InvalidOperationException(
                $"SQL Server native-plan gate rejected the scale-bearing query. Expected an Index Seek on '{indexName}'. " +
                DescribeAccessPaths(document));
        }
    }

    internal static string RetainSafeStructure(
        string value,
        string? physicalObject = null,
        string? indexName = null)
    {
        if (!TryRetainSafeStructure(value, physicalObject, indexName, out var retained))
            throw new InvalidOperationException("SQL Server native-plan evidence is not ShowPlan XML.");
        return retained;
    }

    private static string DescribeAccessPaths(XDocument document)
    {
        var physicalOperators = document
            .Descendants()
            .Attributes("PhysicalOp")
            .Select(attribute => attribute.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        var indexes = document
            .Descendants()
            .Attributes("Index")
            .Select(attribute => attribute.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        return $"Physical operators: [{string.Join(", ", physicalOperators)}]; indexes: [{string.Join(", ", indexes)}].";
    }

    private static bool IsIndex(string? candidate, string expected) =>
        string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(candidate, $"[{expected}]", StringComparison.OrdinalIgnoreCase);

    private static bool IsIdentifier(string? candidate, string expected)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        var last = candidate.Split('.').Last().Trim();
        return string.Equals(last.Trim('[', ']', '"'), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryRetainSafeStructure(
        string value,
        string? physicalObject,
        string? indexName,
        out string retained)
    {
        try
        {
            var document = Parse(value);
            if (document.Root?.Name != XName.Get("ShowPlanXML", ShowplanNamespace))
            {
                retained = string.Empty;
                return false;
            }

            retained = new XDocument(RetainElement(document.Root, physicalObject, indexName))
                .ToString(SaveOptions.DisableFormatting);
            return true;
        }
        catch (XmlException)
        {
            retained = string.Empty;
            return false;
        }
    }

    private static XElement RetainElement(
        XElement source,
        string? physicalObject,
        string? indexName)
    {
        var retained = new XElement(XName.Get(source.Name.LocalName, ShowplanNamespace));
        var bindsObject = physicalObject is not null &&
                          source.Name.LocalName == "Object" &&
                          source.Attribute("Table") is { } table &&
                          IsIdentifier(table.Value, physicalObject);
        if (bindsObject &&
            source.Attribute("Index") is { } observedIndex &&
            indexName is not null &&
            !IsIndex(observedIndex.Value, indexName))
        {
            throw new InvalidOperationException(
                "SQL Server native-plan evidence selected an unexpected index on the bound table.");
        }
        foreach (var attribute in source.Attributes().Where(attribute =>
                     !attribute.IsNamespaceDeclaration &&
                     attribute.Name.NamespaceName.Length == 0))
        {
            var value = RetainedAttributeValue(attribute, physicalObject, indexName, bindsObject);
            if (value is not null)
                retained.Add(new XAttribute(attribute.Name.LocalName, value));
        }
        foreach (var child in source.Elements().Where(element =>
                     element.Name.NamespaceName == ShowplanNamespace))
        {
            retained.Add(RetainElement(child, physicalObject, indexName));
        }
        return retained;
    }

    private static string? RetainedAttributeValue(
        XAttribute attribute,
        string? physicalObject,
        string? indexName,
        bool bindsObject) =>
        attribute.Name.LocalName switch
        {
            "PhysicalOp" when RetainedPhysicalOperators.Contains(attribute.Value) => attribute.Value,
            "Table" when physicalObject is null => attribute.Value,
            "Table" when bindsObject => $"[{physicalObject}]",
            "Index" when indexName is null => attribute.Value,
            "Index" when bindsObject && IsIndex(attribute.Value, indexName) => $"[{indexName}]",
            _ => null
        };

    private static XDocument Parse(string value)
    {
        using var input = new StringReader(value);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        return XDocument.Load(reader, LoadOptions.None);
    }
}
