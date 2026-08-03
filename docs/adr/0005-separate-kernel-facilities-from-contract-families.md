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

### Where the kernel boundary already is

The document stack is not uniformly document-coupled. Measured at its public signatures, coupling is
concentrated at the top of the pipeline and disappears toward the bottom:

| Stage | Entry point | Contract coupling |
|---|---|---|
| Declaration → resolution | `PhysicalStorageResolver.Resolve(StorageManifest, IPhysicalNamePolicy, IProviderPhysicalNameNormalizer)` | **Document-coupled.** Takes a manifest of storage units. |
| Physical definition | `PhysicalTableDefinition` | **Partly.** Generic bones — `ProjectedColumns`, `Indexes`, `SchemaVersion`, `Evolution`. Document concepts — `Form`, `Envelope`, `SharedStorage`, `LinkedProjectionLogicalName`, `LinkedKey` — are present but nullable. |
| Route compilation | `ExecutableStorageRouteCompiler.Compile(ProviderPhysicalTableDefinition)` | Takes a definition, not a manifest. |
| Schema evolution | `PhysicalSchemaDiffPlanner.Plan(PhysicalSchemaTarget, PhysicalSchemaHistoryState, DateTimeOffset, …)` | **Agnostic at the signature, coupled in its types.** No manifest parameter, but `PhysicalSchemaTarget` requires a non-null `IReadOnlyList<ExecutableStorageRoute>` plus manifest identity and version, and `AppliedStorageRouteSnapshot` carries `DocumentIdentitySchemaState?`. |

Two consequences follow, and they pull in opposite directions.

First, the kernel is closer than "extract a kernel" suggests. The declaration model above
`PhysicalTableDefinition` is legitimately per-contract-family and should stay that way; everything
from schema targets downward is already shared machinery wearing document-stack packaging.
`PhysicalTableDefinition` itself is the seam: it needs splitting into a generic physical-object
definition (name, projected columns, indexes, schema version, evolution metadata) that the document
family extends with form, envelope, shared binding, and linked-key concepts.

Second, Core already contains the intended extension point, and it is closer to sufficient than the
parallel stack suggests. `ProviderPhysicalSchemaDefinition` carries an **opaque**
`canonicalDefinition` payload and computes its own fingerprint, under a doc comment that states the
split precisely: *"Core owns identity, fingerprinting, diffing, durable snapshots, and publication;
the named provider owns the canonical definition payload and its execution semantics."* That is
exactly the contract `DiagnosticRecordPhysicalSchemaState` reimplements — canonical serialization
plus fingerprinting of a definition Core does not interpret.

Third, and this is what blocks reuse today: **that extension point is structurally subordinate to
document routes.** `PhysicalSchemaTarget` rejects any provider definition that does not match an
executable storage route —

```csharp
if (ProviderDefinitions.Any(definition =>
        Routes.All(route => route.StorageUnit != definition.StorageUnit)))
    throw new ArgumentException(
        "Every provider physical-schema definition must belong to an executable storage route.", …);
```

— and additionally requires `StorageManifestIdentity`, `StorageManifestVersion`, and a non-null
route list. A diagnostic stream has none of these. Provider definitions are therefore annotations on
document routes, not first-class subjects, and diagnostics could not have consumed
`Core.SchemaEvolution` without synthesizing a document route and manifest identity for a
non-document stream.

The parallel stack was, on this evidence, **not** avoidable by discipline alone. The minimum useful
generalization is correspondingly well-defined: lift `ProviderPhysicalSchemaDefinition` from an
annotation on an `ExecutableStorageRoute` to a first-class schema subject, and admit a schema target
whose subjects are provider definitions rather than routes.

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

There are two seams, not one.

**Seam A — `PhysicalTableDefinition`.** Above it, declaration models, intent, and resolution from a
declaration to a physical definition are per-contract-family; `PhysicalStorageResolver.Resolve`
therefore stays with the document family. What generalizes is the definition it produces.

**Seam B — the schema subject.** `ProviderPhysicalSchemaDefinition` is already the right shape (an
opaque canonical payload with Core-owned fingerprinting) but is admitted only as an annotation on an
`ExecutableStorageRoute`. It must become a first-class schema subject, with `PhysicalSchemaTarget`
admitting targets whose subjects are provider definitions rather than document routes, and
`AppliedStorageRouteSnapshot`'s `DocumentIdentitySchemaState` moving to the document family's
extension.

Seam B is the smaller change and unblocks diagnostics on its own; Seam A is the larger one and is
what lets a contract family reuse physicalization rather than only schema evolution.

Kernel facilities are:

- a generic physical-object definition — logical name, projected columns, indexes, schema version,
  evolution metadata — split out of `PhysicalTableDefinition`, which retains form, envelope, shared
  binding, and linked-key concepts as the document family's extension of it;
- host naming policy, provider identifier normalization, and deterministic fingerprinting;
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

1. Open **seam B** — make `ProviderPhysicalSchemaDefinition` a first-class schema subject — and
   immediately prove it by replacing `DiagnosticRecordPhysicalSchemaState` with
   `Core.SchemaEvolution` in place. This is the smallest change that produces evidence: it is
   confined to the schema-evolution types, it has one obvious consumer waiting, and success or
   failure is legible before any larger commitment. Open **seam A** and close the §3 MongoDB gap
   only after seam B lands.
2. Refactor the rest of `Groundwork.DiagnosticRecords` onto the kernel **in place**, still in this
   repository, with no cross-repository coordination while sufficiency is still being learned. The
   reduction in diagnostics line count is the measure of whether the extraction succeeded.
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
