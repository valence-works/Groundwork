using Groundwork.Core.Capabilities;
using Groundwork.Modules.Inbox;
using Groundwork.SupportTickets;
using Groundwork.SupportTickets.ExternalModules;

internal static class SupportTicketEndpoints
{
    public static WebApplication MapSupportTicketEndpoints(this WebApplication app, SupportTicketStorageOptions storageOptions)
    {
        app.MapGet("/healthz", () => Results.Ok(new
        {
            status = "ok",
            provider = storageOptions.Provider.ToString(),
            storage = "physical-routes",
            storageUnits = new[] { SupportTicketManifest.DocumentKind, SupportTicketManifest.CommentDocumentKind }
        }));

        app.MapPost("/tickets", async (CreateTicketRequest request, SupportTicketRepository tickets, CancellationToken cancellationToken) =>
        {
            try
            {
                var opened = await tickets.CreateAsync(request.ToTicket(), cancellationToken);
                return Results.Created($"/tickets/{UrlSegment(opened.Ticket.TicketNumber)}", ToTicketResponse(opened));
            }
            catch (SupportTicketConflictException exception)
            {
                return Conflict(exception);
            }
        });

        app.MapGet("/tickets/{ticketNumber}", async (string ticketNumber, SupportTicketRepository tickets, CancellationToken cancellationToken) =>
        {
            var ticket = await tickets.LoadAsync(ticketNumber, cancellationToken);
            return ticket is null ? Results.NotFound() : Results.Ok(ToTicketResponse(ticket));
        });

        app.MapGet("/tickets", async (
            string? ticketNumber,
            string? customerId,
            string? status,
            string? assigneeId,
            string? priority,
            SupportTicketRepository tickets,
            CancellationToken cancellationToken) =>
        {
            if (!string.IsNullOrWhiteSpace(ticketNumber))
            {
                var ticket = await tickets.FindByTicketNumberAsync(ticketNumber, cancellationToken);
                return ticket is null ? Results.Ok(Array.Empty<SupportTicketResponse>()) : Results.Ok(new[] { ToTicketResponse(ticket) });
            }

            var results = (customerId, status, assigneeId, priority) switch
            {
                ({ Length: > 0 }, _, _, _) => await tickets.ListByCustomerAsync(customerId, cancellationToken),
                (_, { Length: > 0 }, _, _) => await tickets.ListByStatusAsync(status, cancellationToken),
                (_, _, { Length: > 0 }, _) => await tickets.ListByAssigneeAsync(assigneeId, cancellationToken),
                (_, _, _, { Length: > 0 }) => await tickets.ListByPriorityAsync(priority, cancellationToken),
                _ => []
            };

            return Results.Ok(results.Select(ToTicketResponse));
        });

        app.MapPost("/tickets/{ticketNumber}/assign", async (
            string ticketNumber,
            AssignTicketRequest request,
            SupportTicketRepository tickets,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var assigned = await tickets.AssignAsync(ticketNumber, request.AssigneeId, request.ExpectedVersion, cancellationToken);
                return Results.Ok(ToTicketResponse(assigned));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (SupportTicketConflictException exception)
            {
                return Conflict(exception);
            }
        });

        app.MapPost("/tickets/{ticketNumber}/escalate", async (
            string ticketNumber,
            VersionedTicketRequest request,
            SupportTicketRepository tickets,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var escalated = await tickets.EscalateAsync(ticketNumber, request.ExpectedVersion, DateTimeOffset.UtcNow, cancellationToken);
                return Results.Ok(ToTicketResponse(escalated));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (SupportTicketConflictException exception)
            {
                return Conflict(exception);
            }
        });

        app.MapPost("/tickets/{ticketNumber}/resolve", async (
            string ticketNumber,
            VersionedTicketRequest request,
            SupportTicketRepository tickets,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var resolved = await tickets.ResolveAsync(ticketNumber, request.ExpectedVersion, DateTimeOffset.UtcNow, cancellationToken);
                return Results.Ok(ToTicketResponse(resolved));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (SupportTicketConflictException exception)
            {
                return Conflict(exception);
            }
        });

        app.MapPost("/tickets/{ticketNumber}/comments", async (
            string ticketNumber,
            AddCommentRequest request,
            SupportTicketRepository tickets,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var comment = await tickets.AddCommentAsync(ticketNumber, request.AuthorId, request.Body, request.ExpectedTicketVersion, null, cancellationToken);
                return Results.Created($"/tickets/{UrlSegment(ticketNumber)}/comments/{UrlSegment(comment.Comment.CommentId)}", ToCommentResponse(comment));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (SupportTicketConflictException exception)
            {
                return Conflict(exception);
            }
        });

        app.MapGet("/tickets/{ticketNumber}/comments", async (string ticketNumber, SupportTicketRepository tickets, CancellationToken cancellationToken) =>
        {
            var comments = await tickets.ListCommentsAsync(ticketNumber, cancellationToken);
            return Results.Ok(comments.Select(ToCommentResponse));
        });

        // ---- External module capability extension ----------------------------------------------------

        // Open/closed capability proof: the Inbox module contributes a custom capability that the host
        // registers and validates without changing Groundwork core.
        app.MapGet("/modules/inbox/fit", (ExternalModuleFitReport fit) => Results.Ok(new
        {
            fit.ModuleName,
            Capability = fit.Capability.ToString(),
            moduleProvider = DescribeFit(fit.ModuleProvider),
            documentOnlyProvider = DescribeFit(fit.DocumentOnlyProvider),
            fit.CoreOnlyValidationErrors
        }));

        // Idempotent inbox: the same (consumer, message-key) is admitted once and then reported duplicate.
        app.MapPost("/modules/inbox/admit", async (AdmitInboxMessageRequest request, IInboxStore inbox, CancellationToken cancellationToken) =>
        {
            var admission = await inbox.TryAdmitAsync(request.Consumer, request.MessageKey, cancellationToken);
            return Results.Ok(new AdmitInboxMessageResponse(request.Consumer, request.MessageKey, admission.ToString()));
        });

        app.MapFallbackToFile("index.html");

        return app;
    }

    private static IResult Conflict(Exception exception) => Results.Conflict(new { error = exception.Message });

    private static object DescribeFit(ProviderFit fit) => fit switch
    {
        ProviderFit.Supported => new { verdict = "Supported", detail = (object?)null },
        ProviderFit.RequiresEvidence requiresEvidence => new { verdict = "RequiresEvidence", detail = (object?)requiresEvidence.Reasons },
        ProviderFit.Unsupported unsupported => new { verdict = "Unsupported", detail = (object?)unsupported.MissingRequirements.Select(requirement => requirement.ToString()) },
        _ => new { verdict = "Unknown", detail = (object?)null }
    };

    private static string UrlSegment(string value) => Uri.EscapeDataString(value);

    private static SupportTicketResponse ToTicketResponse(SupportTicketDocument document) =>
        new(document.Ticket, document.Version);

    private static SupportTicketCommentResponse ToCommentResponse(SupportTicketCommentDocument document) =>
        new(document.Comment, document.Version);
}

public sealed record CreateTicketRequest(
    string TicketNumber,
    string CustomerId,
    string Subject,
    string Description,
    string Priority,
    DateTimeOffset? SlaDueAt = null)
{
    public SupportTicket ToTicket() =>
        new(
            TicketNumber,
            CustomerId,
            Subject,
            Description,
            "open",
            Priority,
            "triage",
            DateTimeOffset.UtcNow,
            SlaDueAt: SlaDueAt);
}

public sealed record AssignTicketRequest(string AssigneeId, long ExpectedVersion);

public sealed record VersionedTicketRequest(long ExpectedVersion);

public sealed record AddCommentRequest(string AuthorId, string Body, long ExpectedTicketVersion);

public sealed record AdmitInboxMessageRequest(string Consumer, string MessageKey);

public sealed record AdmitInboxMessageResponse(string Consumer, string MessageKey, string Admission);

public sealed record SupportTicketResponse(SupportTicket Ticket, long Version);

public sealed record SupportTicketCommentResponse(SupportTicketComment Comment, long Version);
