# Elsa EF Core removal: sequencing against the runtime-evaluation gate

Program goal state: [Physical Storage and Operations Readiness](../program-goals/physical-storage-and-operations-readiness.md).

Related: [ADR 0004](../adr/0004-retire-groundwork-operational.md), [Groundwork runtime evaluation](groundwork-runtime-evaluation.md), [Groundwork vocabulary and public API reconciliation](groundwork-vocabulary-and-public-api.md).

Date: 2026-08-02.

Status: recommendation. This report proposes an ordering for a consumer-side migration; it changes no
Groundwork runtime implementation and does not reopen [ADR 0003](../adr/0003-adopt-three-physical-storage-forms.md)
or [ADR 0004](../adr/0004-retire-groundwork-operational.md).

## Question

Elsa Foundation (unreleased) currently runs both EF Core and Groundwork, and intends to remove EF
Core entirely. Is that the right call, and in what order?

## Decision summary

**Remove EF Core before GA. Sequence the removal against the evidence gate, not the calendar.**

The decision is not whether to adopt Groundwork. [ADR 0004](../adr/0004-retire-groundwork-operational.md)
records that Elsa already pins seven Groundwork packages, that all ten runtime persistence seams plus
a durable checkpoint writer are implemented over the portable document store, that
`GroundworkWorkflowSchedulerWorkQueue` is `IDocumentStore`-backed, and that Elsa owns its own
lease/lock implementations. The bet is made and largely executed, including on seams the runtime
evaluation classified `BenchmarkGate` and `NoGo` — built as consumer-owned protocols over
Groundwork primitives, which is the split ADR 0004 §3 prescribes.

The open question is whether to remove the remaining fallback, and the answer is yes, in a specific
order.

## The gate is unpaid, and it names EF Core as an input

Three artifacts in this repository agree:

1. [Groundwork runtime evaluation](groundwork-runtime-evaluation.md) — **Hard Rule**: "No workflow
   runtime hot path should move to Groundwork default from this roadmap alone." Required evidence is
   p95/p99 latency under start/resume/checkpoint concurrency, optimistic-concurrency behaviour under
   parallel attempts, retry and idempotency after failure and restart, and checkpoint/migration
   diagnostics.
2. [`benchmarks/Groundwork.PhysicalStorage.Benchmarks`](../../benchmarks/Groundwork.PhysicalStorage.Benchmarks/README.md)
   — the harness "contains no EF Core comparison, cannot promote baselines, and cannot make an Elsa
   migration go/no-go decision." What exists is one SQLite correctness slice that explicitly does not
   prove four-provider recovery, compare EF, or issue a migration verdict.
3. [Physical Storage and Operations Readiness](../program-goals/physical-storage-and-operations-readiness.md),
   objective 8 — baselines "including an EF Core relational oracle where an application migration
   needs one."

The reservation about removing EF Core is therefore well founded, but it is not about Groundwork
being unproven in the abstract. This repository defines a gate for exactly this decision, that gate
names an EF Core comparison as an input, and it has not been walked through.

## The ordering error to avoid

**EF Core is currently the only oracle, and the benchmark that produces the verdict needs it.**
Removing EF Core first destroys the instrument that was supposed to authorise the removal. After
that, a Groundwork correctness or latency regression has nothing to be compared against.

Every other step is *recoverable*: if Groundwork disappoints, the adapters are rewritten while
Elsa's module contracts and business logic survive. That is not the same as reversible — the
adapters are the entire store × provider surface, so replacing the engine means redoing the work
rather than undoing the decision. Size the up-front evidence accordingly.

Destroying the oracle is the one step that is neither reversible nor recoverable.

## The opposite trap

Carrying two persistence stacks into GA is its own failure: double the provider matrix, double the
conformance burden, module authors forced to choose, and users depending on both — which welds the
seam shut permanently. Pre-GA is the only cheap window for the removal, and it expires.

## Recommended order

1. **Harvest the oracle before deleting it.** While EF Core is still in the consumer's tree, run the
   behavioural/conformance suites and workload benchmarks against both stacks and retain the
   comparison as evidence artifacts, using the harness's existing digest-anchored evidence mechanism.
   Then delete EF Core; Git history is the archive, not a maintained parallel implementation.
2. **Remove EF Core from declarative and configuration-shaped stores immediately.** No gate applies
   and no oracle is needed; this also shrinks the surface the gate must cover.
3. **Land the vocabulary and public-API reconciliation before the surface freezes.** Seven files
   under `src/Groundwork` still carry `Obsolete` markers, and the
   [reconciliation report](groundwork-vocabulary-and-public-api.md) still records two competing query
   paths (`DocumentStoreQuery` versus `PortableDocumentQuery`), duplicated query-capability intent
   where "one manifest can describe two answers" (`IndexDeclaration.SupportedOperations` versus
   `PortableQueryDeclaration.Operations`), physicalization policy names that do not correspond to
   ADR 0003's forms, and an unconverged duplicate `Groundwork.Core.Materialization` alongside the
   independent imperative migration pipeline. Removing EF Core pins every consuming module to
   whatever this surface is on that day, obsolete half included. The duplicated-capability item is a
   correctness hazard, not a naming preference.
4. **Pay the runtime-evaluation gate for the hot-path seams** — checkpoint, bookmark, durable value,
   scheduler queue, outbox — across all four providers, extending the benchmark harness past its
   current SQLite-only correctness slice.
5. **Then remove EF Core entirely**, with the verdict recorded against the runtime-evaluation matrix
   rather than a roadmap date. A seam that fails its numbers gets a specialized implementation over
   the document-store primitives — per ADR 0004 §3 that is a consumer protocol change, not a reason
   to retain EF Core.
6. **Graduate providers independently.** Passing the shared conformance suite is not the same as
   being production-supported. One relational provider should be the first proving ground with a real
   pilot, telemetry, and runbooks; MongoDB stays preview until it graduates on its own evidence and
   on a transaction-capable topology. The code already holds the stricter Mongo precondition — the
   cached transaction-topology gate rejects standalone deployments before schema or session side
   effects — so an explicit preview marking matches what the implementation already enforces.
7. **Do not ship GA with both stacks.** If steps 3 and 4 cannot complete before the GA date, move the
   date or cut GA scope rather than shipping two implementations.

## Where the abstraction boundary can and cannot go

A proposal that consuming modules should never reference Groundwork is half already satisfied and
half not achievable. The distinction decides how much insulation is worth building:

- **Runtime contract: already Groundwork-free.** The consumer's runtime seams are domain-owned
  interfaces; ADR 0004 records the consumer declining Groundwork's operational contracts and
  implementing its own protocols over the portable substrate.
- **Declaration: not separable without duplication.** A module that needs storage declares a storage
  manifest — indexes, bounded queries, physical storage form. Wrapping that in a consumer-native DSL
  re-specifies the same declaration model, and any replacement engine must then implement those
  semantics under different type names. Accept the coupling and spend the effort on declaration
  ergonomics instead of on a second vocabulary.
- **Per-module custom adapters are a pressure valve, not a design centre.** Replacing one module's
  persistence implementation is a reasonable escape hatch; used at scale it reinstates the per-module
  × per-provider multiplication this program goal exists to avoid.

## Standing risks

- **Declaration ergonomics becomes a release blocker.** With EF Core removed, authoring a storage
  manifest is the only way to add a store, and there is no fallback for an author who finds it too
  heavy. There is currently no fluent builder or attribute/source-generator layer over
  `StorageManifest`/`StorageUnit`; `samples/Groundwork.SupportTickets/SupportTicketManifest.cs` is
  125 lines for one document kind.
- **Single-point-of-failure risk rises.** Groundwork stops being one of two options and becomes the
  only persistence implementation. Independent repository and versioning (objective 10) is the
  mitigation, as is resisting provider-set growth beyond the four supported today.
- **Consumer-neutrality remains notional.** ADR 0004 §4 defers shared operational primitives until a
  second consumer exists. With one consumer, Groundwork will continue to be shaped by that consumer's
  needs. Provider-neutrality is real; consumer-neutrality is not yet, and generality nobody has asked
  for should not be built on the assumption that it is.

## Decision checkpoints

- Does a retained, digest-anchored evidence artifact compare EF Core and Groundwork on the same
  workloads, for each provider in scope? If not, EF Core is not yet removable.
- Does the runtime-evaluation matrix carry real p95/p99, concurrency, and restart-recovery numbers in
  place of its `BenchmarkGate` verdicts? If not, the hot-path seams keep their fallback.
- Are the `Obsolete`-marked types resolved, and can a manifest still describe two answers about query
  capability? If it can, consuming modules should not be pinned to that surface.
- Can a contributor declare a three-unit module store, materialize it on SQLite, and query it in
  under twenty lines without reading `CONTEXT.md`?
- Does the support matrix distinguish "passes conformance" from "production-supported," with one
  relational provider carrying a pilot and runbooks and MongoDB marked preview?
- Is there exactly one persistence implementation in the consumer's tree at GA?
