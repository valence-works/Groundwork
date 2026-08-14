using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Store;
using Xunit;

namespace Groundwork.TestInfrastructure;

/// <summary>
/// The one row-visibility contract an index declared <see cref="MissingValueBehavior.Excluded"/> carries,
/// asserted the same way on every provider (valence-works/Groundwork#178).
/// </summary>
/// <remarks>
/// <para>
/// The shape is a nullable projected <c>category</c> keyed by a null-excluding index, three documents —
/// two with a category and one with none — and a query bound to that index whose predicate no provider
/// can prove avoids the omitted rows. Row exclusion is a storage choice, so the answer has to be either
/// every row the predicate matches or a refusal that names the index. What it may never be is a short
/// result set.
/// </para>
/// <para>
/// Each provider decides the pin its own way, and has to: the relational lane proves a column non-null,
/// while MongoDB proves a field present, because its partial filter keeps a document holding an explicit
/// null. Providers therefore land on different branches for the same manifest — SQL Server, SQLite, and
/// MongoDB pin the index and so refuse, while PostgreSQL pins nothing and answers. This asserts what all
/// four owe regardless: <see cref="VerifyQueriesAsync"/> accepts either outcome per case and pins the row
/// set in both, rather than asserting one branch and calling the other a bug.
/// </para>
/// <para>
/// The variable here is therefore the provider. <c>SqlServerNullExcludedIndexHintTests</c> and
/// <c>MongoDbNullExcludedIndexHintTests</c> vary filtered-ness within one provider instead — uniqueness
/// and nullability one ingredient at a time, asserted against the emitted index and the rendered command —
/// and the two are not substitutes for one another.
/// </para>
/// </remarks>
public static class NullExcludedIndexConformance
{
    public const string DocumentKind = "configurationDocument";
    public const string QueryIdentity = "list-by-category";
    public const string DeleteIdentity = "prune-by-category";
    public const string IndexIdentity = "by-category";
    public const string CategoryPath = "category";
    public const string CategoryColumn = "category";

    public const string AlphaId = "categorised-alpha";
    public const string BetaId = "categorised-beta";
    public const string UncategorisedId = "uncategorised";

    /// <summary>The predicate every provider can prove, and therefore must always serve.</summary>
    public static DocumentQueryComparison Provable => DocumentQueryComparison.Equal(CategoryPath, "alpha");

    /// <summary>
    /// The predicates no provider can prove, each with the rows it matches: a null equality asks for
    /// exactly the documents the index omits, and a membership set containing null asks for them alongside
    /// others. <c>NotContains</c> is the third shape and behaves the same way, but MongoDB refuses to
    /// certify it on a scale-bearing route at all — its case-insensitive regular expression cannot be
    /// served by a B-tree index — so it cannot appear in a manifest every provider admits.
    /// </summary>
    public static IReadOnlyList<UnprovenCase> UnprovenCases { get; } =
    [
        new(
            DocumentQueryComparison.Equal(CategoryPath, null),
            [UncategorisedId]),
        new(
            DocumentQueryComparison.In(CategoryPath, ["alpha", null]),
            [AlphaId, UncategorisedId])
    ];

    /// <summary>One unprovable predicate and the documents it matches, whichever provider runs it.</summary>
    public sealed record UnprovenCase(DocumentQueryComparison Comparison, IReadOnlyList<string> ExpectedIds);

    public static DocumentQuery Query(DocumentQueryComparison comparison) =>
        new(DocumentKind, QueryIdentity, [DocumentQueryClause.Of(comparison)]);

    public static StorageManifest CreateManifest(
        string instance,
        PhysicalStorageForm form,
        MissingValueBehavior missingValues = MissingValueBehavior.Excluded,
        bool includeDelete = false,
        BoundedQueryExecutionClass executionClass = BoundedQueryExecutionClass.ScaleBearing)
    {
        var envelope = new DocumentEnvelopeDefinition();
        var binding = new SharedStorageBinding($"runtime_{instance}");
        var column = new ProjectedColumnDefinition(
            CategoryColumn,
            CategoryPath,
            PortablePhysicalType.String,
            Length: 32,
            IsNullable: true);
        var physicalIndex = new PhysicalIndexDefinition(
            IndexIdentity,
            [
                new PhysicalIndexColumnDefinition(CategoryColumn, 0),
                new PhysicalIndexColumnDefinition(envelope.IdComparisonKeyColumn, 1)
            ],
            missingValueBehavior: missingValues);
        var definition = form switch
        {
            PhysicalStorageForm.SharedDocuments => PhysicalTableDefinition.SharedDocuments(
                binding, [column], [physicalIndex], linkedProjectionLogicalName: "configuration_projection"),
            PhysicalStorageForm.DedicatedDocumentTable => PhysicalTableDefinition.DedicatedDocumentTable(
                "configuration_documents",
                indexes: [physicalIndex],
                linkedProjectedColumns: [column],
                linkedProjectionLogicalName: "configuration_projection"),
            PhysicalStorageForm.PhysicalEntityTable => PhysicalTableDefinition.PhysicalEntityTable(
                "configuration_entities", [column], indexes: [physicalIndex]),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
        var logicalIndex = new LogicalIndexDeclaration(
            IndexIdentity,
            [new IndexField(CategoryPath)],
            IndexValueKind.String,
            isUnique: false,
            missingValues);
        var query = new BoundedQueryDeclaration(
            QueryIdentity,
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.In
            },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Offset,
            executionClass,
            supportsTotalCount: true,
            resultOperations: new HashSet<BoundedQueryResultOperation>
            {
                BoundedQueryResultOperation.Documents,
                BoundedQueryResultOperation.Count
            });
        var unit = new StorageUnit(
            new StorageUnitIdentity(DocumentKind),
            "Configuration document",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Global,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [logicalIndex],
                [query],
                boundedMutations: includeDelete
                    ? [new BoundedMutationDeclaration(DeleteIdentity, QueryIdentity, BoundedMutationAction.Delete())]
                    : []));
        return new StorageManifest(
            new StorageManifestIdentity($"null-excluded-index.{instance}"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            [])
        {
            SharedDocumentStorages = form == PhysicalStorageForm.SharedDocuments
                ? [new SharedDocumentStorageDefinition(binding, $"documents_{instance}", envelope)]
                : []
        };
    }

    public static PhysicalSchemaTarget CreateTarget(
        StorageManifest manifest,
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        string instance)
    {
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            new DelegatePhysicalNamePolicy(context => $"gw_{instance}_{context.FeatureDefaultLogicalName}"),
            normalizer);
        Assert.True(
            resolution.IsValid,
            string.Join("; ", resolution.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(
            compilation.IsValid,
            string.Join("; ", compilation.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return new PhysicalSchemaTarget(manifest.Identity, manifest.Version, provider, compilation.Routes);
    }

    public static ExecutablePhysicalIndexRoute CategoryIndex(ExecutableStorageRoute route) =>
        route.Indexes.Single(index => index.Identity == IndexIdentity);

    public static string CategoryColumnIdentifier(ExecutableStorageRoute route) =>
        route.ProjectedColumns.Single(column => column.Definition.Path == CategoryPath).Column.Identifier;

    public static async Task SeedAsync(IDocumentStore writer)
    {
        await SaveAsync(writer, AlphaId, """{"category":"alpha"}""");
        await SaveAsync(writer, BetaId, """{"category":"beta"}""");
        // No category at all rather than an explicit null: MongoDB realises row exclusion as
        // `$exists`, so only an absent path is actually omitted from the index there.
        await SaveAsync(writer, UncategorisedId, "{}");
    }

    /// <summary>
    /// The contract. A provable predicate is always served, and an unprovable one either returns every
    /// row it matches or is refused by a message naming the query, the index, and the excluded column.
    /// </summary>
    public static async Task VerifyQueriesAsync(
        IBoundedDocumentStore queries,
        ExecutableStorageRoute route,
        Action<string>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        var indexName = CategoryIndex(route).Name.Identifier;
        var column = CategoryColumnIdentifier(route);

        var provable = await queries.QueryAsync(Query(Provable));
        Assert.Equal([AlphaId], provable.Documents.Select(document => document.Id).Order());
        Assert.Equal(1, provable.TotalCount);

        // An empty membership set matches nothing, so it cannot drop anything either: every index serves
        // it equally and none may refuse it. Regressed once on the relational side by reading the
        // resulting contradiction as "proves nothing".
        var empty = await queries.QueryAsync(Query(DocumentQueryComparison.In(CategoryPath, [])));
        Assert.Empty(empty.Documents);
        Assert.Equal(0, empty.TotalCount);

        foreach (var unproven in UnprovenCases)
        {
            var query = Query(unproven.Comparison);
            DocumentQueryResult result;
            try
            {
                result = await queries.QueryAsync(query);
            }
            catch (InvalidOperationException refusal)
            {
                trace?.Invoke($"[{unproven.Comparison.Operator}] refused: {refusal.Message}");
                Assert.Contains(QueryIdentity, refusal.Message, StringComparison.Ordinal);
                Assert.Contains(indexName, refusal.Message, StringComparison.Ordinal);
                Assert.Contains(column, refusal.Message, StringComparison.Ordinal);
                // The refusal belongs to the predicate, not to the terminal operation, so a count that
                // reported a number here would be the same short answer in another shape.
                var counted = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    queries.CountAsync(query.Select(BoundedQueryResultOperation.Count)));
                Assert.Contains(indexName, counted.Message, StringComparison.Ordinal);
                continue;
            }

            trace?.Invoke(
                $"[{unproven.Comparison.Operator}] answered: " +
                string.Join(", ", result.Documents.Select(document => document.Id).Order()));
            Assert.Equal(
                unproven.ExpectedIds.Order(),
                result.Documents.Select(document => document.Id).Order());
            Assert.Equal(unproven.ExpectedIds.Count, result.TotalCount);
            Assert.Equal(
                unproven.ExpectedIds.Count,
                await queries.CountAsync(query.Select(BoundedQueryResultOperation.Count)));
        }
    }

    /// <summary>
    /// The same contract for the mutation lane, where under-matching leaves rows unmutated rather than
    /// merely unreported. The bounded delete selects on a membership set containing null, which matches
    /// the document with no category.
    /// </summary>
    public static async Task VerifyBoundedDeleteAsync(
        IDocumentStore writer,
        IBoundedDocumentMutationStore mutations,
        ExecutableStorageRoute route,
        Action<string>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(route);
        var indexName = CategoryIndex(route).Name.Identifier;
        var column = CategoryColumnIdentifier(route);
        var mutation = new DocumentMutation(
            DocumentKind,
            DeleteIdentity,
            "prune-1",
            [DocumentQueryClause.Of(DocumentQueryComparison.In(CategoryPath, ["alpha", null]))]);

        BoundedMutationResult result;
        try
        {
            result = await mutations.ExecuteAsync(mutation);
        }
        catch (InvalidOperationException refusal)
        {
            trace?.Invoke($"[delete] refused: {refusal.Message}");
            Assert.Contains(indexName, refusal.Message, StringComparison.Ordinal);
            Assert.Contains(column, refusal.Message, StringComparison.Ordinal);
            Assert.NotNull(await writer.LoadAsync(DocumentKind, UncategorisedId));
            return;
        }

        trace?.Invoke($"[delete] applied: {result.AffectedCount}");
        Assert.Equal(BoundedMutationStatus.Completed, result.Status);
        Assert.Equal(2, result.AffectedCount);
        Assert.Null(await writer.LoadAsync(DocumentKind, AlphaId));
        Assert.Null(await writer.LoadAsync(DocumentKind, UncategorisedId));
        Assert.NotNull(await writer.LoadAsync(DocumentKind, BetaId));
    }

    private static async Task SaveAsync(IDocumentStore writer, string id, string contentJson) =>
        Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await writer.SaveAsync(new SaveDocumentRequest(DocumentKind, id, "1", contentJson))).Status);
}
