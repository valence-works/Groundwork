# Groundwork

Groundwork is a provider-neutral persistence foundation. Modules describe storage intent, providers report what they can serve, and materialization prepares provider storage for those manifests.

## Language

**Contract Family**:
One set of storage semantics with its own declaration model, store contract, and provider execution.
Documents and diagnostic records are the two that exist. A contract family belongs to whoever needs
it; Groundwork owns only the kernel facilities it builds on.
_Avoid_: Module, provider package, storage contract

**Kernel Facility**:
Machinery reusable by more than one contract family: physical definitions, host naming and
fingerprinting, schema evolution, provider session lifecycle, the capability registry, the operation
ledger, and the conformance-suite pattern. A facility qualifies only if a second contract family can
use it unmodified.
_Avoid_: Core, shared infrastructure, base contract

**Provider Capability**:
The provider's runtime ability to serve a storage manifest's semantics, including query, index, concurrency, and workload requirements.
_Avoid_: Materialization capability, schema capability

**Materialization Capability**:
The provider's ability to prepare storage for a manifest, including schema history and supported materialization operations.
_Avoid_: Provider capability, runtime capability

**Materialization Plan**:
A self-contained description of the storage-preparation work needed to make a provider ready for a
manifest. "Materialization" means storage preparation and nothing else; a runtime write-path
structure is linked storage, not a materialization.
_Avoid_: Storage manifest, provider plan, relationship materialization

**Materialization Operation**:
One executable storage-preparation step inside a materialization plan.
_Avoid_: Provider capability, manifest rule

**Physical Storage Form**:
One of the three provider-neutral document layouts: shared documents, a dedicated document table,
or a physical entity table. Canonical JSON remains authoritative in every form.
_Avoid_: Portable mode, optimized mode

**Physical Table Definition**:
The provider-neutral structural definition for one storage unit: its selected form, envelope and
canonical JSON columns or shared-storage binding, projected columns, physical indexes, schema
version, and evolution metadata. Shared or dedicated document storage may name an auxiliary linked
projected/index table with explicit relationship fields; physical-entity projections are stored
in-primary.
_Avoid_: Provider DDL, materialization plan

**Resolved Physical Definition**:
A physical table definition after deterministic defaults, host naming policy, and per-unit name
overrides, but before provider identifier normalization.
_Avoid_: Provider physical definition, raw manifest

**Provider Physical Definition**:
A resolved physical definition with final provider identifiers and a deterministic fingerprint.
It contains no provider SDK types and is the common input for later provider execution work.
_Avoid_: Hand-authored provider schema

**Executable Storage Route**:
An immutable provider-neutral mapping compiled from one provider physical definition. It fixes the
primary and linked objects, envelope fields, linked relationship fields, projected fields,
scope/discriminator/identity keys, maintenance targets, candidate bounded-query paths, capability
requirements, resolved names, and fingerprints consumed by later provider execution.
_Avoid_: Provider query translation, inferred workload route, raw DDL plan

**Schema History**:
Durable evidence of manifest/provider identity, resolved names, definition and executable-route
fingerprints, operation identities, timestamps, and the canonical applied snapshot recorded only
after acknowledged provider execution.
_Avoid_: Migration class list

**Physical Schema Diff**:
A deterministic additive comparison between desired executable storage routes and durable applied
provider state. It emits semantic create, projection, index, canonical-JSON backfill, validation,
and recording operations without provider DDL. Provider executors acknowledge those exact
operations durably and record canonical applied state only after validation.
_Avoid_: Provider migration script, manifest-version comparison

**Semantic Migration**:
An explicitly authored provider-neutral data transformation used only when a desired-state diff
cannot infer the change.
_Avoid_: General structural migration pipeline

**Bounded Query**:
A declared, closed set of predicate paths/operators, compound ordering, paging, result operations,
and optional latest selection. A scale-bearing bounded query makes its referenced stable index
paths binding physical projection and indexed server-side-plan demand. Equality predicate prefixes
may be followed by ordered suffixes; every ordered physical plan adds identity as its total-order
tie-breaker.
_Avoid_: IQueryable, arbitrary LINQ

**Physical Query Plan**:
Immutable provider output describing the selected linked+primary, primary envelope/JSON, entity
projection, or provider-native field route for a bounded document query. It always carries the
mandatory storage scope and deterministic identity tie-break; unsupported declarations produce no
client-fallback plan. Provider handlers certify and execute the exact provider, route, object,
index, and field mapping before the store can serve traffic. Callers do not submit physical query
plans.
_Avoid_: Document query

**Bounded**:
The adjective for every declared, closed contract Groundwork serves: bounded queries, bounded
mutations, bounded grouped reductions. A bounded contract states its whole shape up front so a
provider can certify it before traffic.
_Avoid_: Closed, portable (as a contract-family name)

**Portable**:
Provider-neutral, and nothing else. It qualifies types and values that mean the same thing on every
provider; it never names a storage form, a query family, or an optimization level.
_Avoid_: Portable mode, portable query (as a family), optimized

**Linked Storage**:
A derived structure holding query keys or relationship keys plus a reference back to its document. It
is maintained atomically with canonical JSON and is never a physical storage form of its own.
_Avoid_: Sidecar, physicalization table, optimized projection

**Relationship Reference Storage**:
The generated, runtime-maintained linked storage that makes one declared cross-unit reference
queryable and fenceable. It is a write-path structure, not a schema-preparation step, so
"materialization" does not name it. The shipped types still carry the older names; this is the
vocabulary new work uses and the rename targets.
_Avoid_: Relationship materialization, relationship sidecar

**Reference Fence Generation**:
The monotonic generation that fences a relationship reference against a concurrent change to its
target, so a stale reference can never be read as proof of an active target. The shipped field is
still named for materialization.
_Avoid_: Materialization generation

**Diagnostic Record Store**:
A specialized, provider-neutral append/query/inspection/retention contract for immutable,
time-ordered, tenant-scoped diagnostic streams. It is separate from ordinary document storage and
from destructive queue/outbox semantics.
_Avoid_: Document physical-storage form, event store, outbox, arbitrary query engine

**Diagnostic Cursor**:
An opaque, provider-assigned monotonic position within one tenant, storage scope, and diagnostic
stream. It is the total-order tie-breaker and survives record trim through stream metadata.
_Avoid_: Application sequence, occurrence timestamp, cross-stream global sequence

**Diagnostic Continuation**:
A query-shape-bound keyset value carrying the first page's committed cursor high-water and the last
ordered key/cursor. It provides a stable traversal that excludes later and backdated appends.
_Avoid_: Offset, live-view page token

**Document Query Continuation**:
An opaque, plan/query/scope-bound exclusive keyset boundary returned by a cursor-declared bounded
document query. It is stable across restart but intentionally uses live-view semantics between page
requests; it does not claim the snapshot high-water guarantees of a Diagnostic Continuation.
_Avoid_: Offset token, snapshot cursor

**Storage Scope**:
A provider-neutral, opaque partition identity bound to a document-store session and explicit unit
of work. Groundwork stamps it into envelope and dependent physical keys; it is never inferred from
document payload data and does not represent an application authorization decision.
_Avoid_: Ambient tenant filter, payload tenant field, implicit wildcard

**Privileged Storage Session**:
An explicitly acquired document-store session carrying a distinct capability for one target scope,
global storage, or cross-scope queries. Acquisition emits audit evidence and never results from a
missing ordinary scope.
_Avoid_: Tenant-agnostic query flag, absent tenant fallback, authorization bypass

## Provider implementation status

MongoDB consumes executable storage routes directly for shared, dedicated, and physical-entity
documents. Generation-fenced leases atomically protect operation evidence and applied state, while
document-incarnation tokens keep restart backfill safe across delete/recreate races. Exact
linked/native handler certifications bind bounded queries to resolved collections, fields, and
indexes. Typed numeric projections validate their original JSON lexemes and declared shape;
DateTime projections use exact UTC ticks. A cached transaction-topology gate protects factory and
direct physical materializer/store entry points before side effects or sessions, and route
validation rejects views, time-series namespaces, and capped collections. Native Number/DateTime
paths without a typed projection fail before traffic.
