# Separate kernel facilities from contract families

Status: proposed (2026-08-02).

Date: 2026-08-02.

Related: [ADR 0004](0004-retire-groundwork-operational.md) (extends §3 and supersedes the
"revisit only with a second consumer" deferral in §4), [ADR 0003](0003-adopt-three-physical-storage-forms.md),
[Groundwork runtime evaluation](../reports/groundwork-runtime-evaluation.md),
[Diagnostic-records grouped reduction scope](../reports/diagnostic-records-grouped-reduction-scope.md),
[Open/closed capabilities](../reports/groundwork-open-closed-capabilities.md).

## Context

Groundwork is intended to be consumer-neutral: a provider-neutral persistence foundation that any
application can adopt, not a component of one application. That intent is currently not achievable
by construction, and `Groundwork.DiagnosticRecords` is the proof.

### The evidence

`Groundwork.DiagnosticRecords` exists to serve one named consumer workload — Elsa Foundation's
`IStructuredLogStore` and `IOpenTelemetryStore`. Its own README says so. Measured against the tree:

| Layer | Lines |
|---|---|
| `Groundwork.DiagnosticRecords` (contract) | 4,001 |
| `Groundwork.DiagnosticRecords.Relational` (shared relational kernel) | 2,525 |
| SQLite / SQL Server / PostgreSQL / MongoDB diagnostic natives | 747 / 1,192 / 779 / 2,636 |
| **Total** | **11,880 of 64,102 `src` lines (18.5%)** |

Nearly a fifth of a consumer-neutral foundation is one consumer's two stores.

More telling than the size is the coupling. `Groundwork.DiagnosticRecords` imports exactly three
Core namespaces — `Core.Text` (shared string-comparison policies), `Core.SchemaEvolution`, and
`Core.Manifests`. It does not use `Core.PhysicalStorage`, `Core.Indexing`, `Core.Queries`, or
`Core.Capabilities`. It independently reimplements:

- a declaration model (stream definitions rather than manifests and storage units);
- physical layout selection and naming;
- a capability model (`IDiagnosticQueryHandler.Capabilities` rather than `ProviderCapabilityValidator`);
- bounded-query validation and admission;
- continuation and keyset semantics;
- an idempotency/operation ledger; and
- a provider conformance suite.

`Groundwork.DiagnosticRecords` is therefore not a module inside Groundwork. It is a second
persistence framework inside Groundwork, sharing string comparison with the first.

### Why this happened

Groundwork's generic machinery — physicalization, executable storage routes, schema evolution,
capability validation, bounded-query planning, provider conformance — is reachable only through
`IDocumentStore`. There is no extension point for a new contract family. A consumer needing
persistence semantics the document contract cannot honestly serve therefore has two options:

1. build a parallel stack, or
2. have the contract merged into Groundwork.

Neither is consumer ownership. [ADR 0004](0004-retire-groundwork-operational.md) §3 drew the right
boundary — "Groundwork owns primitives; consumers own protocols" — but the primitives a specialized
contract family actually needs are not exposed, so the boundary is unenforceable in the one case
where it matters most.

The `Groundwork.Modules.Inbox` sample demonstrates external module authoring, but at 329 lines
across module, provider, and tests, and SQLite only, it does not exercise the machinery a real
contract family needs.

### The observable failure

On 2026-07-31 the grouped-reduction contract was removed from Groundwork on the reasoning that no
consumer required it, then restored the same day after the consuming source was read directly.
Removal would have stopped `elsa-foundation` compiling. This is not a review lapse; it is the
predictable consequence of code whose justification lives in a different repository from the code.
A Groundwork maintainer cannot see Elsa, so "is this required?" is not answerable from inside
Groundwork, and any test that depends on answering it will keep failing.

## Decision

### 1. Divide the repository along kernel facilities and contract families

A **kernel facility** is reusable by any contract family and belongs to Groundwork. A **contract
family** defines a set of storage semantics and belongs to whoever needs it. Documents and
diagnostic records are both contract families; documents remains Groundwork's first-party one.

### 2. The kernel is the extension point, and must be sufficient to author a contract family outside core

Kernel facilities are:

- physical definitions, host naming policy, provider identifier normalization, and deterministic
  fingerprinting (`PhysicalStorageResolver`, `PhysicalTableDefinition` — already contract-agnostic);
- provider-neutral schema evolution: additive diffs, durable applied state, compare-and-swap,
  locking, and safe/destructive authorization;
- per-provider session and connection lifecycle (`Groundwork.Provider.Relational` today);
- the open/closed capability registry (`CapabilityId`, `CapabilityDescriptor`, `IGroundworkModule`,
  `GroundworkModuleCatalog`, `ProviderCapabilityValidator`);
- a durable operation-ledger and idempotency primitive;
- bounded-query compilation and handler certification, parameterized over contract family rather
  than bound to documents; and
- the reusable provider conformance-suite pattern.

A facility is kernel only if a second contract family can use it without modification.

### 3. Close the MongoDB substrate gap

`Groundwork.Provider.Relational` exists; there is no MongoDB equivalent. Consequently a contract
family that must reach MongoDB cannot currently be authored outside Groundwork at all, which is a
sufficient explanation on its own for why diagnostics was not. Kernel parity across the supported
provider families is a precondition of this ADR, not a follow-up.

### 4. Replace the retirement test

[ADR 0004](0004-retire-groundwork-operational.md) §4 deferred shared operational primitives "until a
second consumer needs them." That test cannot be evaluated from inside Groundwork and, applied to
diagnostics, produced a near-deletion of shipped, load-bearing behaviour.

The test becomes: **is this a kernel facility or a contract family?** Kernel facilities are justified
by being usable by more than one contract family — a property provable inside this repository.
Contract families are justified by their owner, and a contract family whose owner is a single
external consumer belongs to that consumer.

### 5. Diagnostic records becomes an externally owned contract family, in three ordered steps

1. Extract the kernel facilities in §2 and close the §3 gap.
2. Refactor `Groundwork.DiagnosticRecords` onto the kernel **in place**, still in this repository.
   This is where kernel sufficiency is proven, with no cross-repository coordination while it is
   still being learned. The reduction in diagnostics line count is the measure of whether the
   extraction succeeded.
3. Move the remaining contract family to its consumer.

Each step is independently shippable. Step 3 must not begin before step 2 demonstrates sufficiency;
moving a parallel stack merely relocates it, and leaves the consumer owning a second framework.

### 6. Retain the conformance suite as a kernel facility

Diagnostic records is the most demanding workload this repository has. When the contract family
leaves, the conformance-suite pattern and the provider substrate it exercises stay, and the external
family runs them. The kernel must not lose its hardest test along with the code.

## Alternatives considered

### Leave diagnostic records in Groundwork and drop the consumer-neutrality claim

Defensible on the merits — diagnostics genuinely requires per-provider execution, so by ADR 0004's
own composability test it is correctly placed, unlike the operational contracts. It was rejected
because consumer-neutrality is a product requirement for Groundwork, not a description to be revised
to match the code.

### Move diagnostic records to its consumer immediately

Rejected. Without §2, the consumer inherits a parallel framework rather than a contract family built
on kernel facilities, including 2,636 lines of MongoDB-native execution with no substrate beneath
it. This is worse for the consumer than the status quo and proves nothing about neutrality.

### Generalize the diagnostic-record contract so it serves more consumers

Rejected as speculative generality, the same failure ADR 0004 corrected. The contract's shape is
correct for its workload; the problem is ownership and the absence of an extension point, not the
contract.

### Keep the "second consumer" test and document the workload more carefully

Rejected as insufficient. Better records reduce the chance of the failure recurring but leave the
test unanswerable from inside the repository. The grouped-reduction review named its own gap
accurately and still reached the wrong conclusion.

## Consequences

- Groundwork gains a real extension point, and consumer-neutrality becomes demonstrable — two
  contract families on one kernel, one of them external — rather than asserted.
- The consuming repository takes ownership of the diagnostics contract family, including its
  provider-native execution. This is a deliberate transfer and should be accepted explicitly by that
  repository before step 3.
- Kernel extraction work competes with in-flight consumer migration work. Diagnostics is a live
  logging path carrying a stated p95 gate relative to EF Core; this ADR's steps must not run
  concurrently with that consumer's EF Core removal, which would destabilize the measured subject
  while removing the instrument that measures it. See
  [Elsa EF Core removal strategy](../reports/elsa-ef-core-removal-strategy.md).
- The vocabulary and public API reconciliation gains a constraint: kernel surfaces must be nameable
  and usable without document-contract vocabulary.
- `Groundwork.Modules.Inbox` remains a valid but shallow sample. Kernel sufficiency is proven by
  step 2, not by the sample.
