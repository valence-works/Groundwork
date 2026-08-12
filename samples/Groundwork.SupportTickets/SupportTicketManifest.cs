using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;

namespace Groundwork.SupportTickets;

/// <summary>
/// Declares the support-ticket storage units through the current physical-storage surface:
/// logical indexes, bounded query declarations, and default physical-form resolution. Every
/// repository query executes one of the bounded declarations below; nothing queries outside them.
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
                PhysicalStoragePolicy.Default(),
                logicalIndexes,
                boundedQueries));

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
