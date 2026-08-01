# Retire Groundwork.Operational; keep the operational capability vocabulary

Status: accepted (2026-07-31).

Date: 2026-07-31.

Supersedes the implementation half of
[Groundwork operational persistence design](../reports/groundwork-operational-persistence-design.md).

Related: [Groundwork runtime evaluation](../reports/groundwork-runtime-evaluation.md).

## Context

`Groundwork.Operational` and `Groundwork.Operational.Relational` were introduced to serve the
workflow-runtime hot path that [the runtime evaluation](../reports/groundwork-runtime-evaluation.md)
classified as `Specialized provider — NoGo`: workflow execution mailbox and agent ownership,
post-commit intents and outbox, and distributed locks and leases. Those rows correctly said the
portable `IDocumentStore` contract cannot serve those workloads and that a specialized provider
contract was required.

The packages were then designed and built before the consuming contract existed. The consumer
subsequently wrote its own, and the two do not match.

### Evidence

Elsa Foundation's `Directory.Packages.props` pins seven Groundwork packages — `Groundwork.Core`,
`Groundwork.Documents`, `Groundwork.DiagnosticRecords`, and the four provider packages.
`Groundwork.Operational` and `Groundwork.Operational.Relational` are not among them; they arrive only
transitively because `Groundwork.Sqlite` references them.

Elsa's `groundwork-host-configurable-persistence-feasibility.md` states that
`IRuntimePostCommitOutboxStore` is bridged over `IDocumentStore`, "deliberately not over Groundwork's
operational `IOutboxStore`", citing structural incompatibility between Groundwork's server-generated
ids plus lease tokens and Elsa's caller-supplied deterministic ids plus optional ownership fields.
The same report records that all ten runtime persistence seams plus a durable checkpoint writer are
implemented and tested over Groundwork's portable document store. Elsa's durable scheduler queue,
`GroundworkWorkflowSchedulerWorkQueue`, is `IDocumentStore`-backed; its lease/lock implementations
live in `src/Elsa/Locking`.

### Why the contracts do not fit

The mismatch is structural, not stylistic:

1. **Idempotency is on the wrong side of the queue.** `IWorkQueueStore.EnqueueAsync` accepts
   `EnqueueRequest(Unit, PartitionKey, Payload, MaxAttempts, InitialDelay)` and returns the
   server-assigned `EnqueueResult(MessageId, Sequence)`. The only caller-supplied idempotency key in
   the interface is on `DequeueAsync`. A consumer whose outbox redelivery re-enqueues the same work
   needs producer-side idempotency — Elsa keys enqueue by `(WorkflowExecutionId, WorkItemId)` — and
   the interface cannot express it.
2. **Claim-with-lease versus peek-and-ack-delete.** `ClaimAsync` mutates on read: it stamps a lease
   token, sets a visibility deadline, and increments the attempt counter. A redrive-safe drain peeks
   the head, dispatches in place, and ack-deletes only once the handler's effect is durable, so a
   crash leaves the item exactly where it was. Under claim-with-lease the same crash hides the item
   until the visibility timeout expires and has already consumed an attempt.
3. **`IOutboxStore.AppendAsync` returns `Task`.** No id is accepted and none is returned, so a
   consumer cannot key outbox items by a deterministic continuation id.

### Why provider-neutrality did not require these packages

Provider-neutral queue, outbox, and lease semantics do not require per-provider queue, outbox, and
lease implementations. Those protocols need three things, all of which `Groundwork.Documents` already
provides portably across SQLite, PostgreSQL, SQL Server, and MongoDB:

- atomic compare-and-swap on a document (optimistic concurrency);
- a monotonic ordering key;
- multi-document atomic commit (`IDocumentUnitOfWork`).

Native per-provider implementations (`SKIP LOCKED`, `findOneAndUpdate`, `READPAST`) buy throughput,
not correctness or portability. Per the runtime evaluation's own rule, adopting them requires
benchmark evidence that has never been collected. Maintaining them costs four providers times three
primitives times full recovery semantics — exactly the per-provider multiplication the
[physical storage and operations readiness](../program-goals/physical-storage-and-operations-readiness.md)
program goal exists to avoid.

## Decision

### 1. Remove the operational implementation packages

`Groundwork.Operational`, `Groundwork.Operational.Relational`, and the SQLite operational store and
materializer are removed, together with their tests and the support-ticket sample's operational
showcase. `Groundwork.Sqlite` no longer references them, so no consumer receives them transitively.

### 2. Keep the capability machinery; declare only capabilities providers advertise

The open/closed capability seam is the durable half of the original design and is kept in full:
`CapabilityId`, `CapabilityRegistry`, `CapabilityDescriptor`, `IGroundworkModule`,
`WorkloadEvidencePolicy`, `ProviderCapabilityValidator`, `ProviderFit`, and
`StorageIntent.Operational`. A consumer can still declare operational demand in its manifest and have
fit computed from declared requirements rather than author self-declaration.

`WellKnownCapabilities` is trimmed to `AtomicCommit` alone — the one built-in every shipped provider
actually advertises, for `IDocumentUnitOfWork`. `AtomicClaim`, `LeaseRecovery`, `FencedOwnership`,
`OrderedConsumption`, `RetryRecovery`, `Idempotency`, `RetentionPolicy`, `ConcurrencyEvidence`,
`OperationalDiagnostics`, and `RangeQuery` are removed: with the implementations gone, no provider
advertises them and nothing outside test fixtures referenced them. Core declaring capabilities no
Groundwork provider can serve is the same speculative posture this ADR is correcting.

A module that needs those concepts contributes its own descriptors in its own vendor namespace —
exactly the path `Groundwork.Modules.Inbox` already demonstrates, and the path the test suite now
uses via `TestCapabilities`. An unregistered requirement remains a `GW-CAP-014` error, so this is a
declaration change, not a loosening.

`AtomicCommit` keeps its existing id string `groundwork.operational.atomic-commit` despite the now
odd `operational` segment: capability ids reach executable-route fingerprints, so renaming one would
read as physical schema drift for no functional gain.

### 3. Groundwork owns primitives; consumers own protocols

The boundary is drawn at the primitive, not the protocol. Visibility timeouts, fencing, attempt
budgets, dead-lettering, and redrive strategy are where consumers legitimately differ — as Elsa and
this design already did. Groundwork's contribution is the portable substrate those protocols are
built on.

### 4. Revisit only with a second consumer

If a second consumer needs operational primitives, build the shared implementation **once over
`IDocumentStore`**, not per provider, and derive its contract from what the two consumers actually
have in common. Until then, a shared implementation is speculative generality.

## Consequences

- Roughly 1,700 lines of `src`, plus tests and the sample showcase, are removed. No capability that
  any known consumer uses is lost.
- Every `Groundwork.Sqlite` consumer stops shipping two unused assemblies.
- `StorageIntent.Operational` and `ProviderCapabilityValidator` are unchanged, so manifests that
  declare operational demand continue to validate — but a manifest that referenced one of the removed
  `WellKnownCapabilities` ids must now declare that capability through its own module descriptor.
  This is a source-breaking change for such a manifest, caught at compile time.
- `docs/reports/groundwork-operational-persistence-design.md` is retained as the historical design
  record, marked superseded.
- The runtime evaluation's `NoGo` rows stand. They correctly said the portable document contract
  cannot serve those workloads; this ADR only records that Groundwork is not the right place to
  implement the protocols that do.

### Applied in the same change

The same principle — a contract is justified by a named workload — was applied to the diagnostic
record store's grouped-reduction capability, which appeared to have shipped without a consumer
requirement on record. It was removed and then restored: reading the consuming source, rather than
the consumer's design document, found the requirement in the OpenTelemetry trace-list and
trace-detail endpoints. Grouped reduction is retained and its workload is now recorded on both sides;
see [diagnostic-records-grouped-reduction-scope](../reports/diagnostic-records-grouped-reduction-scope.md).

The episode sharpens the principle rather than weakening it. "A named workload" means a workload
named against the consumer's *code* at the version that consumes the contract. A design document is
evidence of intent at its date; it does not stay current on its own, and an absence in it is not
evidence that the requirement does not exist.
