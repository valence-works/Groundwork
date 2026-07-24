using System.Globalization;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Validation;

namespace Groundwork.Documents.Store;

/// <summary>
/// Applies the compiled collection projection's exact scalar type semantics before request-set
/// cardinality is admitted. Providers may encode the resulting values differently, but aliases
/// such as <c>1</c>, <c>1.0</c>, and <c>1e0</c> cannot amplify native membership predicates.
/// </summary>
internal static class PhysicalCollectionQueryValueSet
{
    public static int CountDistinct(
        IReadOnlyList<string?> values,
        PhysicalQueryCollectionConstraint constraint)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(constraint);
        return values
            .Select(value => Normalize(
                value ?? throw new InvalidDataException("Collection membership values cannot be null."),
                constraint.PhysicalType))
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    private static string Normalize(string value, PortablePhysicalType physicalType)
    {
        try
        {
            return physicalType switch
            {
                PortablePhysicalType.String => $"string:{value}",
                PortablePhysicalType.Int32 or
                PortablePhysicalType.Int64 or
                PortablePhysicalType.Decimal => $"number:{CanonicalNumber(value)}",
                PortablePhysicalType.Boolean => $"boolean:{bool.Parse(value)}",
                PortablePhysicalType.DateTime => $"datetime:{ParseDateTime(value).UtcTicks}",
                PortablePhysicalType.Guid => $"guid:{Guid.Parse(value):D}",
                PortablePhysicalType.Binary => $"binary:{Convert.ToBase64String(Convert.FromBase64String(value))}",
                _ => throw new InvalidOperationException(
                    $"Physical type '{physicalType}' cannot back an exact collection-membership request.")
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new InvalidDataException(
                $"Collection membership value cannot be converted to '{physicalType}'.",
                exception);
        }
    }

    private static string CanonicalNumber(string value)
    {
        var text = value.AsSpan().Trim();
        if (text.IsEmpty)
            throw new FormatException("A numeric value cannot be empty.");

        var index = 0;
        var negative = false;
        if (text[index] is '+' or '-')
        {
            negative = text[index] == '-';
            index++;
        }

        var integerStart = index;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
            index++;
        var integerEnd = index;
        var fractionStart = index;
        var fractionEnd = index;
        if (index < text.Length && text[index] == '.')
        {
            fractionStart = ++index;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
                index++;
            fractionEnd = index;
        }
        if (integerEnd == integerStart && fractionEnd == fractionStart)
            throw new FormatException($"Numeric value '{value}' has no digits.");

        long exponent = 0;
        if (index < text.Length && text[index] is 'e' or 'E')
        {
            index++;
            var exponentNegative = false;
            if (index < text.Length && text[index] is '+' or '-')
            {
                exponentNegative = text[index] == '-';
                index++;
            }
            var exponentStart = index;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                exponent = checked(exponent * 10 + text[index] - '0');
                index++;
            }
            if (index == exponentStart)
                throw new FormatException($"Numeric value '{value}' has no exponent digits.");
            if (exponentNegative)
                exponent = checked(-exponent);
        }
        if (index != text.Length)
            throw new FormatException($"Numeric value '{value}' contains unsupported characters.");

        var digits = (text[integerStart..integerEnd].ToString() +
                      text[fractionStart..fractionEnd].ToString()).TrimStart('0');
        if (digits.Length == 0)
            return "0";
        var power = checked(exponent - (fractionEnd - fractionStart));
        while (digits.EndsWith('0'))
        {
            digits = digits[..^1];
            power = checked(power + 1);
        }
        return $"{(negative ? "-" : "")}{digits}e{power.ToString(CultureInfo.InvariantCulture)}";
    }

    private static DateTimeOffset ParseDateTime(string value)
    {
        var text = value.Trim();
        var timeSeparator = text.IndexOf('T');
        if (timeSeparator < 0)
            timeSeparator = text.IndexOf('t');
        var offsetSeparator = timeSeparator < 0
            ? -1
            : text.IndexOfAny(['+', '-'], timeSeparator + 1);
        if (!text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) && offsetSeparator < 0)
            throw new FormatException("A DateTime value requires an explicit UTC designator or numeric offset.");
        var fractionalSeparator = timeSeparator < 0 ? -1 : text.IndexOfAny(['.', ','], timeSeparator + 1);
        if (fractionalSeparator >= 0)
        {
            var fractionalEnd = offsetSeparator >= 0
                ? offsetSeparator
                : text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ? text.Length - 1 : text.Length;
            if (fractionalEnd - fractionalSeparator - 1 > 7)
                throw new FormatException("A DateTime value cannot exceed 100ns fractional-second precision.");
        }
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}

public sealed class PhysicalQueryRequestValidationException(GroundworkDiagnostic diagnostic)
    : InvalidOperationException(CreateMessage(diagnostic))
{
    public GroundworkDiagnostic Diagnostic { get; } =
        diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));

    private static string CreateMessage(GroundworkDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return $"{diagnostic.Code}: {diagnostic.Message}";
    }
}
