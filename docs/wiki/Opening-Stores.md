# Opening stores

Applications open document stores through each provider's `OpenPhysicalAsync` factory. The factory
admits the manifest against the live database schema, compiles executable storage routes, and
returns a store that can only serve what was declared. This page covers the factory shapes, schema
admission, and how bounded-query stores are created from `store.Routes`.

> The older `CreateAsync` factories belong to the retired portable model and carry `GW0005`
> obsolete warnings. Always open stores with `OpenPhysicalAsync`.

## What every factory needs

- the **connection** (connection string, or a provider connection object where supported);
- the **`StorageManifest`** (see [[Declaring-Storage]]);
- a **`ProviderIdentity`** — your application's name/version pair for this provider, recorded in
  schema history and fingerprints, e.g. `new ProviderIdentity("groundwork-sqlite", "1.0.0")`;
- a **`DocumentStoreAccess`** — `DocumentStoreAccess.Global` for global units, or
  `DocumentStoreAccess.Scoped(new StorageScope("tenant-a"))` for scoped units. This is mandatory;
  there is no ambient default. See [[Storage-Scopes]];
- optional **schema admission options** (below).

## Schema admission and `AutoApplyOnStartup`

Runtime schema admission is **inspect-only by default**: the factory verifies that the database
already matches the compiled routes and fails loudly when required schema is missing or drifted.
Applications may explicitly opt into safe-only startup application:

```csharp
options: new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true }
```

`AutoApplyOnStartup` applies *pending additive* work only — creating tables, columns, indexes, and
running their canonical-JSON backfills. Protected destructive or semantic-migration work always
requires explicit operator authorization through the `dotnet groundwork` CLI; there is no
automatic startup fallback for it. The SQLite, PostgreSQL, and SQL Server factories accept the
common `GroundworkRuntimeSchemaAdmissionOptions` type; MongoDB exposes the same boolean on
`MongoDbPhysicalDocumentStoreOptions`. For production pipelines, prefer deploying schema
explicitly with the CLI and leaving startup inspect-only — see [[Schema-Evolution]].

## SQLite

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
    store.Routes.Single(route => route.StorageUnit.Value == "supportTicket"),
    provider);
```

The connection-string factory selects SQLite's serialized session policy and rejects *private*
in-memory databases (each pooled connection would see a different database). For in-memory or
test scenarios, open a `SqliteConnection` yourself and pass it to the connection-taking
`OpenPhysicalAsync` overload — schema and identity admission still run. See [[Provider-SQLite]].

## PostgreSQL and SQL Server

The PostgreSQL and SQL Server factories follow the same shape — `OpenPhysicalAsync` plus a
`PostgreSqlPhysicalQueryRuntime`/`SqlServerPhysicalQueryRuntime` bound to a route from `Routes`:

```csharp
using Groundwork.PostgreSql.Documents;   // or Groundwork.SqlServer.Documents

var store = await PostgreSqlDocumentStoreFactory.OpenPhysicalAsync(
    connectionString, manifest, provider, DocumentStoreAccess.Global,
    options: new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true });

IBoundedDocumentStore boundedStore = PostgreSqlPhysicalQueryRuntime.Create(
    store, manifest,
    store.Routes.Single(route => route.StorageUnit.Value == "supportTicket"),
    provider);
```

Both use pooled per-operation connections — there is no retained shared connection or singleton
semaphore. See [[Provider-PostgreSQL]] and [[Provider-SQL-Server]].

## MongoDB: the open handle

MongoDB takes the same manifest. Its `OpenPhysicalAsync` returns an **open handle that owns the
client**, and the store itself is also the bounded-query store for every unit — no per-route query
runtime is needed:

```csharp
using Groundwork.Core.Capabilities;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.MongoDb.Documents;

await using var handle = await MongoDbDocumentStoreFactory.OpenPhysicalAsync(
    "mongodb://localhost:27017",
    "support",                       // database name
    manifest,
    new ProviderIdentity("groundwork-mongodb", "1.0.0"),
    DocumentStoreAccess.Global,
    options: new MongoDbPhysicalDocumentStoreOptions { AutoApplyOnStartup = true });

IDocumentStore store = handle.Store;                // CRUD and unit-of-work
IBoundedDocumentStore boundedStore = handle.Store;  // declared bounded queries
```

Keep the handle alive for the store's lifetime and dispose it (`await using`) when done — the
handle owns the underlying `MongoClient`. MongoDB checks its transaction topology (replica set or
sharded cluster) before side effects; see [[Provider-MongoDB]].

## `store.Routes`: executable storage routes

`store.Routes` exposes one immutable `ExecutableStorageRoute` per storage unit — the compiled
provider-neutral mapping that fixes the physical form, resolved provider names, envelope and
projected fields, physical indexes, scope policy, maintenance targets, candidate bounded-query
paths, and fingerprints. Relational bounded-query stores are created per route:

```csharp
static ExecutableStorageRoute Route(IReadOnlyList<ExecutableStorageRoute> routes, string kind) =>
    routes.Single(route => route.StorageUnit.Value == kind);

var ticketQueries  = SqlitePhysicalQueryRuntime.Create(store, manifest, Route(store.Routes, "supportTicket"), provider);
var commentQueries = SqlitePhysicalQueryRuntime.Create(store, manifest, Route(store.Routes, "supportTicketComment"), provider);
```

Query-runtime construction is where certification happens: every declared bounded query is
compiled against the route and the provider's executable handlers, and construction fails — before
any traffic — if a declaration cannot be served server-side. See
[bounded physical query plans](https://github.com/valence-works/Groundwork/blob/main/docs/bounded-physical-query-plans.md).

## Choosing a provider at runtime

The [SupportTickets sample host](https://github.com/valence-works/Groundwork/blob/main/samples/Groundwork.SupportTickets/SupportTicketSampleHost.cs)
shows one configuration-driven switch over all four providers with the same manifest — a useful
template for provider-configurable applications. See [[Samples]].

## See also

- [[Querying]] — using the bounded store you just created.
- [[Transactions-and-Unit-of-Work]] — `BeginAsync` and commit scopes.
- [[Schema-Evolution]] — deploying schema outside application startup.
