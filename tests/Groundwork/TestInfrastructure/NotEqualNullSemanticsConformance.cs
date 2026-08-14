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
/// Pins <see cref="QueryComparisonOperator.NotEqual"/> as the exact complement of
/// <see cref="QueryComparisonOperator.Equal"/> on every provider.
/// </summary>
/// <remarks>
/// <para>
/// The providers used to disagree in silence. MongoDB's <c>{$ne: v}</c> matches a document that has no
/// such field; relational <c>&lt;&gt; @p</c> is unknown for NULL and drops that row. Nothing in the
/// portable contract said which was right, so the same manifest answered the same question two ways.
/// </para>
/// <para>
/// The settled reading is the complement, which is the one <see cref="QueryComparisonOperator.NotContains"/>
/// already had: a value is either equal or not, and no document may fall through both halves. The
/// partition assertion below is the load-bearing one — it fails on any provider that keeps SQL's
/// three-valued reading, because the absent and null documents would then be in neither half.
/// </para>
/// </remarks>
public static class NotEqualNullSemanticsConformance
{
    public const string DocumentKind = "secret";
    public const string QueryIdentity = "list-by-status";

    public static StorageManifest CreateManifest(string instance)
    {
        var index = new LogicalIndexDeclaration(
            "by-status",
            [new IndexField("status")],
            IndexValueKind.Keyword,
            false,
            // The complement has to be answerable for documents with no value, so the index keeps them.
            // Whether a null-excluding index may serve it is a separate question, settled by the pin
            // decision and covered by the null-excluded index hint tests.
            MissingValueBehavior.IncludedAsNull);
        var query = new BoundedQueryDeclaration(
            QueryIdentity,
            index.Identity,
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.NotEqual
            },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "secrets",
            [
                new ProjectedColumnDefinition(
                    "status",
                    "status",
                    PortablePhysicalType.String,
                    Length: 100,
                    IsNullable: true)
            ],
            indexes:
            [
                // A global unit needs no scope prefix, and a query that neither sorts nor pages needs no
                // identity tie-break, so the scale-bearing shape is exactly the one keyed column.
                new PhysicalIndexDefinition(
                    index.Identity,
                    [new PhysicalIndexColumnDefinition("status", 0)],
                    missingValueBehavior: MissingValueBehavior.IncludedAsNull)
            ]);
        var unit = new StorageUnit(
            new StorageUnitIdentity(DocumentKind),
            "Secret",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Global,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [index],
                [query]));
        return new StorageManifest(
            new StorageManifestIdentity($"not-equal-null-semantics.{instance}"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            []);
    }

    public static PhysicalSchemaTarget CreateTarget(
        StorageManifest manifest,
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        string instance)
    {
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            new DelegatePhysicalNamePolicy(context =>
                $"gw_{instance}_{context.FeatureDefaultLogicalName}"),
            normalizer);
        Assert.True(
            resolution.IsValid,
            string.Join("; ", resolution.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(
            compilation.IsValid,
            string.Join("; ", compilation.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return new PhysicalSchemaTarget(
            manifest.Identity,
            manifest.Version,
            provider,
            compilation.Routes);
    }

    public static async Task VerifyAsync(IDocumentStore writer, IBoundedDocumentStore queries)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(queries);
        await SaveAsync("absent", "{}");
        await SaveAsync("null", """{"status":null}""");
        await SaveAsync("active", """{"status":"active"}""");
        await SaveAsync("revoked", """{"status":"revoked"}""");

        var equalActive = await IdsAsync(DocumentQueryComparison.Equal("status", "active"));
        var notEqualActive = await IdsAsync(DocumentQueryComparison.NotEqual("status", "active"));
        var equalNull = await IdsAsync(DocumentQueryComparison.Equal("status", null));
        var notEqualNull = await IdsAsync(DocumentQueryComparison.NotEqual("status", null));

        // A document with no value is not equal to "active", so it is on the NotEqual side. This is the
        // assertion the relational providers used to fail.
        Assert.Equal(["active"], equalActive);
        Assert.Equal(["absent", "null", "revoked"], notEqualActive);

        // Null is the one value where the complement runs the other way: Equal to null is what collects
        // the documents with no value, so NotEqual to null is what excludes them.
        Assert.Equal(["absent", "null"], equalNull);
        Assert.Equal(["active", "revoked"], notEqualNull);

        // The property all four cells exist to protect: every document lands on exactly one side of the
        // split, whichever value is asked about, so a two-branch query can neither lose nor double-count.
        foreach (var value in new[] { "active", "revoked", "missing", null })
        {
            var equal = await IdsAsync(DocumentQueryComparison.Equal("status", value));
            var notEqual = await IdsAsync(DocumentQueryComparison.NotEqual("status", value));
            Assert.Empty(equal.Intersect(notEqual, StringComparer.Ordinal));
            Assert.Equal<IEnumerable<string>>(
                ["absent", "active", "null", "revoked"],
                equal.Concat(notEqual).Order(StringComparer.Ordinal).ToArray());
        }

        async Task<IReadOnlyList<string>> IdsAsync(DocumentQueryComparison comparison)
        {
            var result = await queries.QueryAsync(new DocumentQuery(
                DocumentKind,
                QueryIdentity,
                [DocumentQueryClause.Of(comparison)]));
            Assert.Equal(result.Documents.Count, result.TotalCount);
            return result.Documents.Select(document => document.Id).Order(StringComparer.Ordinal).ToArray();
        }

        async Task SaveAsync(string id, string content) =>
            Assert.Equal(
                DocumentStoreWriteStatus.Saved,
                (await writer.SaveAsync(new SaveDocumentRequest(DocumentKind, id, "1", content))).Status);
    }
}
