namespace Groundwork.Core.Indexing;

[Obsolete(
    "Use LogicalIndexDeclaration for logical lookup intent and PhysicalIndexDefinition for physical structure.",
    DiagnosticId = "GW0002")]
public sealed record IndexDeclaration(
    string Identity,
    IReadOnlyList<IndexField> Fields,
    IndexValueKind ValueKind,
    bool IsUnique,
    bool IsSortable,
    MissingValueBehavior MissingValueBehavior,
    IReadOnlySet<PortableQueryOperation> SupportedOperations,
    IndexPhysicalizationPolicy Physicalization = IndexPhysicalizationPolicy.Default);

/// <summary>
/// One stable serialized index path. <see cref="ValueKind"/> overrides the declaration default for
/// heterogeneous compound indexes; homogeneous declarations can omit it. <see cref="Length"/>
/// declares the maximum UTF-16 code-unit count of a <see cref="IndexValueKind.String"/> or
/// <see cref="IndexValueKind.Keyword"/> field so default resolution can synthesize a bounded
/// projected column instead of an unbounded one that providers with sized index keys must reject.
/// </summary>
public sealed record IndexField(
    string Path,
    IndexValueKind? ValueKind = null,
    int? Length = null);

public enum IndexValueKind
{
    String,
    Number,
    Boolean,
    DateTime,
    Keyword
}

/// <summary>
/// Whether an index keeps rows that have no value for its keyed fields.
/// </summary>
/// <remarks>
/// Every provider honours this identically: it is the only thing that decides row exclusion, and it is
/// realised as a filtered index on SQL Server and PostgreSQL, a partial index on SQLite, and a partial
/// filter expression on MongoDB.
/// </remarks>
public enum MissingValueBehavior
{
    /// <summary>
    /// Rows without a value are absent from the index. Opt in to this deliberately: the index becomes
    /// unable to serve any predicate that must return those rows — <c>NotContains</c>, a null equality,
    /// or a disjunction spanning another field — and a scale-bearing query bound to it is refused
    /// rather than silently under-served.
    /// </summary>
    /// <remarks>
    /// Note that this is not the zero value of the enum but it is also not the default any declaration
    /// takes: both <see cref="Groundwork.Core.PhysicalStorage.LogicalIndexDeclaration"/> and
    /// <see cref="Groundwork.Core.PhysicalStorage.PhysicalIndexDefinition"/> default to
    /// <see cref="IncludedAsNull"/>, so a sparse index is always something someone asked for.
    /// </remarks>
    Excluded,

    /// <summary>
    /// Every row is indexed, with the missing value ordered as null. The default, because an index that
    /// quietly omits rows is a sharp edge that should be chosen rather than inherited — and because the
    /// narrowing is invisible until some query needs one of the omitted rows.
    /// </summary>
    IncludedAsNull
}

public enum PortableQueryOperation
{
    Equal,
    NotEqual,
    StartsWith,
    Contains,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    In,
    NotContains,
    CollectionContains,
    CollectionContainsAll
}

[Obsolete(
    "Physical placement belongs to PhysicalTableDefinition. Convert existing declarations with LegacyPhysicalStorageBridge.",
    DiagnosticId = "GW0002")]
public enum IndexPhysicalizationPolicy
{
    Default,
    Portable,
    Optimized
}
