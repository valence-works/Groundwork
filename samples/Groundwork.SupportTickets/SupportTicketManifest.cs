using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;

namespace Groundwork.SupportTickets;

/// <summary>
/// Declares the support-ticket storage units through the current physical-storage surface:
/// logical indexes, bounded query declarations, and explicit physical-entity-table definitions
/// with bounded projected columns. Every repository query executes one of the bounded
/// declarations below; nothing queries outside them.
/// </summary>
public static class SupportTicketManifest
{
    public const string DocumentKind = "supportTicket";
    public const string CommentDocumentKind = "supportTicketComment";
    public const string SchemaVersion = "1.0.0";

    // Logical index identities.
    public const string ByTicketNumber = "by-ticket-number";
    public const string ByCustomer = "by-customer";
    public const string ByStatus = "by-status";
    public const string ByAssignee = "by-assignee";
    public const string ByPriority = "by-priority";
    public const string ByCommentTicket = "by-comment-ticket";
    public const string ByCommentAuthor = "by-comment-author";

    // Bounded query identities, one per repository operation.
    public const string FindByTicketNumber = "find-by-ticket-number";
    public const string ListByCustomer = "list-by-customer";
    public const string ListByStatus = "list-by-status";
    public const string ListByAssignee = "list-by-assignee";
    public const string ListByPriority = "list-by-priority";
    public const string ListCommentsByTicket = "list-comments-by-ticket";
    public const string ListCommentsByAuthor = "list-comments-by-author";

    // Maximum UTF-16 code units per keyword key column, so providers with sized index
    // keys (SQL Server) can bound the synthesized physical indexes.
    public const int KeywordKeyLength = 128;

    // Stable serialized paths addressed by runtime DocumentQuery comparisons.
    public const string TicketNumberPath = "ticketNumber";
    public const string CustomerIdPath = "customerId";
    public const string StatusPath = "status";
    public const string AssigneeIdPath = "assigneeId";
    public const string PriorityPath = "priority";
    public const string CommentAuthorIdPath = "authorId";

    // Keyword columns are bounded to 128 UTF-16 code units so the widest SQL Server index key —
    // one keyword column plus the provider-applied identity tie-break column — stays inside the
    // provider's 1700-byte key budget: 128 * 2 + 1350 (id_comparison_key) = 1606 bytes.
    private const int KeywordColumnLength = 128;

    private static readonly DocumentEnvelopeDefinition Envelope = new();

    public static StorageManifest Create() =>
        new(
            new StorageManifestIdentity("support-tickets"),
            new StorageManifestOwner("groundwork.sample.support"),
            new StorageManifestVersion(SchemaVersion),
            [TicketUnit(), CommentUnit()],
            new HashSet<string> { "schema-history", "optimistic-concurrency" },
            []);

    private static StorageUnit TicketUnit() =>
        Unit(
            DocumentKind,
            "Support ticket",
            [
                Keyword(ByTicketNumber, TicketNumberPath, isUnique: true),
                Keyword(ByCustomer, CustomerIdPath),
                Keyword(ByStatus, StatusPath),
                Keyword(ByAssignee, AssigneeIdPath),
                Keyword(ByPriority, PriorityPath)
            ],
            [
                PointLookup(FindByTicketNumber, ByTicketNumber),
                List(ListByCustomer, ByCustomer),
                List(ListByStatus, ByStatus),
                List(ListByAssignee, ByAssignee),
                List(ListByPriority, ByPriority)
            ]);

    private static StorageUnit CommentUnit() =>
        Unit(
            CommentDocumentKind,
            "Support ticket comment",
            [
                Keyword(ByCommentTicket, TicketNumberPath),
                Keyword(ByCommentAuthor, CommentAuthorIdPath)
            ],
            [
                List(ListCommentsByTicket, ByCommentTicket),
                List(ListCommentsByAuthor, ByCommentAuthor)
            ]);

    private static StorageUnit Unit(
        string documentKind,
        string displayName,
        IReadOnlyList<LogicalIndexDeclaration> logicalIndexes,
        IReadOnlyList<BoundedQueryDeclaration> boundedQueries) =>
        StorageUnit.Create(
            new StorageUnitIdentity(documentKind),
            displayName,
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Global,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(EntityTable(documentKind, logicalIndexes)),
                logicalIndexes,
                boundedQueries));

    /// <summary>
    /// Builds the explicit physical-entity-table definition for one storage unit: every keyword
    /// index path becomes a bounded String projected column, and every logical index becomes the
    /// physical index the default policy would synthesize — with the same names, missing-value
    /// behavior, and identity tie-break column — so query identities keep working unchanged.
    /// </summary>
    private static PhysicalTableDefinition EntityTable(
        string documentKind,
        IReadOnlyList<LogicalIndexDeclaration> logicalIndexes) =>
        PhysicalTableDefinition.PhysicalEntityTable(
            documentKind,
            logicalIndexes
                .SelectMany(index => index.Fields)
                .Select(field => field.Path)
                .Distinct(StringComparer.Ordinal)
                .Select(path => new ProjectedColumnDefinition(
                    path,
                    path,
                    PortablePhysicalType.String,
                    Length: KeywordColumnLength))
                .ToArray(),
            Envelope,
            logicalIndexes.Select(PhysicalIndex).ToArray());

    private static PhysicalIndexDefinition PhysicalIndex(LogicalIndexDeclaration index)
    {
        var columns = new List<PhysicalIndexColumnDefinition>
        {
            new(index.Fields.Single().Path, 0)
        };

        // Non-unique indexes back offset-paged list queries, which require the provider-applied
        // document-identity tie-break as the trailing key column. The unique index backs only an
        // unpaged point lookup and needs no tie-break.
        if (!index.IsUnique)
            columns.Add(new PhysicalIndexColumnDefinition(Envelope.IdComparisonKeyColumn, 1));

        return new PhysicalIndexDefinition(
            index.Identity,
            columns,
            index.IsUnique,
            missingValueBehavior: index.MissingValueBehavior);
    }

    private static LogicalIndexDeclaration Keyword(string identity, string path, bool isUnique = false) =>
        new(
            identity,
            [new IndexField(path)],
            IndexValueKind.Keyword,
            isUnique,
            MissingValueBehavior.Excluded,
            length: KeywordKeyLength);

    private static readonly IReadOnlySet<PortableQueryOperation> EqualOnly =
        new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal };

    private static BoundedQueryDeclaration PointLookup(string identity, string indexIdentity) =>
        new(
            identity,
            indexIdentity,
            EqualOnly,
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing);

    private static BoundedQueryDeclaration List(string identity, string indexIdentity) =>
        new(
            identity,
            indexIdentity,
            EqualOnly,
            QuerySortSupport.Ascending,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true);
}
