# Getting started

This page takes you from an empty project to a working document store on SQLite: declare a
manifest, open a store, save a document, and run a declared query. Everything here transfers
unchanged to PostgreSQL, SQL Server, and MongoDB — only the factory call differs (see
[[Opening-Stores]]).

## 1. Reference the provider package

```bash
dotnet add package Groundwork.Sqlite
```

The provider package brings `Groundwork.Core` and `Groundwork.Documents` transitively. You need
the .NET SDK 10.0 or newer.

## 2. Declare a manifest

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

Every read your application will ever perform is one of those `BoundedQueryDeclaration` entries.
There is nothing else to query with — that is the point. [[Declaring-Storage]] explains every
policy and declaration in detail.

## 3. Open a physical document store

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

## 4. Save and load a document

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
```

An update with a stale `ExpectedVersion` returns `DocumentStoreWriteStatus.ConcurrencyConflict`
instead of overwriting; deletes take the same guard. See [[Transactions-and-Unit-of-Work]] for
multi-document all-or-nothing writes.

## 5. Run a declared query

`IBoundedDocumentStore` accepts a `DocumentQuery` bound to one of your declared bounded-query
identities:

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

Query planning validates every shape against the declaration and the provider's handlers at
startup; unsupported server-side shapes fail compilation rather than falling back to an unbounded
in-memory scan. [[Querying]] covers the full runtime model — richer operators, compound order,
keyset continuations, and latest-per-key selection.

## Where next

- Swap the provider: [[Opening-Stores]] shows the PostgreSQL, SQL Server, and MongoDB factories.
- Understand what you declared: [[Declaring-Storage]].
- Deploy schema from CI/CD instead of startup: [[Schema-Evolution]].
- See it all working together: [[Samples]].
