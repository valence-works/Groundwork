# Groundwork

![Groundwork banner](docs/assets/groundwork-banner.png)

> [!IMPORTANT]
> **Groundwork v1 is frozen.** The frozen source is tagged
> [`v1-frozen`](https://github.com/valence-works/groundwork/tree/v1-frozen), and existing consumers
> should remain pinned to their current v1 preview while v2 is developed. V1 receives no features
> or routine bug fixes; only critical security fixes are eligible for narrowly scoped maintenance.
> See the [Groundwork v2 program](docs/v2/program.md) for the replacement direction and delivery
> plan.

Groundwork is a provider-neutral persistence foundation for .NET applications. Modules describe
storage intent through manifests — storage units, logical indexes, and one bounded query
declaration per read the application performs — and providers (SQLite, PostgreSQL, SQL Server,
MongoDB) translate those manifests into concrete database structures. Applications open stores
through each provider's `OpenPhysicalAsync` factory and execute exactly the declared queries;
there is no unbounded query surface and no in-memory fallback.

This repository contains the standalone Groundwork library. An earlier portable document model
existed and has been removed; see [ADR 0006](docs/adr/0006-retire-the-portable-document-model.md).

## Samples

### Support tickets

[`samples/Groundwork.SupportTickets`](samples/Groundwork.SupportTickets) demonstrates a small
support-ticket domain as an ASP.NET Core API with a React/Vite client. The same manifest runs
against SQLite, PostgreSQL, SQL Server, or MongoDB.

The sample:

- declares `supportTicket` and `supportTicketComment` storage units in
  [`SupportTicketManifest.cs`](samples/Groundwork.SupportTickets/SupportTicketManifest.cs) —
  logical indexes with declared 128-unit key lengths, bounded query declarations, and explicit
  physical-entity-table definitions with bounded projected columns;
- opens the selected provider through its `OpenPhysicalAsync` factory with safe startup
  auto-apply, resolving each unit's executable route from the store's `Routes`;
- creates and loads tickets and comments through `IDocumentStore`, and executes every read as a
  declared `DocumentQuery` through `IBoundedDocumentStore`;
- updates tickets with optimistic concurrency, including version-gated comment writes;
- wires the external Inbox module (below) alongside the ticket store and reports its
  capability fit;
- serves the built React workspace from `wwwroot`.

Run it with:

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

### Inbox: an external capability module

[`samples/Groundwork.Modules.Inbox`](samples/Groundwork.Modules.Inbox) shows Groundwork's
open/closed capability system from the consumer side: it contributes a brand-new persistence
semantic — an idempotent inbox / exactly-once consumer — entirely from outside `Groundwork.Core`.
The module declares a custom `CapabilityId`, a provider implements and advertises it, and the
standard `ProviderCapabilityValidator` derives provider fit for it exactly as for built-in
capabilities. See the [module README](samples/Groundwork.Modules.Inbox/README.md).

## Requirements

- .NET SDK 10.0 or newer.
- Node.js and npm when rebuilding the support-ticket React client.
- Docker for provider tests that use container-backed databases.

## Packages

Reference the provider package for your database; it brings the core contracts transitively:

- `Groundwork.Sqlite`, `Groundwork.PostgreSql`, `Groundwork.SqlServer` — relational providers.
- `Groundwork.MongoDb` — MongoDB provider.
- `Groundwork.Tool` — a `dotnet` tool (`dotnet groundwork`) for explicit deployment-time schema
  validation, planning, status, and application in CI/CD. Pin it to the same Groundwork release as
  the application's packages. See [the schema-tool guide](docs/schema-tool.md).

## Use Groundwork

### Declare a manifest

Groundwork starts with a provider-neutral `StorageManifest`. The manifest below declares a
support-ticket unit with string IDs, JSON content, optimistic concurrency, keyword logical
indexes, and one `BoundedQueryDeclaration` per read the application performs. The default
physical-storage policy synthesizes the projected columns and physical indexes those declarations
demand; the declared `length` bounds each string key so providers with sized index keys
(SQL Server) can materialize them:

```csharp
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;

const string DocumentKind = "supportTicket";
const string SchemaVersion = "1.0.0";

var equalOnly = new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal };

var manifest = new StorageManifest(
    new StorageManifestIdentity("support-tickets"),
    new StorageManifestOwner("sample.support"),
    new StorageManifestVersion(SchemaVersion),
    [
        StorageUnit.Create(
            new StorageUnitIdentity(DocumentKind),
            "Support ticket",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Global,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Default(),
                logicalIndexes:
                [
                    new LogicalIndexDeclaration(
                        "by-ticket-number",
                        [new IndexField("ticketNumber")],
                        IndexValueKind.Keyword,
                        isUnique: true,
                        MissingValueBehavior.Excluded,
                        length: 128),
                    new LogicalIndexDeclaration(
                        "by-status",
                        [new IndexField("status")],
                        IndexValueKind.Keyword,
                        isUnique: false,
                        MissingValueBehavior.Excluded,
                        length: 128)
                ],
                boundedQueries:
                [
                    new BoundedQueryDeclaration(
                        "find-by-ticket-number",
                        "by-ticket-number",
                        equalOnly,
                        QuerySortSupport.None,
                        QueryPagingSupport.None,
                        BoundedQueryExecutionClass.ScaleBearing),
                    new BoundedQueryDeclaration(
                        "list-by-status",
                        "by-status",
                        equalOnly,
                        QuerySortSupport.Ascending,
                        QueryPagingSupport.Offset,
                        BoundedQueryExecutionClass.ScaleBearing,
                        supportsTotalCount: true)
                ]))
    ],
    new HashSet<string> { "schema-history", "optimistic-concurrency" },
    []);
```

Marking a bounded query `ScaleBearing` requires the addressed content paths to be served by typed
projected columns with one physical index per referenced logical index. `PhysicalStoragePolicy.Default()`
synthesizes those from the declarations above. When you need to state the physical shape yourself —
exact columns, index key order, identity tie-breaks — use `PhysicalStoragePolicy.Explicit` with a
`PhysicalTableDefinition`; the
[sample manifest](samples/Groundwork.SupportTickets/SupportTicketManifest.cs) declares explicit
physical-entity tables that match what the default policy would synthesize.

Applications that also use immutable diagnostic records compose those stream definitions through
`DiagnosticRecordDeploymentManifest`; streams are not document storage units. The `Groundwork.Tool`
deployment commands plan, inspect, validate, and apply both declarations from one application
source. See [the schema-tool guide](docs/schema-tool.md).

### Open a physical document store

Open SQLite with `OpenPhysicalAsync`. Runtime schema admission is inspect-only by default; opting
into safe startup auto-apply creates the pending additive schema. The bounded-query store is
created per executable route (one route per storage unit), resolved from the store's `Routes`:

```csharp
using Groundwork.Core.Capabilities;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Sqlite.Documents;

var provider = new ProviderIdentity("groundwork-sqlite", "1.0.0");
SqlitePhysicalDocumentStore store = await SqliteDocumentStoreFactory.OpenPhysicalAsync(
    "Data Source=support-tickets.db",
    manifest,
    provider,
    DocumentStoreAccess.Global,
    options: new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true });

IBoundedDocumentStore boundedStore = SqlitePhysicalQueryRuntime.Create(
    store,
    manifest,
    store.Routes.Single(route => route.StorageUnit.Value == DocumentKind),
    provider);
```

The PostgreSQL and SQL Server factories follow the same shape: `OpenPhysicalAsync` plus a
`PostgreSqlPhysicalQueryRuntime`/`SqlServerPhysicalQueryRuntime` bound to a route from `Routes`.

MongoDB takes the same manifest. Its `OpenPhysicalAsync` returns an open handle that owns the
client, and the store itself is also the bounded-query store for every unit:

```csharp
using Groundwork.Core.Capabilities;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.MongoDb.Documents;

await using var handle = await MongoDbDocumentStoreFactory.OpenPhysicalAsync(
    "mongodb://localhost:27017",
    "support",
    manifest,
    new ProviderIdentity("groundwork-mongodb", "1.0.0"),
    DocumentStoreAccess.Global,
    options: new MongoDbPhysicalDocumentStoreOptions { AutoApplyOnStartup = true });

IDocumentStore store = handle.Store;                // CRUD and unit-of-work
IBoundedDocumentStore boundedStore = handle.Store;  // declared bounded queries
```

### Save, load, and delete documents

`IDocumentStore` stores JSON envelopes, so any CLR type works as long as it serializes to the
field names the manifest indexes declare. Optimistic concurrency is expressed with
`ExpectedVersion`:

```csharp
using System.Text.Json;
using Groundwork.Documents.Store;

var ticket = new
{
    ticketNumber = "TCK-1001",
    customerId = "acme",
    subject = "Invoice export fails",
    status = "open",
    openedAt = DateTimeOffset.UtcNow
};

var created = await store.SaveAsync(new SaveDocumentRequest(
    DocumentKind,
    ticket.ticketNumber,
    SchemaVersion,
    JsonSerializer.Serialize(ticket)));

if (created.Status != DocumentStoreWriteStatus.Saved)
    throw new InvalidOperationException($"Ticket was not saved: {created.Status}");

var loaded = await store.LoadAsync(DocumentKind, ticket.ticketNumber);

var updated = await store.SaveAsync(new SaveDocumentRequest(
    DocumentKind,
    "TCK-1001",
    SchemaVersion,
    """{ "ticketNumber": "TCK-1001", "customerId": "acme", "subject": "Invoice export fails", "status": "assigned", "openedAt": "2026-06-12T08:00:00Z" }""",
    ExpectedVersion: created.Document!.Version));

if (updated.Status == DocumentStoreWriteStatus.ConcurrencyConflict)
    throw new InvalidOperationException("Ticket changed before the assignment was saved.");

var deleted = await store.DeleteAsync(new DeleteDocumentRequest(
    DocumentKind,
    "TCK-1001",
    ExpectedVersion: updated.Document!.Version));
```

### Bounded document queries

`IBoundedDocumentStore` accepts one closed `DocumentQuery` runtime model bound to a
`BoundedQueryDeclaration` identity: an `AND` of `OR`-groups of declared comparisons with ordering,
offset or keyset paging, total count, and count/any/first result operations. Query planning
validates every shape against the declaration and the provider's handlers at startup; unsupported
server-side shapes fail compilation rather than falling back to an unbounded in-memory scan.

```csharp
using Groundwork.Documents.Store;

var query = new DocumentQuery(
    DocumentKind,
    "list-by-status",
    [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "open"))],
    take: 25);

DocumentQueryResult page = await boundedStore.QueryAsync(query);
long total = page.TotalCount;

DocumentEnvelope? first = await boundedStore.FirstOrDefaultAsync(
    query.Select(BoundedQueryResultOperation.First));
bool any = await boundedStore.AnyAsync(query.Select(BoundedQueryResultOperation.Any));
```

Richer declarations unlock richer runtime queries — membership (`In`), declared
substring/prefix (`Contains`/`StartsWith`), ranges, compound predicates and compound order,
keyset paging, and latest-per-key selection — always validated against what the declaration and
provider support. Operator semantics match EF Core exactly: `Equal` with a `null` value matches
documents whose field is null/absent; `In` over an empty set matches nothing; `Contains` is
case-insensitive and a null field yields no match (never throws); `DocumentQueryClause.MatchNone`
is a constant-false sentinel; and zero clauses match all documents of the kind. See
[bounded physical query plans](docs/bounded-physical-query-plans.md) for source selection,
compound-prefix rules, and plan diagnostics.

### Multi-document transactions

For write commands that persist several related documents all-or-nothing, an `IDocumentStore` is
also an `IDocumentSessionFactory`: it begins a document unit of work over a declared
`DocumentCommitScope`. Staged `Save`/`Delete` operations become visible only on `CommitAsync`;
`RollbackAsync` (or disposing without committing) discards them.

```csharp
using Groundwork.Core.Transactions;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;

// Detect native cross-document atomicity before committing to a path (no exception needed).
if (store.TransactionBoundary != TransactionBoundary.CrossUnitAtomic)
    /* use a compensation fallback */;

await using var unitOfWork = await store.BeginAsync(
    DocumentCommitScope.Of("workflow-version", "workflow-definition", "layout"));
try
{
    var saved = await unitOfWork.SaveAsync(new SaveDocumentRequest(/* version doc */));
    if (saved.Status != DocumentStoreWriteStatus.Saved)
    {
        await unitOfWork.RollbackAsync();   // all-or-nothing: caller rolls back on any non-success
        return;
    }

    await unitOfWork.SaveAsync(new SaveDocumentRequest(/* updated definition doc */));
    await unitOfWork.DeleteAsync(new DeleteDocumentRequest(/* stale layout record */));

    await unitOfWork.CommitAsync();
}
catch
{
    await unitOfWork.RollbackAsync();
    throw;
}
```

Contract:

- **Boundary detection.** `TransactionBoundary` reports `CrossUnitAtomic` when the store can
  commit multiple documents atomically, or `PerOperation` when it cannot — letting callers choose
  a compensation path without catching an exception.
- **Staging.** `SaveAsync`/`DeleteAsync` run against the open unit of work and return their normal
  `DocumentStoreWriteResult` immediately (including `ConcurrencyConflict`/`NotFound`). They are
  **not** auto-committed; the all-or-nothing guarantee is the caller's: roll back on any
  non-success result or exception. `LoadAsync` inside the unit of work sees staged writes.
- **Relational** (SQLite/PostgreSQL/SQL Server) is `CrossUnitAtomic`, backed by a real
  `DbTransaction`. Some engines (e.g. PostgreSQL) abort the whole transaction on the first failed
  statement, so rollback is the only valid next step after a non-success result.
- **MongoDB** uses a multi-document transaction over a client session, which requires a replica
  set or sharded deployment (reported as `CrossUnitAtomic`). On a standalone deployment the
  boundary is `PerOperation` and `BeginAsync` throws `UnsupportedAtomicCommitException` — a loud
  failure rather than silent non-atomic writes.

### Schema admission and deployment

Runtime schema admission is inspect-only by default. Applications may explicitly opt into
safe-only startup application through `GroundworkRuntimeSchemaAdmissionOptions.AutoApplyOnStartup`
(the SQLite, PostgreSQL, and SQL Server factories accept that common options type; MongoDB exposes
the same boolean on `MongoDbPhysicalDocumentStoreOptions`). Protected destructive or
semantic-migration work always requires explicit operator authorization through the
[Groundwork schema tool](docs/schema-tool.md), which supports stable human/JSON output and
documented pipeline exit codes for CI/CD.

## Concepts

- **Storage intent and capabilities.** Storage intent declares the provider capabilities a unit
  requires; provider fit is computed from those declared requirements, never from author
  self-declaration. `StorageIntent.PortableDocument()` is the default document/table contract;
  `StorageIntent.Operational(rationale, descriptor, requirements)` declares `CapabilityId`
  requirements plus the rationale when correctness depends on more — atomic commit across units,
  concurrency evidence, or custom semantics contributed by external modules such as the
  [Inbox sample](samples/Groundwork.Modules.Inbox/README.md). `ProviderCapabilityValidator`
  compares a manifest against a provider's capability report and returns a `ProviderFit`.
- **Physical storage forms.** Physical intent uses exactly three forms: `SharedDocuments` for
  dynamic/runtime-defined units, `DedicatedDocumentTable` for declared units without scale-bearing
  projected-field demand, and `PhysicalEntityTable` for declared units whose bounded queries mark
  stable non-envelope paths as scale-bearing. All three retain the standard envelope and
  authoritative canonical JSON; projected columns are rebuildable derivatives, never a second
  source of truth. See [ADR 0003](docs/adr/0003-adopt-three-physical-storage-forms.md) and
  [executable storage routes](docs/executable-storage-routes.md).
- **Scoped access.** Document stores require explicit `DocumentStoreAccess`:
  `Scoped(StorageScope)` for scoped units or `Global` for deliberately global units. Scope is
  enforced by the storage boundary and never read from document JSON; cross-scope reads require a
  separately acquired privileged session (`PrivilegedStorageAccess`), and there is no query flag
  that disables isolation. See [storage scope sessions](docs/storage-scope-sessions.md).
- **Diagnostic records.** `Groundwork.DiagnosticRecords` provides bounded
  append/query/inspection/retention contracts for immutable diagnostic streams, deployed alongside
  document units through the same schema tooling.

[`CONTEXT.md`](CONTEXT.md) is the short vocabulary reference; deeper design notes live under
[`docs/`](docs) and the ADRs under [`docs/adr/`](docs/adr).

## Projects

- `Groundwork.Core`: manifests, storage intent, provider capability checks, validation, physical
  storage definitions and executable routes, and provider-neutral schema evolution.
- `Groundwork.Documents`: document-store contracts, bounded queries, and document planning.
- `Groundwork.DiagnosticRecords`: bounded append/query/inspection/retention contracts for
  immutable diagnostic streams.
- `Groundwork.DiagnosticRecords.Relational`: shared relational schema, transactional
  ledger/retention kernel, and bounded SQL query translation for diagnostic streams.
- `Groundwork.Relational`: relational planning and shared relational document-store
  infrastructure.
- `Groundwork.Provider.Relational`: reusable relational provider toolkit (also used by external
  modules).
- `Groundwork.Sqlite`: SQLite materialization plus document-store and diagnostic-record providers.
- `Groundwork.SqlServer`: SQL Server materialization plus document-store and diagnostic-record providers.
- `Groundwork.PostgreSql`: PostgreSQL materialization plus document-store and diagnostic-record providers.
- `Groundwork.MongoDb`: MongoDB materialization, document-store, and transactional
  diagnostic-record provider.
- `Groundwork.SchemaTool`: the `Groundwork.Tool` package — explicit provider-neutral schema
  validation, planning, status, and application for CI/CD.

## Build And Test

```bash
dotnet test tests/Groundwork/Groundwork.Tests/Groundwork.Tests.csproj
dotnet test tests/Groundwork/Groundwork.DiagnosticRecords.Tests/Groundwork.DiagnosticRecords.Tests.csproj
dotnet test tests/Groundwork/Groundwork.Sqlite.Tests/Groundwork.Sqlite.Tests.csproj
dotnet test tests/Groundwork/Groundwork.SchemaTool.Tests/Groundwork.SchemaTool.Tests.csproj
dotnet test tests/Groundwork/Groundwork.SchemaTool.ProviderTests/Groundwork.SchemaTool.ProviderTests.csproj
dotnet test samples/Groundwork.SupportTickets.Tests/Groundwork.SupportTickets.Tests.csproj
dotnet test samples/Groundwork.Modules.Inbox.Tests/Groundwork.Modules.Inbox.Tests.csproj
npm --prefix samples/Groundwork.SupportTickets/Client run build
```

Provider integration suites can be run separately when Docker-backed databases are available:

```bash
dotnet test tests/Groundwork/Groundwork.Differential.Tests/Groundwork.Differential.Tests.csproj
dotnet test tests/Groundwork/Groundwork.MongoDb.Tests/Groundwork.MongoDb.Tests.csproj
dotnet test tests/Groundwork/Groundwork.RelationalProviders.Tests/Groundwork.RelationalProviders.Tests.csproj
```

The four-provider differential suite is the standing portability gate. Its normalize-or-refuse
decisions are recorded in the [draft portable semantics contract](docs/v2/portable-semantics-draft.md).

The physical-storage macrobenchmark scaffolding and its current evidence limits are documented in
[`benchmarks/Groundwork.PhysicalStorage.Benchmarks`](benchmarks/Groundwork.PhysicalStorage.Benchmarks/README.md).

The historical specs and Groundwork-focused planning notes are kept under `specs/` and `docs/`.
