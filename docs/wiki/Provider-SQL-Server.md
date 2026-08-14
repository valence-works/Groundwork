# Provider: SQL Server

`Groundwork.SqlServer` binds the shared relational kernel to SQL Server. Its defining constraint
is **sized index keys**: SQL Server budgets index key bytes (900 bytes for clustered / 1700 bytes
for nonclustered keys), which shapes how identities are stored and why string index keys must
declare a length.

## Opening

```csharp
var provider = new ProviderIdentity("groundwork-sqlserver", "1.0.0");
var store = await SqlServerDocumentStoreFactory.OpenPhysicalAsync(
    connectionString, manifest, provider, DocumentStoreAccess.Global,
    options: new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true });

IBoundedDocumentStore boundedStore = SqlServerPhysicalQueryRuntime.Create(
    store, manifest,
    store.Routes.Single(route => route.StorageUnit.Value == "supportTicket"),
    provider);
```

## The key budget, and declared lengths

- **Declare `length` on string/keyword index fields.** Unbounded string projections are valid
  portable metadata everywhere else, but `SqlServerPhysicalIndexValidator` rejects any physical
  index whose String or Binary key column has no `Length` ("requires bounded String key column").
  Since [ADR 0008](https://github.com/valence-works/Groundwork/blob/main/docs/adr/0008-declared-index-key-lengths.md)
  you state the bound directly on the `LogicalIndexDeclaration` — see [[Declaring-Storage]].
  As a sizing example, the SupportTickets sample bounds keyword keys to 128 UTF-16 code units so
  the widest key — one keyword column (256 bytes) plus the provider-applied identity tie-break
  column (1350 bytes) — stays inside the 1700-byte budget.
- **Identity storage.** Document kind and id are retained as binary-collated `nvarchar(450)`
  values and scope as binary-collated `nvarchar(128)`. Persisted SHA-256 `binary(32)`
  provider-owned columns form the nonclustered physical primary key, while every exact lookup and
  linked join compares both the digest and the retained original. A native key violation is probed
  by digest, and a different retained identity raises `PhysicalIdentityHashCollisionException`
  rather than masquerading as an optimistic-concurrency conflict.
- **Decimal projections**: precision 1–28 with explicit scale (SQL Server's index-key sizing is
  what sets the provider-portable 1–28 envelope).
- **DateTime projections**: `datetimeoffset(7)` — full 100 ns tick precision natively.
- **Identifiers**: 128-character limit, enforced by the same deterministic normalizer used for
  provider-owned column names; a route whose visible column collides with a provider-owned name is
  rejected before use.

## Index pinning and filtered indexes

Planned indexes are pinned with `WITH (INDEX(...))`. For an index declared
`MissingValueBehavior.Excluded` (a filtered index), the pin is decided per invocation from the
predicate: when the predicate proves every excluded column non-null, the query keeps its pin and
carries a redundant `IS NOT NULL` conjunct per excluded column — SQL Server's filtered-index
matching cannot see through expressions like `LOWER(column) LIKE @p`, so the conjunct is what lets
it produce a plan at all. When the predicate does not prove it, a scale-bearing query is refused
by name rather than degraded to a scan. See
[bounded physical query plans](https://github.com/valence-works/Groundwork/blob/main/docs/bounded-physical-query-plans.md).

## Schema application

`RelationalServerPhysicalSchemaExecutor` owns a dedicated, non-pooled connection holding a SQL
Server **session application lock** (`sp_getapplock`) for the provider/manifest lease; history
reads, DDL/backfill, validation, and applied-state recording execute on that session, with
DDL/backfill and the operation-ledger row committing in one transaction. The live catalog is
inspected for exact compatibility with the compiled route; create-if-absent is not compatibility
evidence.

## Bounded mutations

SQL Server mutation execution uses indexed query sources, transaction-owned `sp_getapplock`
operation locks, session-local selection tables, `JSON_MODIFY`, and retained-original plus
persisted-hash identity joins. Transition and assignment parameters preserve numbers and booleans
as native JSON scalars while string, keyword, date-time, and GUID values remain JSON strings.
Ledger lookup keys hash unbounded `nvarchar(max)` identity values through `varbinary(max)`
SHA-256, so operation identities have no length limit.

## Query execution

Predicates, compound ordering, paging, count, any, and first execute server-side. Explain runs the
exact parameterized read under `SET STATISTICS XML ON` and inspects the actual-plan XML (format:
`sqlserver-statistics-xml`) — note this executes the real read. Conformance asserts the optimizer
selected the declared physical index.

## See also

- [[Providers]] — side-by-side comparison.
- [[Declaring-Storage]] — declared key lengths and numeric precision/scale.
