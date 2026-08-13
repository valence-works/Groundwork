# Groundwork.PostgreSql

`PostgreSqlPhysicalSchemaExecutor`, `PostgreSqlPhysicalDocumentStore`, and
`PostgreSqlPhysicalQueryRuntime` implement all three compiled physical storage forms.
`PostgreSqlPhysicalMutationRuntime` executes declared bounded deletes, finite-source transitions,
and manifest-fixed assignments with exact idempotent outcomes. Assignment uses only its admitted
selector, overwrites every selected scalar target—including already-target, null, missing, and
extension-defined values—and returns the exact matched/processed count. Schema
application uses advisory locks and a transactional operation ledger; document and query operations
use independent pooled sessions. Declared date-time projections use exact UTC ticks to avoid native
microsecond rounding, and no client-side query fallback is available.

`Groundwork.PostgreSql` also implements the provider-neutral diagnostic-record contract through
`PostgreSqlDiagnosticRecordStoreFactory`. Diagnostic stores use independent pooled sessions, native
`LIMIT`, `strpos`, session advisory per-stream locks, `C`-collated comparison keys, partial latest-per-key
indexes, durable operation tombstones, and bounded `ctid` cleanup.

## Current Scope

- Executes declared bounded mutations with transaction-scoped advisory locks, exact identity
  selection, and durable replay evidence.
- Exposes `PostgreSqlGroundworkCapabilities.Runtime()`, advertising `IndexCapabilities.All` and the full query-operation set.

## Factory and session lifecycle

`PostgreSqlDocumentStoreFactory.OpenPhysicalAsync` is the route-driven startup gate. It inspects the
durable physical schema without mutation by default and accepts
`GroundworkRuntimeSchemaAdmissionOptions.AutoApplyOnStartup` for opt-in safe-only application. The
returned store is stateless: independent operations acquire concurrent pooled connections, while an
explicit unit of work owns one connection and transaction until completion. Pool limits and
timeouts—not a Groundwork-wide semaphore—provide backpressure.

## Deliberate Limits

- JSON content is stored as text (no `jsonb` column or provider-specific JSON indexing).
- Definitions whose predicate bounds can exceed PostgreSQL's 65,535-parameter command ceiling are
  rejected before materialization.
- No Entity Framework dependency.
- No host-specific dependency.
