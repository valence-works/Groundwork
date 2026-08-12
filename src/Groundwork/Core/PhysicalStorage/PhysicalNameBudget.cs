using System.Text;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Deterministic UTF-8 byte-budget truncation shared by provider name normalizers. Never splits
/// a rune, so truncated identifiers stay valid UTF-8 for engines that measure identifier length
/// in bytes. Public so provider assemblies share one truncation loop instead of copying it.
/// </summary>
public static class PhysicalNameBudget
{
    /// <summary>Returns the longest prefix of <paramref name="value"/> whose UTF-8 encoding fits within <paramref name="maximumBytes"/>.</summary>
    public static string TruncateUtf8(string value, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
            return value;
        var builder = new StringBuilder();
        var usedBytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (usedBytes + rune.Utf8SequenceLength > maximumBytes)
                break;
            builder.Append(rune);
            usedBytes += rune.Utf8SequenceLength;
        }
        return builder.ToString();
    }
}
