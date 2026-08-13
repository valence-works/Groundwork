# Providers

Groundwork ships four providers — SQLite, PostgreSQL, SQL Server, and MongoDB — that all serve the
same manifest, the same store contracts, and the same conformance suites. This page explains the
capability model that binds them and compares their practical differences.

Per-provider pages: [[Provider-SQLite]] · [[Provider-PostgreSQL]] · [[Provider-SQL-Server]] ·
[[Provider-MongoDB]].

## Executable capabilities, not metadata

A provider capability claim must correspond to the same registered handler or execution path that
implements it — capability reports are not optimistic metadata maintained independently from
execution. Planning rejects unsupported combinations before startup, and shared conformance suites
verify advertised behavior on every provider, including the expected physical query plan (native
explain evidence), not only result equality.

Provider fit is computed from **declared requirements**:

- A storage unit's `StorageIntent` declares the capabilities it requires (see
  [[Declaring-Storage]]).
- `ProviderCapabilityValidator` compares a manifest against a provider's capability report and
  returns a `ProviderFit`.
- The capability registry is **open/closed**: external modules contribute their own
  `CapabilityId`s and descriptors via `IGroundworkModule`/`GroundworkModuleCatalog`, and the same
  validator derives fit for them exactly as for built-ins. An unregistered requirement is a
  `GW-CAP-014` error. The [[Inbox sample|Samples]] demonstrates this end to end.

The one built-in well-known capability is `AtomicCommit`
(`groundwork.operational.atomic-commit`) — cross-unit atomic commit behind
`IDocumentUnitOfWork`, advertised by every shipped provider (MongoDB: on replica-set/sharded
topologies only). See
[ADR 0004](https://github.com/valence-works/Groundwork/blob/main/docs/adr/0004-retire-groundwork-operational.md).

## Comparison

| | SQLite | PostgreSQL | SQL Server | MongoDB |
|---|---|---|---|---|
| Package | `Groundwork.Sqlite` | `Groundwork.PostgreSql` | `Groundwork.SqlServer` | `Groundwork.MongoDb` |
| Open | `OpenPhysicalAsync` + per-route `SqlitePhysicalQueryRuntime` | same shape, `PostgreSqlPhysicalQueryRuntime` | same shape, `SqlServerPhysicalQueryRuntime` | `OpenPhysicalAsync` returns an open handle; the store is also the bounded store for every unit |
| Cross-unit transactions | Yes (`DbTransaction`) | Yes; aborts on first failed statement — rollback only | Yes | Replica set / sharded only; standalone throws `UnsupportedAtomicCommitException` |
| Keyset paging / latest-per-key | Not advertised (fails before traffic) | Advertised per handler certification | Advertised per handler certification | Unsupported keyset/latest declarations fail before traffic |
| Decimal projections | Precision 1–18, fixed-scale integer storage | Precision 1–28 | Precision 1–28 | Typed numeric projections validate original JSON lexemes |
| DateTime projections | UTC ticks (integer) | UTC ticks (native timestamps would round to µs) | `datetimeoffset(7)` | Exact UTC ticks |
| Canonical-JSON Number/DateTime query sources | Not certified — use a projected route | Certified | Certified | Native paths without a typed projection fail before traffic |
| Identifier limits | — | 63 bytes (UTF-8 rune-boundary truncation + semantic hash) | 128 characters (same deterministic normalizer for provider-owned columns) | Collection/field naming via route resolution |
| Index pinning | `INDEXED BY` | Never pins; emits the filter, optimizer decides | `WITH (INDEX(...))` + filtered-index conjuncts | Hint, decided per-invocation from the predicate |
| Explain format | `sqlite-query-plan` | `postgresql-json` | `sqlserver-statistics-xml` | `mongodb-json` |
| Schema application lock | Direct-connection lease; whole-plan single transaction | Advisory lock on a dedicated non-pooled session | `sp_getapplock` on a dedicated non-pooled session | Generation-fenced leases |
| Schema-tool alias | `sqlite` | `postgresql` | `sqlserver` | `mongodb` (needs `--database` unless in URI) |

## What is identical everywhere

- The manifest, the store contracts (`IDocumentStore`, `IBoundedDocumentStore`), operator
  semantics (see [[Querying]]), scope isolation (see [[Storage-Scopes]]), and the unit-of-work
  contract (see [[Transactions-and-Unit-of-Work]]).
- All three physical storage forms, with canonical JSON authoritative in each.
- The additive schema-evolution protocol, durable applied state, and the CLI (see
  [[Schema-Evolution]]).
- `MissingValueBehavior.Excluded` realized identically from one shared rule: a filtered index on
  SQL Server and PostgreSQL, a partial index on SQLite, a partial filter expression on MongoDB.
- Conformance-tested pooled sessions: stateless store facades, a pooled connection per autonomous
  operation, one connection/transaction per explicit unit of work.

## External providers

Store constructors and the reusable relational implementation seam are internal to the built-in,
conformance-tested provider adapters. External providers implement `IDocumentStore` and expose
their own admission-first factory; external *modules* can also build on the reusable
`Groundwork.Provider.Relational` toolkit, as the Inbox sample's SQLite provider does.
