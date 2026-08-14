using Groundwork.Core.PhysicalStorage;
using Groundwork.Relational.Documents;
using Groundwork.Relational.PhysicalStorage;

namespace Groundwork.SqlServer.PhysicalStorage;

/// <summary>
/// Owns SQL Server's bounded opaque physical identity while retaining every original value for
/// exact ordinal verification. The hash-expression seam is internal and exists solely to prove
/// collision handling deterministically.
/// </summary>
internal sealed class SqlServerPhysicalIdentity
{
    private readonly SqlServerPhysicalIdentityHash hash;

    public SqlServerPhysicalIdentity(SqlServerPhysicalIdentityHash hash) =>
        this.hash = hash ?? throw new ArgumentNullException(nameof(hash));

    public RelationalPhysicalIdentityLayout Layout(
        IReadOnlyList<RelationalPhysicalIdentityColumn> identityColumns,
        IReadOnlyList<string> logicalPrimaryKey,
        Func<string, string> quote)
    {
        ArgumentNullException.ThrowIfNull(identityColumns);
        ArgumentNullException.ThrowIfNull(logicalPrimaryKey);
        ArgumentNullException.ThrowIfNull(quote);
        var identityNames = identityColumns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        if (logicalPrimaryKey.Any(column => !identityNames.Contains(column)))
            throw new ArgumentException("Every logical primary-key column must be a retained identity column.", nameof(logicalPrimaryKey));

        var columns = identityColumns.Select(column =>
        {
            var hidden = HiddenColumn(column.Name);
            return new RelationalProviderOwnedPhysicalColumn(
                hidden,
                $"{quote(hidden)} AS {hash.Expression(quote(column.Name))} PERSISTED NOT NULL",
                "binary(32)",
                false,
                IsComputed: true,
                IsPersisted: true,
                ComputedDefinition: hash.Expression(quote(column.Name)));
        }).ToArray();
        return new RelationalPhysicalIdentityLayout(
            Array.AsReadOnly(columns),
            Array.AsReadOnly(logicalPrimaryKey.Select(HiddenColumn).ToArray()));
    }

    /// <summary>
    /// The key columns SQL Server emits for <paramref name="index"/>. ANSI-padded NVARCHAR equality
    /// treats values differing only by trailing spaces as duplicates, so a unique index gains one
    /// provider-owned persisted hash column per projected string key column; the hash is byte-exact,
    /// restoring the portable exact-match uniqueness contract. The declared columns stay in front so
    /// query seeks and index pins are unaffected.
    /// </summary>
    public IReadOnlyList<RelationalPhysicalIndexKeyColumn> IndexKeyColumns(
        ExecutableStorageRoute route,
        ExecutablePhysicalIndexRoute index,
        Func<string, string> quote)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(quote);
        var declared = index.Columns
            .Select(column => new RelationalPhysicalIndexKeyColumn(column.Column.Identifier, column.Direction));
        return index.IsUnique
            ? declared.Concat(UniqueStringKeyColumns(route, index).Select(projection =>
            {
                var hidden = HiddenColumn(projection.Column.Identifier);
                var expression = hash.Expression(quote(projection.Column.Identifier));
                return new RelationalPhysicalIndexKeyColumn(
                    hidden,
                    PhysicalSortDirection.Ascending,
                    new RelationalProviderOwnedPhysicalColumn(
                        hidden,
                        $"{quote(hidden)} AS {expression} PERSISTED{(projection.Definition.IsNullable ? "" : " NOT NULL")}",
                        "binary(32)",
                        projection.Definition.IsNullable,
                        IsComputed: true,
                        IsPersisted: true,
                        ComputedDefinition: expression));
            })).ToArray()
            : declared.ToArray();
    }

    /// <summary>The projected string key columns of <paramref name="index"/> in declared order.</summary>
    public static IReadOnlyList<ExecutableProjectedColumnRoute> UniqueStringKeyColumns(
        ExecutableStorageRoute route,
        ExecutablePhysicalIndexRoute index) =>
        index.Columns
            .Select(indexColumn => route.ProjectedColumns.SingleOrDefault(column =>
                column.Target == index.Target &&
                column.Column.Identifier == indexColumn.Column.Identifier &&
                column.Definition.Type == PortablePhysicalType.String))
            .Where(projection => projection is not null)
            .Select(projection => projection!)
            .ToArray();

    public void ValidateRoute(ExecutableStorageRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var primaryIdentityColumns = new[]
        {
            route.Envelope.DocumentKind.Identifier,
            route.Envelope.StorageScope.Identifier,
            route.Envelope.Id.Identifier,
            route.Envelope.Identity.ComparisonKey.Identifier,
            route.Envelope.Identity.LookupKey.Identifier
        };
        ValidateTable(
            route,
            route.PrimaryStorage.Name.Identifier,
            primaryIdentityColumns.Concat(IndexHashColumnSources(route, ExecutableStorageObjectRole.PrimaryStorage)).ToArray(),
            primaryIdentityColumns.Concat(
            [
                route.Envelope.SchemaVersion.Identifier,
                route.Envelope.Version.Identifier,
                route.Envelope.CanonicalJson.Identifier,
                RelationalPhysicalStorageColumns.CreatedUtc,
                RelationalPhysicalStorageColumns.UpdatedUtc
            ]).Concat(route.ProjectedColumns
                .Where(column => column.Target == ExecutableStorageObjectRole.PrimaryStorage)
                .Select(column => column.Column.Identifier)));

        if (route.LinkedIndexStorage is not null)
        {
            var relationship = route.LinkedRelationship!;
            var linkedIdentityColumns = new[]
            {
                relationship.DocumentKind.Identifier,
                relationship.StorageScope.Identifier,
                relationship.DocumentId.Identifier,
                relationship.Identity.ComparisonKey.Identifier,
                relationship.Identity.LookupKey.Identifier
            };
            ValidateTable(
                route,
                route.LinkedIndexStorage.Name.Identifier,
                linkedIdentityColumns.Concat(IndexHashColumnSources(route, ExecutableStorageObjectRole.LinkedIndexStorage)).ToArray(),
                linkedIdentityColumns.Concat(route.ProjectedColumns
                    .Where(column => column.Target == ExecutableStorageObjectRole.LinkedIndexStorage)
                    .Select(column => column.Column.Identifier)));
        }

        SqlServerPhysicalIndexValidator.Validate(route);
    }

    public string ExactPredicate(
        IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts,
        Func<string, string> quote,
        bool includeOriginal)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(quote);
        return string.Join(" AND ", parts.SelectMany(part =>
        {
            var key = $"{Qualified(part.Alias, HiddenColumn(part.ColumnIdentifier), quote)} = {hash.Expression(part.ValueExpression)}";
            return includeOriginal
                ? new[] { key, $"{Qualified(part.Alias, part.ColumnIdentifier, quote)} = {part.ValueExpression}" }
                : [key];
        }));
    }

    public static string ExactJoin(
        IReadOnlyList<RelationalPhysicalIdentityJoinPart> parts,
        Func<string, string> quote)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(quote);
        return string.Join(" AND ", parts.SelectMany(part => new[]
        {
            $"{Qualified(part.LeftAlias, HiddenColumn(part.LeftColumnIdentifier), quote)} = {Qualified(part.RightAlias, HiddenColumn(part.RightColumnIdentifier), quote)}",
            $"{Qualified(part.LeftAlias, part.LeftColumnIdentifier, quote)} = {Qualified(part.RightAlias, part.RightColumnIdentifier, quote)}"
        }));
    }

    private static IEnumerable<string> IndexHashColumnSources(
        ExecutableStorageRoute route,
        ExecutableStorageObjectRole target) =>
        route.Indexes
            .Where(index => index.IsUnique && index.Target == target)
            .SelectMany(index => UniqueStringKeyColumns(route, index))
            .Select(projection => projection.Column.Identifier)
            .Distinct(StringComparer.Ordinal);

    private static void ValidateTable(
        ExecutableStorageRoute route,
        string table,
        IReadOnlyList<string> hiddenColumnSources,
        IEnumerable<string> visibleColumns)
    {
        var hidden = hiddenColumnSources.Select(HiddenColumn).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var collision = visibleColumns.FirstOrDefault(hidden.Contains);
        if (collision is not null)
        {
            throw new InvalidOperationException(
                $"Executable route '{route.StorageUnit.Value}' maps visible column '{table}.{collision}', which collides with a SQL Server provider-owned column.");
        }
        if (hidden.Count != hiddenColumnSources.Count)
            throw new InvalidOperationException($"Executable route '{route.StorageUnit.Value}' produces duplicate SQL Server provider-owned columns in '{table}'.");
    }

    public static string HiddenColumn(string retainedColumn) =>
        SqlServerPhysicalName.Normalize($"{retainedColumn}_key");

    private static string Qualified(string? alias, string identifier, Func<string, string> quote) =>
        alias is null ? quote(identifier) : $"{alias}.{quote(identifier)}";
}

internal sealed class SqlServerPhysicalIdentityHash
{
    private readonly Func<string, string> expression;

    public SqlServerPhysicalIdentityHash(Func<string, string>? expression = null) =>
        this.expression = expression ?? (value =>
            $"CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), {value})))");

    public string Expression(string valueExpression) => expression(valueExpression);
}

internal static class SqlServerUnboundedIdentityHash
{
    public static string Expression(string valueExpression) =>
        $"CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), {valueExpression})))";
}

internal static class SqlServerMutationOperationIdentity
{
    public static string ExactPredicate(
        IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts,
        Func<string, string> quote) =>
        RelationalMutationOperationIdentity.ExactPredicate(
            parts,
            quote,
            SqlServerUnboundedIdentityHash.Expression);
}
