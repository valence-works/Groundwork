# Groundwork vocabulary and public API: post-runtime reconciliation

Status: ratified pre-1.0 vocabulary decision under accepted [ADR 0003](../adr/0003-adopt-three-physical-storage-forms.md)
and [ADR 0005](../adr/0005-separate-kernel-facilities-from-contract-families.md). This report does not
reopen either governing decision.

Date: 2026-08-12.

Tracking: [PRD #25](https://github.com/valence-works/Groundwork/issues/25) and
[issue #63](https://github.com/valence-works/Groundwork/issues/63).

Supersedes the forward-looking half of the pre-runtime
[vocabulary and public API reconciliation](groundwork-vocabulary-and-public-api.md) (issue #28). That
report remains the record of what the surface looked like before the physical-storage runtime existed;
where the two disagree, this one governs.

## Purpose

Issue #28 named a target vocabulary before any of ADR 0003's three physical storage forms had a
provider runtime. Since then the physical-storage kernel (#43, #44), all four provider runtimes
(#46, #47, #48), the migration CLI (#49), bulk lifecycle mutations (#51), bounded grouped reduction
(#130), collection-element projections (#128), and fenced cross-unit relationships (#141) have all
shipped and been exercised by a real external consumer.

This report re-inventories the *shipped* surface against that experience, reconciles the terms #28
listed as unsettled, ratifies the vocabulary that survives, and gives every remaining source-breaking
cleanup either an applied change in this slice or a tracked successor.

## Method and evidence

Three evidence sources, all reproducible:

1. **The shipped surface.** Every public type declaration under `src/Groundwork` (623 declarations
   across 12 packages) plus the diagnostic-code families, CLI terms, and capability identifiers they
   emit.
2. **Consumer pressure.** The coordination notes on #63 from Elsa Foundation (#629/#646, #130/#131,
   #135/#136, #139, #140, #141, PR #154), plus a direct usage count of each candidate contract in the
   `elsa-foundation` tree. That count is what separates "remove now" from "sequence the bridge".
3. **The delivery record.** What #46–#51 and the follow-on issues actually had to name, which is the
   only test of a pre-runtime vocabulary that matters.

Consumer usage, counted by files referencing the type:

| Contract | Consumer files | Consequence |
|---|---|---|
| `GroundworkMigration` family | 0 | Removable now. |
| `MaterializationPlan` (either namespace) | 0 | Removable once the internal legacy lane retires. |
| `DocumentPlan` / `RelationalPlan` | 0 | Removable now on consumer grounds; kept only by the internal legacy lane. |
| `RelationalServerPhysicalSchemaDialect` | 0 | Member renames are free. |
| `PortableDocumentQuery` | 42 | Bridge; sequence, do not remove. |
| `DocumentStoreQuery` | 37 | Bridge; sequence, do not remove. |
| `IndexDeclaration` | 29 | Bridge; sequence, do not remove. |
| `PhysicalizationPolicy` | 22 | Bridge; sequence, do not remove. |
| `PortableQueryDeclaration` | 7 | Bridge; sequence, do not remove. |

## Executive findings

- **ADR 0003's three forms needed no fourth value.** `PhysicalStorageForm` still has exactly
  `SharedDocuments`, `DedicatedDocumentTable`, and `PhysicalEntityTable` after four provider runtimes.
  Linked index storage, collection-element storage, and relationship sidecars all stayed *derived*
  structures rather than becoming forms. The form vocabulary is ratified unchanged.
- **`PhysicalTableDefinition` and the naming pipeline are confirmed.** The resolution order #28
  proposed shipped intact and survived MongoDB, which consumes it for collections without a rename.
  ADR 0005 independently located the kernel seam at the same type, which is corroboration.
- **"Bounded" won; "closed" and "portable" did not.** Every contract added since #45 is named
  `Bounded*` — declaration, query, mutation, grouped reduction. `Closed*` survives only in
  `ClosedQueryCapabilityModel`, `ClosedQueryNativeSupport`, `ClosedQueryIndexSupport`,
  `ClosedQueryIndexResolver`, and `StorageUnitClosedQuerySupport`, all serving the legacy lane.
- **`Portable` is now three unrelated jobs in one word**, and one of them is *not* legacy:
  `PortableQueryOperation` is the live operator enum that `BoundedQueryDeclaration` consumes. #28
  assumed "portable" would retire with the legacy query family; it cannot, because the live bounded
  contract depends on a type carrying that prefix.
- **"Materialization" acquired a second, unrelated meaning after #28.** It now denotes both
  storage preparation (`Groundwork.Materialization`, `Core.Materialization`, `Core.SchemaEvolution`)
  and runtime relationship-reference durability (`PhysicalRelationshipMaterializationSchema`,
  `MaterializationGeneration`, `RelationshipMaterializationGeneration`). This collision is new and is
  the sharpest naming defect in the shipped surface.
- **"Sidecar" was re-adopted after #28 retired it.** #28 used "sidecar" only to name what was being
  replaced; #141 then shipped `PhysicalRelationshipSidecarField` and
  `PhysicalRelationshipSidecarAccessPath` as canonical public names.
- **"Physicalization" is only half-deprecated.** The declaration types carry GW0001/GW0002/GW0004,
  but `PhysicalizationNameEncoder`, `RelationalPhysicalizationNames`, `RelationalPhysicalizationValues`,
  and two namespaces carry no obsolete marker at all.
- **The imperative migration pipeline is dead and was removed in this slice.** ADR 0003 §6 and #28
  both required convergence on one plan; `Core.SchemaEvolution` plus the schema tool now own the
  semantic-migration lifecycle end to end, and the old family had no consumer.
- **One provider's identifier limit still defines every relational provider.** #28 flagged
  `RelationalPhysicalizationNames`' hard-coded 63-character cap as PostgreSQL leakage; it is still
  public, still capped, and still not obsolete-marked.
- **The diagnostic-code area vocabulary was never ratified.** Eighteen `GW-<AREA>-NNN` families are in
  use, including provider-specific (`GW-MONGO-ROUTE`), legacy (`GW-MAT`, `GW-PHYSICAL-LEGACY`), and
  near-duplicate (`GW-SCHEMA` vs `GW-RELATIONAL-SCHEMA`) areas.

## Inventory of the shipped surface

### Package boundaries

| Package | Role after runtime delivery | Assessment |
|---|---|---|
| `Groundwork.Core` | Manifests, capabilities, physical storage, schema evolution, identity, scoping, text. | Keep, but ADR 0005 splits the generic physical-object definition out of `PhysicalTableDefinition`; spec 023 owns that seam. |
| `Groundwork.Documents` | Document contract family: stores, queries, mutations, unit of work, scoping. | Keep. This is a contract family in ADR 0005 terms, not a kernel facility. |
| `Groundwork.Materialization` | Compatibility plan/planner for the legacy document lane only. | Retire with the legacy lane. It is not a package boundary anyone should learn. |
| `Groundwork.Relational` | Shared relational document store, physical storage, planning, legacy physicalization naming. | Keep, minus the legacy planning/physicalization members. |
| `Groundwork.Provider.Relational` | Session, executor, command, and unit-of-work primitives. | Keep. This is a genuine kernel facility and the boundary reads correctly. |
| `Groundwork.Sqlite` / `.SqlServer` / `.PostgreSql` / `.MongoDb` | Provider runtimes for both the document and diagnostic families. | Keep. |
| `Groundwork.DiagnosticRecords` / `.Relational` | The second contract family. | Keep; ADR 0005 governs its convergence onto kernel facilities. |
| `Groundwork.SchemaTool` | The provider-neutral CLI. | Keep. Its command vocabulary matches ADR 0003 §6 exactly. |

`Groundwork.Materialization` is the only package whose name promises a concept that is no longer the
authority for that concept. `Core.SchemaEvolution` is.

### Capability names

`WellKnownCapabilities` declares exactly one built-in capability,
`groundwork.operational.atomic-commit`. Its `operational` segment predates ADR 0004's retirement of
`Groundwork.Operational` and is retained deliberately: capability ids reach executable-route
fingerprints, so renaming one reads as physical schema drift. **This exception is ratified.** It is
documented at the declaration and must stay documented; it is identifier stability, not vocabulary.

`StorageIntent.Operational(...)` carries the same retired word with none of the stability
justification — it is an ordinary factory method whose name no longer denotes anything.

### Transition diagnostics

| Id | Covers | Bridge state |
|---|---|---|
| `GW0001` | `PhysicalizationPolicy`, `PhysicalizationKind` | Live in consumer (22 files). |
| `GW0002` | `IndexDeclaration`, `IndexPhysicalizationPolicy` | Live in consumer (29 files). |
| `GW0003` | `PortableQueryDeclaration` | Live in consumer (7 files). |
| `GW0004` | `DocumentStoreQuery`, `PortableDocumentQuery`, `PhysicalizationProjection`, `PhysicalizedFieldPlan` | Live in consumer (43 files reference one or both query types). |

The four ids are correctly scoped and their replacement guidance is accurate. The gap is that they
cover *declarations only*: the naming and value helpers that implement the same legacy concept
(`PhysicalizationNameEncoder`, `RelationalPhysicalizationNames`, `RelationalPhysicalizationValues`)
carry no marker, so a consumer can reach legacy physicalization without a warning.

### CLI terms

`plan | validate | status | apply`, with `--safe`, `--offline`, `--expected-plan`,
`--allow-destructive`, `--allow-semantic`, and exit codes `0/2/3/4/5/10/130`. This is a faithful
rendering of ADR 0003 §6's `ValidateOnly` / `ApplySafe` / `ApplyAuthorized` modes into operator
vocabulary, and the "migrations" framing ADR 0003 permitted for operator familiarity was correctly
*not* taken: the tool says "schema", and there is one plan and one executor lifecycle underneath.
**No CLI change is required.**

## Reconciling the terms #63 named

### `optimized`

Retired as a contract promise, as #28 required — but not yet absent. It survives in
`MaterializationOperationKind.CreateOptimizedProjection`, `CreateOptimizedProjectionOperation`,
`IndexPhysicalizationPolicy.Optimized`, `PhysicalizationKind.Optimized`, the `Groundwork__Physicalization=Optimized`
sample switch, and the `README` quickstart. Every survival is inside the legacy lane. **Decision:**
no new use; removal is bound to the legacy lane's retirement, not tracked separately.

### `projection`

Confirmed as correct for derived fields, exactly as #28 scoped it. `ProjectedColumnDefinition`,
`ExecutableProjectedColumnRoute`, `ProjectionRebuildMode`, `ProjectionCardinality`, and
`AddProjectedColumnOperation` all use it for rebuildable derived values and nothing else. **Decision:**
ratified unchanged.

### `physicalization`

Retired as a concept name. Two namespaces (`Groundwork.Core.Physicalization`,
`Groundwork.Relational.Physicalization`) and three public helpers still carry it without obsolete
markers. **Decision:** the word is legacy-only; the unmarked helpers are a defect and are tracked.

### `route`

Confirmed and load-bearing. `ExecutableStorageRoute` is the compiled provider-neutral mapping, and
the runtime added `ExecutableQueryPathRoute`, `ExecutableCollectionElementStorageRoute`,
`ExecutableMaintenanceRoute`, and friends without straining the word. **Decision:** ratified. One
qualification the runtime forced and #28 did not anticipate: routes are also a *subordination*
invariant — `PhysicalSchemaTarget` and `PhysicalSchemaAppliedSnapshot` both reject a provider
definition that belongs to no route. ADR 0005 and spec 023 own relaxing that; the word itself is fine.

### `storage unit`

Confirmed. It survived as the logical document kind through all three forms and both write paths.
**Decision:** ratified. The defect is not the term but `StorageUnit`'s shape: its positional record
constructor still requires `IndexDeclaration`, `PortableQueryDeclaration`, and `PhysicalizationPolicy`
values that current code passes as `[]`, `[]`, and `Portable`. `StorageUnit.Create` is the honest
constructor; the positional one is a bridge artefact.

### `table definition`

Confirmed, including for MongoDB. **Decision:** ratified for the document contract family. Under
ADR 0005 the generic half (name, projected columns, indexes, schema version, evolution metadata)
becomes a contract-family-neutral physical-object definition and only the document half (form,
envelope, shared binding, linked key) keeps the "table" word. Spec 023 owns that split; this review
confirms the word is right for what remains.

### `materialization`

**Not reconciled — this is the finding that most needs a decision.** The shipped surface uses one word
for two unrelated things:

| Use | Meaning | Examples |
|---|---|---|
| Storage preparation | Making provider storage ready for a manifest (CONTEXT.md's definition) | `Groundwork.Materialization`, `MaterializationPlan`, `MaterializationOperationKind`, `IProviderMaterializationOperation`, `*GroundworkMaterializer` |
| Relationship durability | Making a cross-unit reference durable and fenced at write time | `PhysicalRelationshipMaterializationSchema`, `PhysicalRelationshipMaterializationIdentity`, `RelationshipMaterializationGeneration`, `PhysicalRelationshipSidecarField.MaterializationGeneration` |

The second use arrived with #141, after #28 closed. It is not a sub-case of the first: it names a
runtime write-path structure, not a schema-preparation step, and it is maintained per write rather
than per deployment.

**Decision:** `materialization` is reserved for storage preparation, as CONTEXT.md already defines it.
The relationship family is renamed around **relationship reference storage** — a generated,
runtime-maintained structure that makes a declared cross-unit reference queryable and fenceable. Its
generation counter becomes a **reference fence generation**. Renaming is source-breaking on a surface
with no external consumer, so it is tracked rather than folded into this slice, which is already
carrying three removals.

### `sidecar`

**Decision:** retired, again, and this time the retirement covers the shipped surface rather than only
the aspiration. `linked` is the ratified word for a derived structure that stores keys plus a document
reference (`LinkedDocumentKeyDefinition`, `CreateLinkedStorageOperation`, `ExecutableLinkedRelationshipRoute`,
`LinkedIndexStorage`). `PhysicalRelationshipSidecarField` and `PhysicalRelationshipSidecarAccessPath`
are the only public exceptions and are renamed with the relationship family above.

## Shallow, duplicative, and leaking APIs

| Surface | Defect | Disposition |
|---|---|---|
| `Groundwork.Core.Migrations.*` (`IGroundworkMigration`, `GroundworkMigration`, `GroundworkMigrationOperation(Kind)`, `GroundworkMigrationRunner`, `IGroundworkMigrationExecutor`, `GroundworkMigrationExecutionOptions`, `GroundworkMigrationResult`, `GroundworkMigrationRecord`) and `SqliteGroundworkMigrationExecutor` | A second, imperative schema-evolution pipeline with its own ordering, executor, options, result, and ledger. No production wiring, no consumer. | **Removed in this slice.** |
| `Groundwork.Core.Materialization.MaterializationPlan` / `MaterializationOperation` / `SchemaHistoryEntry` | Duplicate records shadowing the active `Groundwork.Materialization` names with different shapes; zero references anywhere including their own package. | **Removed in this slice.** The file now holds only the genuinely shared `MaterializationOperationKind` and `IProviderMaterializationOperation` seam and is named for it. |
| `RelationalServerPhysicalSchemaDialect.Q` | A public abstract single-letter member, asymmetric with `RelationalPhysicalDocumentDialect.QuoteIdentifier`, which already used the explicit name. | **Renamed in this slice** to `QuoteIdentifier`, together with every private `Q` helper in providers, tests, and benchmarks. |
| `DocumentPlan` / `RelationalPlan` | Forwarding facades whose only members re-expose `MaterializationPlan`'s. Zero consumer usage. | Track. They retire with the legacy lane rather than separately. |
| `RelationalPhysicalizationNames` | Public, unmarked, and hard-codes PostgreSQL's 63-character identifier cap for every relational provider. | Track. |
| `PhysicalizationNameEncoder`, `RelationalPhysicalizationValues` | Public legacy helpers with no obsolete marker. | Track with the above. |
| `StorageUnit` positional constructor | Requires three obsolete parameter types that current callers satisfy with empty/`Portable` placeholders. | Track. It is the last GW0001–GW0003 dependency that new code cannot avoid. |
| `StorageIntent.Operational` | Retired ADR 0004 vocabulary with no identifier-stability justification. | Track. |
| `MongoDbPhysicalStorageModel`, `MongoDbGroundworkNames`, `MongoDbDiagnosticRecordNames` | Provider mechanics on the public surface. #28 asked for these to become internal. | Track. |
| `GW-MONGO-ROUTE-###`, `GW-MAT-###`, `GW-PHYSICAL-LEGACY-###`, `GW-RELATIONAL-SCHEMA-###` | Unratified diagnostic-code areas: one provider-specific, two legacy, one near-duplicate of `GW-SCHEMA`. | Track. |
| `ClosedQuery*` family | Competes with `BoundedQuery*` for the same concept. | Track with the legacy lane. |

## Ratified vocabulary delta

The canonical vocabulary table in the [#28 report](groundwork-vocabulary-and-public-api.md#canonical-vocabulary)
stands, with these post-runtime amendments.

| Term | Ratified meaning | Change from #28 |
|---|---|---|
| **Bounded** | The operative adjective for every declared, closed contract: queries, mutations, grouped reductions. | Promoted from "bounded query" to the general adjective. `Closed` is legacy. |
| **Portable** | Provider-neutral, and nothing else. Valid in `PortablePhysicalType`, `PortableStringComparisonPolicy`, `ProviderNeutralityRules`. | #28 expected the word to retire with the legacy query family. It does not: `PortableQueryOperation` is live under `BoundedQueryDeclaration` and is renamed to `BoundedQueryOperation` rather than deprecated. |
| **Linked** | A derived structure holding keys plus a document reference. Not a storage form. | Unchanged, but now explicitly displaces `sidecar` on the shipped surface. |
| **Materialization** | Preparing provider storage for a manifest. Nothing else. | Newly contested; the relationship family surrenders the word. |
| **Relationship reference storage** | The generated, runtime-maintained structure that makes a declared cross-unit reference queryable and fenceable. | New. Replaces the relationship family's use of `materialization` and `sidecar`. |
| **Reference fence generation** | The monotonic generation that fences a relationship reference against a concurrent target change. | New. Replaces `MaterializationGeneration`. |
| **Executable storage route** | Unchanged from CONTEXT.md, with the added invariant that provider physical-schema definitions are subordinate to routes on both the desired and applied sides. | Invariant made explicit; ADR 0005 owns relaxing it. |
| **Physicalization** | Legacy only. Never in new API, including naming and value helpers. | Tightened: #28 deprecated the declarations, this ratifies the helpers as legacy too. |
| **`groundwork.operational.*` capability ids** | Frozen identifiers, not vocabulary. Retained for fingerprint stability under ADR 0004. | New ratified exception. |

## Bridge sequencing

PR #154's `0.0.1-preview.100` family compiles the consumer while emitting GW0001–GW0004 across live
bridge surfaces. Per the consumer coordination note, those warnings are migration debt, not a reason
to suppress the diagnostics or add downstream aliases. This review ratifies that position and fixes
the sequence:

1. **Now (this slice).** Remove what has no consumer at all: the imperative migration pipeline, the
   duplicate Core materialization records, and the terse dialect member. No bridge surface is touched,
   so no consumer recompilation is forced by this slice.
2. **Warning stays warning** until the consumer's replacement work lands. Groundwork does not raise
   GW0001–GW0004 to errors while it is the only party that can see the whole migration.
3. **Warning to error** in one announced pre-1.0 release, after the consumer confirms the replacement
   declarations are in place. That release also removes the unmarked legacy helpers, so a consumer
   cannot silently reach legacy physicalization after the declarations are gone.
4. **Removal** of the legacy lane — declarations, `Groundwork.Materialization`, the legacy document
   stores and factories, `DocumentPlan`/`RelationalPlan`, and the `ClosedQuery*` family — in the
   announced breaking release, with a migration table in the release notes.

Groundwork must not add compatibility aliases or forwarding facades at any step. The bridge is the
obsolete-marked declaration plus `LegacyPhysicalStorageBridge`; there is no second mechanism.

## Applied in this slice

- Removed `Groundwork.Core.Migrations` and `SqliteGroundworkMigrationExecutor` with their tests.
- Removed the unused duplicate `Groundwork.Core.Materialization` plan, operation, and schema-history
  records; the remaining shared kind enum and provider-operation interface now live in
  `ProviderMaterializationOperation.cs`.
- Renamed `RelationalServerPhysicalSchemaDialect.Q` to `QuoteIdentifier` and every private `Q` helper
  in providers, tests, and benchmarks.
- Added the ratified terms to [`CONTEXT.md`](../../CONTEXT.md).
- Cross-linked this report from [ADR 0003](../adr/0003-adopt-three-physical-storage-forms.md),
  [ADR 0002](../adr/0002-additive-index-backfill-in-materializer.md), the
  [#28 report](groundwork-vocabulary-and-public-api.md), and the
  [physical-storage readiness goal](../program-goals/physical-storage-and-operations-readiness.md).

## Tracked successors

Every approved cleanup not applied above is filed against #25 and cross-linked from #63:

| Issue | Cleanup |
|---|---|
| [#180](https://github.com/valence-works/Groundwork/issues/180) | Rename the relationship family off `materialization` and `sidecar`. |
| [#181](https://github.com/valence-works/Groundwork/issues/181) | Rename `PortableQueryOperation` to `BoundedQueryOperation`; settle `Bounded` vs `Closed`. |
| [#182](https://github.com/valence-works/Groundwork/issues/182) | Retire the unmarked legacy physicalization helpers, PostgreSQL's identifier cap, `StorageIntent.Operational`, and the public MongoDB mechanics. |
| [#183](https://github.com/valence-works/Groundwork/issues/183) | Give `StorageUnit` a constructor that does not require obsolete declaration types. |
| [#184](https://github.com/valence-works/Groundwork/issues/184) | Ratify the `GW-<AREA>-NNN` diagnostic-code areas. |
| [#185](https://github.com/valence-works/Groundwork/issues/185) | Retire the legacy document lane in one announced breaking release. |

ADR 0005's kernel/contract-family seam — splitting the generic physical-object definition out of
`PhysicalTableDefinition` and relaxing route subordination — is owned by
[spec 023](../../specs/023-groundwork-schema-subject-seam), not by this review.

## Out of scope

New persistence capabilities unrelated to API coherence, and any reopening of ADR 0003's storage-form
decision or ADR 0005's kernel/contract-family split.
