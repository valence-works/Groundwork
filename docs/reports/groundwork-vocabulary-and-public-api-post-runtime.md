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

1. **The shipped surface.** Every public type declaration under `src/Groundwork` — 623 across 12
   packages when this review opened, 610 after the removals below — plus the 156 distinct
   diagnostic codes, the CLI terms, and the capability identifiers they emit.
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
  `Bounded*` — declaration, query, mutation, grouped reduction. `Closed*` survives in
  `ClosedQueryCapabilityModel`, `ClosedQueryNativeSupport`, `ClosedQueryIndexResolver`,
  `ClosedQueryIndexSupport`, `ClosedQuerySupportResult`, and `StorageUnitClosedQuerySupport`. Every
  one of them is reached only from `RelationalDocumentStore` and `MongoDbDocumentStore` — the legacy
  stores — so the family retires with the legacy lane rather than needing its own decision.
- **`Portable` is now three unrelated jobs in one word**, and one of them is *not* legacy:
  `PortableQueryOperation` is the live operator enum that `BoundedQueryDeclaration` consumes. #28
  assumed "portable" would retire with the legacy query family; it cannot, because the live bounded
  contract depends on a type carrying that prefix.
- **"Materialization" acquired a second, unrelated meaning after #28.** It now denotes both
  storage preparation (the former compatibility package, `Core.Materialization`, `Core.SchemaEvolution`)
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
- **The diagnostic-code area vocabulary was never ratified.** 156 distinct codes are spread over
  seventeen `GW-<AREA>-NNN` families, including provider-specific (`GW-MONGO-ROUTE`), legacy
  (`GW-MAT`, `GW-PHYSICAL-LEGACY`), and near-duplicate (`GW-SCHEMA` vs `GW-RELATIONAL-SCHEMA`) areas.
  The compiler-facing `GW0001`–`GW0004` transition ids are a separate, correctly scoped family.

## Inventory of the shipped surface

### Package boundaries

| Package | Role after runtime delivery | Assessment |
|---|---|---|
| `Groundwork.Core` | Manifests, capabilities, physical storage, schema evolution, identity, scoping, text. | Keep, but ADR 0005 seam A splits the generic physical-object definition out of `PhysicalTableDefinition`. That seam is sequenced after seam B and has no spec yet. |
| `Groundwork.Documents` | Document contract family: stores, queries, mutations, unit of work, scoping. | Keep. This is a contract family in ADR 0005 terms, not a kernel facility. |
| Former compatibility materialization package | Compatibility plan/planner for the legacy document lane only. | Retire with the legacy lane. It is not a package boundary anyone should learn. |
| `Groundwork.Relational` | Shared relational document store, physical storage, planning, legacy physicalization naming. | Keep, minus the legacy planning/physicalization members. |
| `Groundwork.Provider.Relational` | Session, executor, command, and unit-of-work primitives. | Keep. This is a genuine kernel facility and the boundary reads correctly. |
| `Groundwork.Sqlite` / `.SqlServer` / `.PostgreSql` / `.MongoDb` | Provider runtimes for both the document and diagnostic families. | Keep. |
| `Groundwork.DiagnosticRecords` / `.Relational` | The second contract family. | Keep; ADR 0005 governs its convergence onto kernel facilities. |
| `Groundwork.SchemaTool` | The provider-neutral CLI. | Keep. Its command vocabulary matches ADR 0003 §6 exactly. |

The former compatibility materialization package was the only package whose name promised a concept
that was no longer the authority for that concept. `Core.SchemaEvolution` is the authority.

### Capability names

`WellKnownCapabilities` declares exactly one built-in capability,
`groundwork.operational.atomic-commit`. Its `operational` segment predates ADR 0004 and is retained
deliberately: capability ids reach executable-route fingerprints, so renaming one reads as physical
schema drift. **This exception is ratified.** It is documented at the declaration and must stay
documented; it is identifier stability, not vocabulary.

`operational` more generally is **not** a residue to clean up. ADR 0004 retired the
`Groundwork.Operational` *packages* while keeping the capability vocabulary in full, naming
`StorageIntent.Operational` among the members it retains. The word denotes a consumer's declared
workload demand, which is computed into provider fit — a live concept with a live meaning. This
review confirms that reading and adds nothing to it.

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
sample switch, and the `README` quickstart. Every survival is either the legacy lane itself or
documentation of it. **Decision:**
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
definition that belongs to no route. ADR 0005 seam B and [spec 023](../../specs/023-groundwork-schema-subject-seam)
own relaxing that; the word itself is fine.

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
envelope, shared binding, linked key) keeps the "table" word. That is ADR 0005 seam A, which the ADR
sequences after seam B and which has no spec yet; this review confirms only that the word is right
for what remains.

### `materialization`

**Reconciled here for the first time.** #28 could not have seen this; the second use did not exist
yet. The shipped surface now uses one word for two unrelated things:

| Use | Meaning | Examples |
|---|---|---|
| Storage preparation | Making provider storage ready for a manifest (CONTEXT.md's definition) | the former compatibility materialization package, `MaterializationPlan`, `MaterializationOperationKind`, `IProviderMaterializationOperation`, `*GroundworkMaterializer` |
| Relationship durability | Making a cross-unit reference durable and fenced at write time | `PhysicalRelationshipMaterializationSchema`, `PhysicalRelationshipMaterializationIdentity`, `RelationshipMaterializationGeneration`, `PhysicalRelationshipSidecarField.MaterializationGeneration` |

The second use arrived with #141, after #28 closed. It is not a sub-case of the first: it names a
runtime write-path structure, not a schema-preparation step, and it is maintained per write rather
than per deployment.

**Decision:** `materialization` is reserved for storage preparation, as CONTEXT.md already defines it.
The relationship family is renamed around **relationship reference storage** — a generated,
runtime-maintained structure that makes a declared cross-unit reference queryable and fenceable. Its
generation counter becomes a **reference fence generation**. Renaming is source-breaking on a surface
with no external consumer, so it is tracked rather than folded into this slice, and because a rename
that touches persisted schema hashes deserves its own evidence rather than riding along with
unrelated removals.

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
| `Groundwork.Core.Materialization.MaterializationPlan` / `MaterializationOperation` / `SchemaHistoryEntry` | Duplicate records shadowing the former compatibility package's names with different shapes; zero references anywhere including their own package. | **Removed in this slice.** The file now holds only the genuinely shared `MaterializationOperationKind` and `IProviderMaterializationOperation` seam and is named for it. |
| `RelationalServerPhysicalSchemaDialect.Q` and `RelationalPhysicalDocumentStore.P` | Single-letter identifier-quoting and parameter members — one public abstract, one internal and reached from four files — asymmetric with `RelationalPhysicalDocumentDialect.QuoteIdentifier`, which already spelled its name out. This is the deferred #47 finding recorded on #63. | **Renamed in this slice** to `QuoteIdentifier` and `Parameter`, together with every private `Q` helper in providers, tests, and benchmarks. The two local aliases that became same-name shadows are inlined to the `dialect.` calls the rest of their file already used. |
| `DocumentPlan` / `RelationalPlan` | Forwarding facades whose only members re-expose `MaterializationPlan`'s. Zero consumer usage. | Track. They retire with the legacy lane rather than separately. |
| `RelationalPhysicalizationNames` | Public, unmarked, and hard-codes PostgreSQL's 63-character identifier cap for every relational provider. | Track. |
| `PhysicalizationNameEncoder`, `RelationalPhysicalizationValues` | Public legacy helpers with no obsolete marker. | Track with the above. |
| `StorageUnit` positional constructor | Requires three obsolete parameter types that current callers satisfy with empty/`Portable` placeholders. | Track. It is the last GW0001–GW0003 dependency that new code cannot avoid. |
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
| **Executable storage route** | Unchanged from CONTEXT.md, with the added invariant that provider physical-schema definitions are subordinate to routes on both the desired and applied sides. | Invariant made explicit; ADR 0005 seam B owns relaxing it. |
| **Physicalization** | Legacy only. Never in new API, including naming and value helpers. | Tightened: #28 deprecated the declarations, this ratifies the helpers as legacy too. |
| **`groundwork.operational.*` capability ids** | Frozen identifiers. ADR 0004 keeps the operational capability vocabulary; the id is additionally frozen because capability ids reach executable-route fingerprints. | New ratified exception, and a correction: `operational` is live vocabulary, not a residue. |

## Bridge sequencing

PR #154's `0.0.1-preview.100` family compiles the consumer while emitting GW0001–GW0004 across live
bridge surfaces. Per the consumer coordination note, those warnings are migration debt, not a reason
to suppress the diagnostics or add downstream aliases. This review ratifies that position and fixes
the sequence:

1. **Now (this slice).** Remove what has no consumer at all — the imperative migration pipeline and
   the duplicate Core materialization records — and rename the single-letter dialect and store
   members. No bridge surface is touched, and the renamed members are either unimplemented by any
   consumer or `internal`, so this slice forces no consumer recompilation.
2. **Warning stays warning** until the consumer's replacement work lands. Groundwork does not raise
   GW0001–GW0004 to errors while it is the only party that can see the whole migration.
3. **Warning to error** in one announced pre-1.0 release, after the consumer confirms the replacement
   declarations are in place. That release also removes the unmarked legacy helpers, so a consumer
   cannot silently reach legacy physicalization after the declarations are gone.
4. **Removal** of the legacy lane — declarations, the compatibility materialization package, the legacy document
   stores and factories, `DocumentPlan`/`RelationalPlan`, and the `ClosedQuery*` family — in the
   announced breaking release, with a migration table in the release notes.

Groundwork must not add compatibility aliases or forwarding facades at any step. The bridge is the
obsolete-marked declaration plus `LegacyPhysicalStorageBridge`; there is no second mechanism.

## Applied in this slice

- Removed `Groundwork.Core.Migrations` and `SqliteGroundworkMigrationExecutor` with their tests.
- Removed the unused duplicate `Groundwork.Core.Materialization` plan, operation, and schema-history
  records; the remaining shared kind enum and provider-operation interface now live in
  `ProviderMaterializationOperation.cs`.
- Renamed `RelationalServerPhysicalSchemaDialect.Q` to `QuoteIdentifier` and
  `RelationalPhysicalDocumentStore.P` to `Parameter`, together with every private `Q`/`P` helper in
  providers, tests, and benchmarks, closing the deferred #47 finding recorded on #63. Both renames
  are mechanical: every retained file is byte-identical to its previous content with the identifier
  substituted.
- Added the ratified terms to [`CONTEXT.md`](../../CONTEXT.md), and de-staled the `README.md`
  physical-storage section, which still said provider execution had not moved to resolved
  definitions.
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
| [#182](https://github.com/valence-works/Groundwork/issues/182) | Retire the unmarked legacy physicalization helpers and PostgreSQL's identifier cap; internalize the public MongoDB mechanics. |
| [#183](https://github.com/valence-works/Groundwork/issues/183) | Give `StorageUnit` a constructor that does not require obsolete declaration types. |
| [#184](https://github.com/valence-works/Groundwork/issues/184) | Ratify the `GW-<AREA>-NNN` diagnostic-code areas. |
| [#185](https://github.com/valence-works/Groundwork/issues/185) | Retire the legacy document lane in one announced breaking release. |

ADR 0005's kernel/contract-family seams are not owned by this review. Seam B — making
`ProviderPhysicalSchemaDefinition` a first-class schema subject and relaxing route subordination — is
[spec 023](../../specs/023-groundwork-schema-subject-seam). Seam A — splitting the generic
physical-object definition out of `PhysicalTableDefinition` — is sequenced after seam B and has no
spec yet.

## Out of scope

New persistence capabilities unrelated to API coherence, and any reopening of ADR 0003's storage-form
decision or ADR 0005's kernel/contract-family split.
