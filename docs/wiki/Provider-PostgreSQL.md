# Provider: PostgreSQL

`Groundwork.PostgreSql` binds the shared relational kernel — the same route-driven store, query,
acknowledgement, compare-and-swap, and backfill machinery as SQLite and SQL Server — to
PostgreSQL with provider-owned DDL, metadata, locking, value, and explain adapters.

## Opening

```csharp
var provider = new ProviderIdentity("groundwork-postgresql", "1.0.0");
var store = await PostgreSqlDocumentStoreFactory.OpenPhysicalAsync(
    connectionString, manifest, provider, DocumentStoreAccess.Global,
    options: new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true });

IBoundedDocumentStore boundedStore = PostgreSqlPhysicalQueryRuntime.Create(
    store, manifest,
    store.Routes.Single(route => route.StorageUnit.Value == "supportTicket"),
    provider);
```

Connections are pooled per operation; there is no retained shared connection.

## Quirks and limits

- **Transactions abort on the first failed statement.** Inside an explicit unit of work, after any
  non-success result the transaction is aborted server-side — rollback is the only valid next
  step. Groundwork's staging contract already requires the caller to roll back on any non-success
  outcome, so code written to the contract is unaffected. See [[Transactions-and-Unit-of-Work]].
- **Identifier limit: 63 bytes.** `PostgreSqlGroundworkCapabilities.PhysicalNames` truncates long
  logical names on a UTF-8 rune boundary within PostgreSQL's 63-byte limit and appends a
  deterministic semantic hash so long names do not silently collide; the executor quotes every
  final identifier.
- **DateTime projections are stored as UTC ticks** (an integer), because native timestamps round
  Groundwork's 100 ns contract to microseconds.
- **Decimal projections**: precision 1–28 with explicit scale, matching the exact CLR decimal
  conversion boundary used by live writes, backfills, defaults, and query parameters.
- **Never pins an index.** PostgreSQL has no index hint; Groundwork emits the null-exclusion
  filter conjuncts where applicable and leaves plan choice to the optimizer, while conformance
  still asserts (via `EXPLAIN (FORMAT JSON)`) that the optimizer selects the declared physical
  index.
- **Bounded mutations** use transaction-scoped advisory operation locks, `ON COMMIT DROP`
  selection tables, and native `text[]` JSON paths with `jsonb_set`. Ledger lookup keys are
  SHA-256 digests computed by a validated provider-owned immutable function over stored generated
  `bytea` columns, so operation identities have no length limit.

## Schema application

`RelationalServerPhysicalSchemaExecutor` owns a dedicated, non-pooled connection holding a
PostgreSQL **advisory lock** for the provider/manifest application lease; history reads, DDL and
backfill operations, validation, and applied-state recording all execute on that lock-owning
session. DDL/backfill and the matching operation-ledger row commit in one transaction, and
acknowledgements are reread from durable storage so a retry after response loss returns the
database timestamp. The live catalog is inspected for exact compatibility — envelope types,
nullability, collation, primary-key order, projected type/default/collation, and index
ownership/uniqueness/order/direction must match the compiled route; create-if-absent is not
compatibility evidence.

## Query execution

Predicates, compound ordering, paging, count, any, and first execute server-side from the exact
certified plan. Explain output format: `postgresql-json`.

## See also

- [[Providers]] — side-by-side comparison.
- [[Schema-Evolution]] — the deployment protocol these executors implement.
