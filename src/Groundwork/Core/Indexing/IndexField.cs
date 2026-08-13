namespace Groundwork.Core.Indexing;

/// <summary>
/// One stable serialized index path. <see cref="ValueKind"/> overrides the declaration default for
/// heterogeneous compound indexes; homogeneous declarations can omit it. <see cref="Length"/>
/// declares the maximum UTF-16 code-unit count of a <see cref="IndexValueKind.String"/> or
/// <see cref="IndexValueKind.Keyword"/> field so default resolution can synthesize a bounded
/// projected column instead of an unbounded one that providers with sized index keys must reject.
/// <see cref="Precision"/> and <see cref="Scale"/> declare the portable decimal shape of a
/// <see cref="IndexValueKind.Number"/> field so default resolution can synthesize its projected
/// column instead of refusing to invent decimal precision; a field declaring either component
/// overrides any declaration-level defaults as one pair.
/// </summary>
public sealed record IndexField(
    string Path,
    IndexValueKind? ValueKind = null,
    int? Length = null,
    int? Precision = null,
    int? Scale = null);
