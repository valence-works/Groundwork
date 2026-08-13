# Glossary

The Groundwork vocabulary, adapted for consumers from
[`CONTEXT.md`](https://github.com/valence-works/Groundwork/blob/main/CONTEXT.md) (the
authoritative short reference). Terms link to the wiki page where the concept is used.

**Bounded**
The adjective for every declared, closed contract Groundwork serves: bounded queries, bounded
mutations, bounded grouped reductions. A bounded contract states its whole shape up front so a
provider can certify it before traffic.

**Bounded query**
A declared, closed set of predicate paths/operators, compound ordering, paging, result
operations, and optional latest selection. A scale-bearing bounded query makes its referenced
stable index paths binding physical projection and indexed server-side-plan demand. See
[[Declaring-Storage]] and [[Querying]].

**Contract family**
One set of storage semantics with its own declaration model, store contract, and provider
execution. Documents and diagnostic records are the two that exist. A contract family belongs to
whoever needs it; Groundwork owns only the kernel facilities it builds on.

**Diagnostic continuation**
A query-shape-bound keyset value carrying the first page's committed cursor high-water and the
last ordered key/cursor. It provides a stable traversal that excludes later and backdated
appends. See [[Diagnostic-Records]].

**Diagnostic cursor**
An opaque, provider-assigned monotonic position within one tenant, storage scope, and diagnostic
stream. It is the total-order tie-breaker and survives record trim through stream metadata. Not an
application sequence, occurrence timestamp, or cross-stream global sequence.

**Diagnostic record store**
A specialized, provider-neutral append/query/inspection/retention contract for immutable,
time-ordered, tenant-scoped diagnostic streams. Separate from ordinary document storage and from
destructive queue/outbox semantics. See [[Diagnostic-Records]].

**Document query continuation**
An opaque, plan/query/scope-bound exclusive keyset boundary returned by a cursor-declared bounded
document query. Stable across restart but intentionally live-view between page requests; it does
not claim the snapshot high-water guarantees of a diagnostic continuation. See [[Querying]].

**Executable storage route**
An immutable provider-neutral mapping compiled from one provider physical definition. It fixes
the primary and linked objects, envelope fields, projected fields, scope/discriminator/identity
keys, maintenance targets, candidate bounded-query paths, capability requirements, resolved
names, and fingerprints consumed by later provider execution. Exposed as `store.Routes`; see
[[Opening-Stores]].

**Kernel facility**
Machinery reusable by more than one contract family: physical definitions, host naming and
fingerprinting, schema evolution, provider session lifecycle, the capability registry, the
operation ledger, and the conformance-suite pattern. A facility qualifies only if a second
contract family can use it unmodified.

**Linked storage**
A derived structure holding query keys or relationship keys plus a reference back to its
document. It is maintained atomically with canonical JSON and is never a physical storage form of
its own.

**Materialization capability**
The provider's ability to *prepare storage* for a manifest, including schema history and
supported materialization operations. Distinct from provider capability.

**Materialization plan / operation**
A self-contained description of the storage-preparation work needed to make a provider ready for
a manifest, and one executable step inside it. "Materialization" means storage preparation and
nothing else.

**Physical query plan**
Immutable provider output describing the selected route for a bounded document query. It always
carries the mandatory storage scope and deterministic identity tie-break; unsupported
declarations produce no client-fallback plan. Callers do not submit physical query plans.

**Physical schema diff**
A deterministic additive comparison between desired executable storage routes and durable applied
provider state, emitting semantic operations without provider DDL. See [[Schema-Evolution]].

**Physical storage form**
One of the three provider-neutral document layouts: shared documents, a dedicated document table,
or a physical entity table. Canonical JSON remains authoritative in every form. See
[[Declaring-Storage]].

**Physical table definition**
The provider-neutral structural definition for one storage unit: its selected form, envelope and
canonical JSON columns or shared-storage binding, projected columns, physical indexes, schema
version, and evolution metadata.

**Portable**
Provider-neutral, and nothing else. It qualifies types and values that mean the same thing on
every provider; it never names a storage form, a query family, or an optimization level. (The
retired "portable document model" is a historical usage — see the [[FAQ]].)

**Privileged storage session**
An explicitly acquired document-store session carrying a distinct capability for one target
scope, global storage, or cross-scope queries. Acquisition emits audit evidence and never results
from a missing ordinary scope. See [[Storage-Scopes]].

**Provider capability**
The provider's runtime ability to serve a storage manifest's semantics, including query, index,
concurrency, and workload requirements. Computed into a `ProviderFit` by
`ProviderCapabilityValidator`. See [[Providers]].

**Provider physical definition**
A resolved physical definition with final provider identifiers and a deterministic fingerprint.
It contains no provider SDK types and is the common input for provider execution.

**Resolved physical definition**
A physical table definition after deterministic defaults, host naming policy, and per-unit name
overrides, but before provider identifier normalization.

**Schema history**
Durable evidence of manifest/provider identity, resolved names, definition and executable-route
fingerprints, operation identities, timestamps, and the canonical applied snapshot recorded only
after acknowledged provider execution. See [[Schema-Evolution]].

**Semantic migration**
An explicitly authored provider-neutral data transformation used only when a desired-state diff
cannot infer the change.

**Storage scope**
A provider-neutral, opaque partition identity bound to a document-store session and explicit unit
of work. Groundwork stamps it into envelope and dependent physical keys; it is never inferred
from document payload data and does not represent an application authorization decision. See
[[Storage-Scopes]].
