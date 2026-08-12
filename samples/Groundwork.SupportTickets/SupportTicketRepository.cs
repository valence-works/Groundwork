using System.Text.Json;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;

namespace Groundwork.SupportTickets;

/// <summary>
/// Persists tickets and comments through <see cref="IDocumentStore"/> and executes only the
/// bounded queries declared by <see cref="SupportTicketManifest"/> through <see cref="IBoundedDocumentStore"/>.
/// </summary>
public sealed class SupportTicketRepository(
    IDocumentStore documents,
    IBoundedDocumentStore ticketQueries,
    IBoundedDocumentStore commentQueries)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<SupportTicketDocument> CreateAsync(SupportTicket ticket, CancellationToken cancellationToken = default)
    {
        if (await LoadAsync(ticket.TicketNumber, cancellationToken) is not null)
            throw new SupportTicketConflictException($"Ticket '{ticket.TicketNumber}' already exists.");

        var result = await documents.SaveJsonAsync(
            SupportTicketManifest.DocumentKind,
            ticket.TicketNumber,
            SupportTicketManifest.SchemaVersion,
            ticket,
            SerializerOptions,
            cancellationToken: cancellationToken);

        return ToSavedTicket(result, $"Ticket '{ticket.TicketNumber}' already exists.");
    }

    public async Task<SupportTicketDocument?> LoadAsync(string ticketNumber, CancellationToken cancellationToken = default)
    {
        var envelope = await documents.LoadAsync(SupportTicketManifest.DocumentKind, ticketNumber, cancellationToken);
        return envelope is null ? null : ToTicket(envelope);
    }

    public async Task<SupportTicketDocument?> FindByTicketNumberAsync(string ticketNumber, CancellationToken cancellationToken = default)
    {
        var envelope = await ticketQueries.FirstOrDefaultAsync(
            TicketQuery(SupportTicketManifest.FindByTicketNumber, SupportTicketManifest.TicketNumberPath, ticketNumber)
                .Select(BoundedQueryResultOperation.First),
            cancellationToken);

        return envelope is null ? null : ToTicket(envelope);
    }

    public Task<IReadOnlyList<SupportTicketDocument>> ListByCustomerAsync(string customerId, CancellationToken cancellationToken = default) =>
        QueryTicketsAsync(SupportTicketManifest.ListByCustomer, SupportTicketManifest.CustomerIdPath, customerId, cancellationToken);

    public Task<IReadOnlyList<SupportTicketDocument>> ListByStatusAsync(string status, CancellationToken cancellationToken = default) =>
        QueryTicketsAsync(SupportTicketManifest.ListByStatus, SupportTicketManifest.StatusPath, status, cancellationToken);

    public Task<IReadOnlyList<SupportTicketDocument>> ListByAssigneeAsync(string assigneeId, CancellationToken cancellationToken = default) =>
        QueryTicketsAsync(SupportTicketManifest.ListByAssignee, SupportTicketManifest.AssigneeIdPath, assigneeId, cancellationToken);

    public Task<IReadOnlyList<SupportTicketDocument>> ListByPriorityAsync(string priority, CancellationToken cancellationToken = default) =>
        QueryTicketsAsync(SupportTicketManifest.ListByPriority, SupportTicketManifest.PriorityPath, priority, cancellationToken);

    public async Task<IReadOnlyList<SupportTicketCommentDocument>> ListCommentsAsync(string ticketNumber, CancellationToken cancellationToken = default)
    {
        var page = await commentQueries.QueryAsync(
            EqualityQuery(
                SupportTicketManifest.CommentDocumentKind,
                SupportTicketManifest.ListCommentsByTicket,
                SupportTicketManifest.TicketNumberPath,
                ticketNumber),
            cancellationToken);

        return page.Documents
            .Select(ToComment)
            .OrderBy(comment => comment.Comment.CreatedAt)
            .ThenBy(comment => comment.Comment.CommentId, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<SupportTicketDocument> AssignAsync(
        string ticketNumber,
        string assigneeId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var existing = await RequireAsync(ticketNumber, cancellationToken);
        var updated = existing.Ticket with { AssigneeId = assigneeId, Status = "assigned" };
        return await SaveExistingAsync(updated, expectedVersion, cancellationToken);
    }

    public async Task<SupportTicketDocument> EscalateAsync(
        string ticketNumber,
        long expectedVersion,
        DateTimeOffset escalatedAt,
        CancellationToken cancellationToken = default)
    {
        var existing = await RequireAsync(ticketNumber, cancellationToken);
        var updated = existing.Ticket with { Status = "escalated", EscalatedAt = escalatedAt };
        return await SaveExistingAsync(updated, expectedVersion, cancellationToken);
    }

    public async Task<SupportTicketDocument> ResolveAsync(
        string ticketNumber,
        long expectedVersion,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default)
    {
        var existing = await RequireAsync(ticketNumber, cancellationToken);
        var updated = existing.Ticket with { Status = "resolved", ResolvedAt = resolvedAt };
        return await SaveExistingAsync(updated, expectedVersion, cancellationToken);
    }

    public async Task<SupportTicketCommentDocument> AddCommentAsync(
        string ticketNumber,
        string authorId,
        string body,
        long expectedTicketVersion,
        DateTimeOffset? createdAt = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await RequireAsync(ticketNumber, cancellationToken);
        await SaveExistingAsync(
            existing.Ticket,
            expectedTicketVersion,
            cancellationToken,
            $"Ticket '{ticketNumber}' changed before the comment could be saved.");

        var comment = new SupportTicketComment(
            $"comment-{Guid.NewGuid():N}",
            ticketNumber,
            authorId,
            body,
            createdAt ?? DateTimeOffset.UtcNow);
        var result = await documents.SaveJsonAsync(
            SupportTicketManifest.CommentDocumentKind,
            comment.CommentId,
            SupportTicketManifest.SchemaVersion,
            comment,
            SerializerOptions,
            cancellationToken: cancellationToken);

        return ToSavedComment(result, $"Comment '{comment.CommentId}' already exists.");
    }

    private async Task<IReadOnlyList<SupportTicketDocument>> QueryTicketsAsync(
        string queryIdentity,
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        var page = await ticketQueries.QueryAsync(TicketQuery(queryIdentity, path, value), cancellationToken);
        return page.Documents.Select(ToTicket).ToList();
    }

    private static DocumentQuery TicketQuery(string queryIdentity, string path, string value) =>
        EqualityQuery(SupportTicketManifest.DocumentKind, queryIdentity, path, value);

    private static DocumentQuery EqualityQuery(string documentKind, string queryIdentity, string path, string value) =>
        new(
            documentKind,
            queryIdentity,
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal(path, value))]);

    private async Task<SupportTicketDocument> RequireAsync(string ticketNumber, CancellationToken cancellationToken)
    {
        var ticket = await LoadAsync(ticketNumber, cancellationToken);
        return ticket ?? throw new KeyNotFoundException($"Ticket '{ticketNumber}' was not found.");
    }

    private async Task<SupportTicketDocument> SaveExistingAsync(
        SupportTicket ticket,
        long expectedVersion,
        CancellationToken cancellationToken,
        string? conflictMessage = null)
    {
        var result = await documents.SaveJsonAsync(
            SupportTicketManifest.DocumentKind,
            ticket.TicketNumber,
            SupportTicketManifest.SchemaVersion,
            ticket,
            SerializerOptions,
            expectedVersion,
            cancellationToken);

        return ToSavedTicket(result, conflictMessage ?? $"Ticket '{ticket.TicketNumber}' changed before the update could be saved.");
    }

    private static SupportTicketDocument ToSavedTicket(DocumentStoreWriteResult result, string conflictMessage) =>
        result.Status switch
        {
            DocumentStoreWriteStatus.Saved => ToTicket(result.Document!),
            DocumentStoreWriteStatus.ConcurrencyConflict => throw new SupportTicketConflictException(conflictMessage),
            DocumentStoreWriteStatus.NotFound => throw new KeyNotFoundException("Ticket was not found."),
            _ => throw new InvalidOperationException($"Unexpected write status '{result.Status}'.")
        };

    private static SupportTicketCommentDocument ToSavedComment(DocumentStoreWriteResult result, string conflictMessage) =>
        result.Status switch
        {
            DocumentStoreWriteStatus.Saved => ToComment(result.Document!),
            DocumentStoreWriteStatus.ConcurrencyConflict => throw new SupportTicketConflictException(conflictMessage),
            DocumentStoreWriteStatus.NotFound => throw new KeyNotFoundException("Comment was not found."),
            _ => throw new InvalidOperationException($"Unexpected write status '{result.Status}'.")
        };

    private static SupportTicketDocument ToTicket(DocumentEnvelope envelope) =>
        new(envelope.DeserializeJson<SupportTicket>(SerializerOptions), envelope.Version);

    private static SupportTicketCommentDocument ToComment(DocumentEnvelope envelope) =>
        new(envelope.DeserializeJson<SupportTicketComment>(SerializerOptions), envelope.Version);
}
