# Groundwork

Groundwork is a provider-neutral persistence foundation for .NET applications. Modules describe
storage intent through **manifests** — storage units, logical indexes, and one bounded query
declaration per read the application performs — and providers (SQLite, PostgreSQL, SQL Server,
MongoDB) translate those manifests into concrete database structures. Applications open stores
through each provider's `OpenPhysicalAsync` factory and execute exactly the declared queries;
there is no unbounded query surface and no in-memory fallback.

This wiki is for developers who **consume** Groundwork from its NuGet packages. It is generated
from the [`docs/wiki/`](https://github.com/valence-works/Groundwork/tree/main/docs/wiki) directory
of the main repository — do not edit pages here directly.

## The model in one paragraph

You declare *what* you store (a `StorageManifest` of storage units) and *how* you will read it
(logical indexes plus one `BoundedQueryDeclaration` per read path). Groundwork resolves that intent
into provider-neutral physical definitions, compiles executable storage routes, and lets each
provider certify — before any traffic — that it can execute every declared query server-side.
Canonical JSON stays authoritative in every physical form; projected columns and indexes are
rebuildable derivatives. Anything you did not declare is not merely slow — it is rejected at
startup, loudly, instead of silently degrading into a client-side scan.

## Where to start

1. [[Getting-Started]] — the 3-minute SQLite path: declare, open, save, query.
2. [[Declaring-Storage]] — manifests, storage units, logical indexes, bounded queries and mutations.
3. [[Opening-Stores]] — per-provider `OpenPhysicalAsync`, schema admission, and `store.Routes`.
4. [[Querying]] — `DocumentQuery`, `IBoundedDocumentStore`, paging, and result operations.

## All pages

**Using Groundwork**

- [[Getting-Started]] — first store in three minutes, on SQLite.
- [[Declaring-Storage]] — the manifest surface: units, indexes, key lengths, bounded queries, bounded mutations.
- [[Opening-Stores]] — factories, schema admission, `AutoApplyOnStartup`, executable routes.
- [[Querying]] — the runtime query model, continuations, and operator semantics.
- [[Transactions-and-Unit-of-Work]] — multi-document atomicity and boundary detection.
- [[Storage-Scopes]] — scoped vs. global access, and privileged sessions.
- [[Schema-Evolution]] — additive diffs, durable applied state, and the `dotnet groundwork` CLI.
- [[Identity-Generators]] — the sortable id catalog in `Groundwork.Core.Identity`.

**Providers**

- [[Providers]] — the capability model and a side-by-side comparison.
- [[Provider-SQLite]] · [[Provider-PostgreSQL]] · [[Provider-SQL-Server]] · [[Provider-MongoDB]]

**Beyond documents**

- [[Diagnostic-Records]] — the bounded append/query contract for immutable diagnostic streams.
- [[Samples]] — the SupportTickets application and the external Inbox capability module.

**Reference**

- [[Glossary]] — the domain vocabulary, from `CONTEXT.md`.
- [[FAQ]] — common errors, diagnostics, and design questions.

## Packages

Reference the provider package for your database; it brings the core contracts transitively:

| Package | Contents |
|---|---|
| `Groundwork.Sqlite` | SQLite materialization plus document-store and diagnostic-record providers. |
| `Groundwork.PostgreSql` | PostgreSQL materialization and document-store provider. |
| `Groundwork.SqlServer` | SQL Server materialization and document-store provider. |
| `Groundwork.MongoDb` | MongoDB materialization, document-store, and transactional diagnostic-record provider. |
| `Groundwork.Tool` | The `dotnet groundwork` CLI for deployment-time schema validation, planning, status, and application. |

Requires the .NET SDK 10.0 or newer.

> **Note.** An earlier *portable* document model (shared `groundwork_documents` tables,
> `CreateAsync` factories, `PortableDocumentQuery`) is retired and marked obsolete
> (`GW0001`–`GW0005`). This wiki teaches only the physical, route-driven model. See
> [ADR 0006](https://github.com/valence-works/Groundwork/blob/main/docs/adr/0006-retire-the-portable-document-model.md)
> and the [[FAQ]].
