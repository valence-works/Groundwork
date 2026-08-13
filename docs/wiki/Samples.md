# Samples

The repository ships two consumer-facing samples: a complete application
(**SupportTickets**) and an external capability module (**Inbox**). Together they demonstrate the
whole consumption surface this wiki documents.

## SupportTickets: a provider-configurable application

[`samples/Groundwork.SupportTickets`](https://github.com/valence-works/Groundwork/tree/main/samples/Groundwork.SupportTickets)
demonstrates a small support-ticket domain as an ASP.NET Core API with a React/Vite client. The
same manifest runs against SQLite, PostgreSQL, SQL Server, or MongoDB.

The sample:

- declares `supportTicket` and `supportTicketComment` storage units in
  [`SupportTicketManifest.cs`](https://github.com/valence-works/Groundwork/blob/main/samples/Groundwork.SupportTickets/SupportTicketManifest.cs)
  — logical indexes with declared 128-unit key lengths, bounded query declarations, and explicit
  physical-entity-table definitions with bounded projected columns;
- opens the selected provider through its `OpenPhysicalAsync` factory with safe startup
  auto-apply, resolving each unit's executable route from the store's `Routes` (see
  [`SupportTicketSampleHost.cs`](https://github.com/valence-works/Groundwork/blob/main/samples/Groundwork.SupportTickets/SupportTicketSampleHost.cs));
- creates and loads tickets and comments through `IDocumentStore`, and executes every read as a
  declared `DocumentQuery` through `IBoundedDocumentStore` — one bounded-query identity per
  repository operation, nothing queries outside them;
- updates tickets with optimistic concurrency, including version-gated comment writes;
- wires the external Inbox module (below) alongside the ticket store and reports its capability
  fit;
- serves the built React workspace from `wwwroot`.

### Run it

```bash
Groundwork__Provider=Sqlite \
Groundwork__ConnectionString="Data Source=support-tickets.db" \
dotnet run --project samples/Groundwork.SupportTickets/Groundwork.SupportTickets.csproj
```

then browse to the reported address (http://localhost:5000 by default). The sample also accepts
`PostgreSql`, `SqlServer`, and `MongoDb` as `Groundwork__Provider` values when the matching
connection string is supplied; for MongoDB, set `Groundwork__DatabaseName` to override the default
`groundwork_support_tickets` database name.

For client development, run the API and the Vite dev server separately:

```bash
GROUNDWORK_SUPPORT_TICKETS_API_URL=http://localhost:5000 \
npm --prefix samples/Groundwork.SupportTickets/Client run dev
```

### What to read it for

- A realistic manifest with explicit `PhysicalStoragePolicy.Explicit` entity tables that match
  what the default policy would synthesize — useful when you need to see both styles
  ([[Declaring-Storage]]).
- A configuration-driven provider switch over all four factories, including SQLite's
  connection-taking overload for in-memory databases and MongoDB's open handle
  ([[Opening-Stores]]).
- The 128-unit keyword key-length sizing that keeps SQL Server's widest index key inside its
  1700-byte budget ([[Provider-SQL-Server]]).

## Inbox: an external capability module

[`samples/Groundwork.Modules.Inbox`](https://github.com/valence-works/Groundwork/tree/main/samples/Groundwork.Modules.Inbox)
shows Groundwork's **open/closed capability system** from the consumer side: it contributes a
brand-new persistence semantic — an idempotent inbox / exactly-once consumer — entirely from
outside `Groundwork.Core`.

| Layer | Project | Contents |
|---|---|---|
| Capability + contract | `Groundwork.Modules.Inbox` | `InboxCapabilities.IdempotentConsumer` (`community.inbox.idempotent-consumer`), `InboxModule : IGroundworkModule`, `IInboxStore`, schema DDL. References only `Groundwork.Core`. |
| Provider impl | `Groundwork.Modules.Inbox.Sqlite` | `SqliteInboxStore` on the reusable `Groundwork.Provider.Relational` toolkit; advertises the capability. |
| Proof | `Groundwork.Modules.Inbox.Tests` | Dedup behaviour + capability-fit derivation. |

The contract:

```csharp
public interface IInboxStore
{
    Task<InboxAdmission> TryAdmitAsync(string consumer, string messageKey, CancellationToken ct = default);
    Task MarkProcessedAsync(string consumer, string messageKey, CancellationToken ct = default);
    Task<bool> IsProcessedAsync(string consumer, string messageKey, CancellationToken ct = default);
}
// InboxAdmission = Admitted | Duplicate
```

`TryAdmitAsync` returns `Admitted` the first time a `(consumer, messageKey)` pair is seen and
`Duplicate` on every redelivery — implemented with `INSERT ... ON CONFLICT DO NOTHING`.

Wiring the module:

```csharp
var (registry, evidencePolicy) = new GroundworkModuleCatalog()
    .Add(new InboxModule())
    .Build();

var validator = new ProviderCapabilityValidator(registry);
ProviderFit fit = validator.Evaluate(manifest, providerReport, evidencePolicy);
```

A provider advertises support with
`report.WithCapabilities(InboxCapabilities.IdempotentConsumer)`; a manifest unit declares the need
with `StorageIntent.Operational(rationale, descriptor, InboxCapabilities.IdempotentConsumer)`.
A core-only validator (default registry) rejects the unknown capability with `GW-CAP-014` — the
registry, not a hardcoded enum, is the source of truth.

This is the intended pattern whenever your application needs a persistence semantic Groundwork's
document contract cannot honestly serve: contribute the capability and contract in your own
module, implement it per provider on the reusable toolkit, and let the standard validator derive
fit. See the
[module README](https://github.com/valence-works/Groundwork/blob/main/samples/Groundwork.Modules.Inbox/README.md)
and [[Providers]].

## Test suites

Both samples have test projects
([`samples/Groundwork.SupportTickets.Tests`](https://github.com/valence-works/Groundwork/tree/main/samples/Groundwork.SupportTickets.Tests),
[`samples/Groundwork.Modules.Inbox.Tests`](https://github.com/valence-works/Groundwork/tree/main/samples/Groundwork.Modules.Inbox.Tests))
that double as executable documentation for the patterns above.
