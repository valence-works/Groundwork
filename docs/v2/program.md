# Groundwork v2 — program plan

> Status: approved 2026-08-14. This document is the durable source of truth for the v2
> program. Work items below are tracked as GitHub issues labelled `v2`; see the epic for the
> index. Where an issue and this document disagree, this document is authoritative on intent
> and the issue is authoritative on scope.

## Context

Groundwork v1 was built to a real requirement no product satisfies: one declaration materialized
correctly on SQLite, PostgreSQL, SQL Server and MongoDB, with cross-engine ambiguity refused at
compile time rather than averaged away. That property works and an external evaluation confirmed it
from outside the team.

But v1 made the document model mandatory rather than optional. ADR 0003 ratified that canonical JSON
is the source of truth and that *"a columns-only entity table is not part of this model"* — so there
is no way to declare a plain `Customer` table and insert rows. Everything pays for an envelope
(`document_kind`, `storage_scope`, `id`, `id_comparison_key` at 1350 bytes on SQL Server,
`id_lookup_key`, `version`, canonical JSON, timestamps), a string-plus-hash identity subsystem, and
an unconditional locking pre-read per write. `ExecutableStorageRoute.Envelope` is non-nullable and
providers dereference it in **263 places across 30 files**, so this is not reachable by refactor.

The consequences are measured: writes cost 4–11× a hand-written insert, an *n*-document commit costs
`2n + 2` serialized round trips, every page read issues an unconditional `COUNT(*)`, and a manifest
for an 11-column table runs to 301 lines in the largest real consumer
(`elsa-foundation/src/Elsa/Secrets/Persistence/Groundwork/SecretsStorageManifest.cs`).

**v2 inverts the layering.** The product is a provider-neutral *physical* store — declare a table or
collection with typed columns and indexes, insert and read rows directly. Documents become an
optional package on top. Queries are ordinary expressions verified against declared indexes rather
than declared one-by-one.

### Decisions already taken

| Decision | Consequence |
|---|---|
| Greenfield v2 in a new repo | v1 frozen at `a5fec7a`; no further v1 feature work |
| Elsa frozen on `0.0.1-preview.114` | Elsa gets no v1 fixes; migrates once, to v2 |
| `Groundwork.DiagnosticRecords` deleted | Its four *generic* capabilities absorbed into the kernel |
| Relationship/fence feature dropped | ~5,000 lines, fail-closed, zero consumers |
| Manifest declares tables/columns/indexes only | No `BoundedQueryDeclaration` |
| Portable predicate AST + LINQ front-end | LINQ lowers into the AST or fails the build |
| Build-time coverage error, runtime enforced | Analyzer is the early warning; runtime is the guarantee |

---

### What carries forward from v1

Reuse the *design*, and the code where it is clean:

- `Core/PhysicalStorage/PhysicalIndexNullExclusion.cs:71-97` — the cross-engine ambiguity refusal,
  including the implied-uniqueness exemption. **Port logic-for-logic; do not touch it.**
- `Core/PhysicalStorage/CompoundIndexOrdering.cs` — 63 lines encoding the whole prefix/sort-start
  rule. The coverage checker's core.
- `Core/PhysicalStorage/PhysicalQueryPlanCompiler.cs:711-888` — `ValidatePhysicalCompatibility` and
  `ResolveOrder`, repointed from validating a declaration to validating an expression.
- `Core/Text/PortableStringComparison.cs` — versioned collation-independent comparison and the
  boundary-delimited search key. **This is what makes `StartsWith` index-served on MongoDB.**
- `Core/Capabilities/*` — the open/closed capability registry, v1's best-designed subsystem.
- `Core/SchemaEvolution/*` — additive fingerprinted diff planning with CAS-recorded applied state.
- `Core/PhysicalStorage/PortableQueryOperationCompatibility.cs` — the per-type operator table.
- The relational dialect seam at `Relational/PhysicalStorage/RelationalServerPhysicalSchemaExecutor.cs:1542+`.

`docs/adr/0005-separate-kernel-facilities-from-contract-families.md` is effectively v2's charter and
should be restated as v2's ADR 0001. v2 supersedes ADR 0003.

---

## Tracking

Board: **[Groundwork v2](https://github.com/orgs/valence-works/projects/5)** (kanban).
Epic: **#228**. Work items: **#229–#274**, one milestone per phase.

The board maintains itself. `Status` is recomputed from GitHub state by
`.github/workflows/v2-board.yml` on every issue and pull-request event and hourly —
closed is Done, an open PR with a closing-keyword link is In review, an assignee is In
progress, all blockers closed is Ready, otherwise Blocked. Parent and dependency references
do not move unrelated cards. Dragging a card is pointless; change the underlying fact instead.
Blockers are parsed from each issue's `## Dependencies` section, so the dependency graph lives
in the issues and stays correct when one is edited.

Setup requirement: a `PROJECT_TOKEN` repository secret (PAT with `project` read/write and
`repo` scope). The default `GITHUB_TOKEN` cannot write to organisation projects, and the
workflow fails loudly if it is missing.

---

## Phase 0 — Decision gates

**Three throwaway probes. Each can invert the design. None of Phase 1+ starts until all three
report.** Run in parallel; ~2 weeks. Nothing built here ships.

| ID | Probe | Question it answers | Gate |
|---|---|---|---|
| **G1** | Schema and query visibility | Can a Roslyn analyzer see the schema and resolve real query sites? Build a throwaway analyzer reading a hand-written `[assembly: GroundworkSchema]` with one hard-coded rule. Point it at `samples/Groundwork.SupportTickets`, `samples/Groundwork.Modules.Inbox`, and Elsa's `SecretsStorageManifest` + `ElsaRuntimeStorageManifest`. | **≥90% of manifest declarations expressible declaratively; ≥95% of query sites statically resolvable with a `WhereIf` primitive available.** Below either → invert to runtime-first, analyzer advisory. |
| **G2** | Cross-provider differential semantics | Which of the portable-semantics claims are fiction? ~40 rows engineered around the edges (nulls, empty strings, Turkish dotted-I, German sharp-S, boundary decimals, epoch-adjacent dates, max-length strings) × ~300 predicate shapes, all four providers in containers, asserting **bit-identical result sets and order**. Reuse v1's container harness. | Every disagreement forces an explicit decision recorded in the semantics spec: **normalize and pay, or refuse to compile.** No silent divergence survives. |
| **G3** | MongoDB hostile cases | Does "real collections with typed columns" hold, or does Mongo quietly re-import an envelope? Declare `Customer` plus `Decimal(19,4)`, a unique index with `MissingValues.Excluded`, a two-column composite key, and a `ProviderSequence` column. Assert **cross-provider equivalence** against SQLite: same declaration, same observable outcome. | If Mongo needs something nobody else needs, it becomes a **declared, capability-gated feature with a `CapabilityId`** decided now — not an envelope discovered in month four. |

> **Why these three:** v1's Mongo provider is 11,698 lines against PostgreSQL's 1,841, because Mongo
> was made to carry the same envelope as everyone else. And v1's `Contains` renders as four
> *different functions* across providers (`ILIKE`, `LOWER(x) LIKE LOWER(@p)` twice with different
> `LOWER` semantics, and a `/i` regex) — so for non-ASCII input it already returns different rows on
> different providers. That is a portability bug behind a portable-looking API, and G2 is how we
> find the rest of them before freezing anything.

**Phase 0 also, non-blocking:** repo creation, CI with four provider containers, ADR 0001
(kernel/contract-family charter, restating v1 ADR 0005), ADR 0002 (superseding v1 ADR 0003).

---

## Phase 1 — Kernel

Foundation. `Groundwork.Kernel` has no async, no I/O, no provider types, and depends on nothing
outside the BCL.

| ID | Work item | Shape | Proof |
|---|---|---|---|
| **K1** | Declaration model: `ColumnDefinition`, `KeyDefinition`, `IndexDefinition`, `DerivedColumnDefinition`, `StorageUnit`. No `Form`, no `Envelope`, no `Path`, no `IdentityPolicy`. | cross-cutting | The 25-line `Customer` declaration compiles and validates |
| **K2** | Portability rules `GW-PORT-001`…`008` as a single validator, run by `Build()`, the manifest validator, and the schema compiler | cross-cutting | One red test per rule; GW-PORT-001 ported from `PhysicalIndexNullExclusion.cs` with its exemption intact |
| **K3** | Fluent builder + `RecordTable.For<T>()` typed front-end. Every policy defaults; nothing positional. | single-module | `Customer` in ≤12 builder lines / ≤9 typed lines |
| **K4** | Port capability registry, storage scope, identity generators, `PortableStringComparison` | mechanical | Byte-identical behaviour tests carried from v1 |
| **K5** | Schema evolution, subject-first (v1 ADR 0005 seam B / spec 023). Drop `RelationshipMaterializationTransition`, `DocumentStoreIdentitySchemaState`. | cross-cutting | A non-document schema subject plans and applies |
| **K6** | `Groundwork.Testing` conformance-suite skeleton, shipped as a package | single-module | Runs green against an in-memory reference provider |
| **K7** | **Architecture tests** — reference closure (no kernel/substrate assembly may reference a contract family), public-API vocabulary (no `Document`/`Envelope`/`Record`/`Stream` tokens in kernel signatures) | mechanical | Both fail loudly on a deliberate violation |

---

## Phase 2 — Substrates and providers

**MongoDB is built second, not last.** This is deliberate and is the single most important
sequencing decision in the plan — see G3.

| ID | Work item | Shape | Proof |
|---|---|---|---|
| **P1** | `Groundwork.Substrate.Mongo` + `Groundwork.MongoDb`: typed BSON row mapping, index models, native `_id` keys (composite → pinned-order sub-document) | cross-cutting | `Customer` produces a real collection; G3's hostile cases pass |
| **P2** | `Groundwork.Substrate.Relational`: dialect seam, generic DDL/DML emission. **Zero `InternalsVisibleTo` to non-test assemblies.** | cross-cutting | An out-of-tree provider stub compiles against public API only |
| **P3** | SQLite provider, incl. ported table-rebuild DDL | single-module | Conformance suite green |
| **P4** | PostgreSQL provider | single-module | Conformance suite green |
| **P5** | SQL Server provider, incl. index key-byte-budget validator | single-module | Conformance suite green |

---

## Phase 3 — Write path

Addresses v1 issues [#224](https://github.com/valence-works/groundwork/issues/224) and
[#225](https://github.com/valence-works/groundwork/issues/225) structurally.

| ID | Work item | Shape | Proof |
|---|---|---|---|
| **W1** | Single-statement conditional upsert per provider. PG/SQLite `ON CONFLICT … RETURNING`; SQL Server `UPDATE WITH (UPDLOCK, SERIALIZABLE) … OUTPUT` then conditional `INSERT` (**not `MERGE`**); Mongo `updateOne(upsert:true)`. `createdAt` preserved by *not writing it* — `DO UPDATE SET` omission / `$setOnInsert`. | cross-cutting | Round-trip counter: 1 write = 1 round trip, all four |
| **W2** | **Concurrency conformance harness** — N writers × M keys, asserting: every outcome is exactly one of {Inserted, Updated, ConcurrencyConflict}; insert count = distinct keys; final version = accepted-write count; `createdAt` = first accepted write. | cross-cutting | Runs on every provider on every PR |
| **W3** | Batched unit of work: group by `(unit, mode, column-set)`; PG multi-row `INSERT … ON CONFLICT`, SQL Server TVP, SQLite prepared-statement reuse, Mongo `BulkWrite`. Document coalescing and flush-on-staged-read ordering semantics. | cross-cutting | n=1000 commit ≤ `2 + ⌈n/1000⌉` round trips |
| **W4** | Opt-in concurrency: `ConcurrencyKind.None` means **no version column exists**; `ExpectedVersion` on such a unit is refused at validation, not ignored | single-module | A `None` unit's table has no version column |

> **W2 must exist before P4 lands.** Dropping the pre-read moves correctness from one shared code
> path into four provider dialects, and every failure mode is contention-only: SQL Server's
> UPDATE-then-INSERT races without exactly the right lock hints; PostgreSQL's `ON CONFLICT` does not
> fire on a *partial* index unless the inference clause matches (and `MissingValues.Excluded`
> produces partial indexes); Mongo's `MatchedCount` is ambiguous under a CAS filter. Single-threaded
> tests find none of this.

---

## Phase 4 — Query

Ordering is load-bearing: **AST, planner and providers must be complete and conformance-tested
before the LINQ layer exists**, so LINQ is provably a pure front-end and never becomes the place
semantics get decided.

The coverage checker must be `netstandard2.0` with no provider and no runtime dependency — a Roslyn
analyzer cannot reference a .NET 10 library. One implementation, three call sites (analyzer, store
open, executor). **If that library ever forks, the guarantee is dead.**

| ID | Work item | Shape | Proof |
|---|---|---|---|
| **Q1** | `Groundwork.Query.Model` (`netstandard2.0`): predicate AST, typed constants, canonical `ShapeFingerprint`. Free `And`/`Or`/`Not` at the surface, normalized to bounded CNF. | cross-cutting | G2's ~300 shapes round-trip |
| **Q2** | Portable semantics: **total booleans, no three-valued logic**. Per-leaf complement table. Mongo writes explicit `null` for every declared column (never `$exists`). | cross-cutting | Differential suite bit-identical on all four |
| **Q3** | `Groundwork.Query.Planning` (`netstandard2.0`): coverage checker ported from `PhysicalQueryPlanCompiler` + `CompoundIndexOrdering`, v1 tests repointed at AST inputs | cross-cutting | v1's plan-compiler test corpus green |
| **Q4** | Provider renderers: AST → SQL / Mongo filter | single-module | Differential suite green |
| **Q5** | Source generator: `[GwTable]`/`[GwColumn]`/`[GwIndex]` → runtime manifest **and** `[assembly: GroundworkSchema]`. The assembly attribute travels through metadata references, so cross-package schemas analyse without build wiring. | cross-cutting | A query in project B against a table declared in package A is analysed |
| **Q6** | Roslyn analyzer: coverage diagnostics naming *the index that would work*, `WhereIf` shape enumeration (2ⁿ, n≤6, **every** shape must be covered), dataflow fallback, `GW-COVER-900` on give-up | cross-cutting | The 3am case — both filters absent — fails the build |
| **Q7** | `AcceptScan(id, reason, owner, expiresOn)` as a **runtime AST value, not a compile-time suppression** — `#pragma` cannot forge it. Error when applied to a covered query. Opt-in per assembly. | single-module | Suppressed-but-unaccepted query still refused at runtime |
| **Q8** | LINQ front-end: closed `IGwQueryable<T>`, **not `IQueryable<T>`**. Allow-list lowering; `GW-LINQ-101`…`110` each naming the AST equivalent. `[GwQueryFragment]` for shared predicates. | cross-cutting | ~250-spelling conformance corpus locked in CI; **the docs table is generated from it** |
| **Q9** | Search-key columns → `StartsWith` index-served on all four. Stored as **ASCII text in a binary collation, never `BinData`** (BSON compares binary length-first, so prefix ranges are silently wrong). Only *folded* columns need the extra column. | cross-cutting | Mongo `StartsWith` is an `IXSCAN`, not a regex |
| **Q10** | Runtime enforcement: column drift → fail open; **index drift → fail only the affected query shapes**, not the process. Extra un-declared indexes ignored for coverage. | single-module | Rolling deploy with a missing index degrades one endpoint, not the app |
| **Q11** | Explain-assert test mode: fetch the provider's chosen plan, assert the proven index was used | single-module | Closes the gap between *can* serve and *did* serve |

> **Q10's asymmetry is deliberate.** During a rolling deploy, new code meets an old schema. Failing
> the process turns a one-endpoint problem into an outage.

---

## Phase 5 — Stream capabilities

The four generic capabilities absorbed from the deleted `DiagnosticRecords`. Target: a diagnostics
stream declared in **~21 manifest lines** replacing **11,880 lines** of v1 family + provider code.

| ID | Work item | Shape | Proof |
|---|---|---|---|
| **S1** | `ColumnGeneration.ProviderSequence` — `BIGSERIAL`/`IDENTITY`/`AUTOINCREMENT`; Mongo counter document in-transaction, **capability-gated on replica-set availability** | single-module | Monotonic under concurrent append, all four |
| **S2** | Append idempotency: kernel-owned ledger keyed `(unit, scope, nonce)`, **expiry from provider commit time, not `IssuedAt`** (v1 got this right) | single-module | Replay returns `Replayed`, writes nothing |
| **S3** | `RetentionDeclaration` — `KeepNewest` with partition columns. Mongo uses bounded `deleteMany` on a watermark, **not a capped collection** (breaks partitioned retention and index updates). | single-module | Exact retained count after churn |
| **S4** | `AggregationProfile` — Min/Max/Sum/SetUnion/FirstBy, kernel-validated per column type, selected by name with a declared post-reduction allowance set | single-module | v1's diagnostics conformance tests pass against the declared manifest |
| **S5** | **`samples/Groundwork.Samples.EventLog`** — a second contract family that is neither documents nor records, built only against the kernel | cross-cutting | **Milestone-1 deliverable.** If a kernel facility is missing, this does not compile — that is the failing test |

---

## Phase 6 — Layers

| ID | Work item | Shape | Proof |
|---|---|---|---|
| **L1** | `Groundwork.Records` — POCO ↔ row mapping, typed builder | single-module | `Customer` CRUD end to end |
| **L2** | `Groundwork.Documents` — composes a `StorageUnit` contributing its own envelope columns as *ordinary declared columns*, plus a `ColumnBinding` map. Ports `VersionedJsonDocumentCodec` and `DocumentJsonUpcasterRegistry` intact. | cross-cutting | Providers cannot distinguish a document write from a record write |
| **L3** | `Groundwork.SchemaTool` — CLI + MSBuild verify task, `schema emit` for dynamic schemas | single-module | Ported v1 CLI contract tests |

> **L2 must be buildable outside the v2 repository by the end of Phase 2** — against kernel packages
> from a local feed. A missing kernel facility then becomes a package-release event rather than a
> two-line edit. This is the only mechanism that reliably keeps the boundary honest, and v1 is the
> proof: the extension point existed, was structurally subordinate to document routes, and a second
> persistence framework grew inside the library instead.

---

## Phase 7 — Consumer migration

| ID | Work item | Shape |
|---|---|---|
| **E1** | Port Elsa's three hardest manifests **on paper, before the declaration model freezes** — `SecretsStorageManifest` (301 lines), Publishing, OpenTelemetry. The only evidence in existence about what a real consumer needs. | cross-cutting |
| **E2** | Elsa re-declares diagnostics as ordinary manifests; delete the `DiagnosticRecords` adapters (~4,834 lines) | cross-cutting |
| **E3** | Migrate the 45 manifest sources and ~30 modules | cross-cutting |
| **E4** | Data migration path from v1-shaped tables (envelope + JSON) to v2 typed columns | cross-cutting |

**E4 is not optional and is easy to under-scope.** Existing v1 deployments have `id_comparison_key`
primary keys and canonical-JSON content. Decide early whether v2 offers a migration tool or a
documented export/import.

---

## Housekeeping

| ID | Work item |
|---|---|
| **H1** | Close v1 issues #200, #224, #225, #226, #227 as **superseded by v2**, each linked to the work item that subsumes it: #200→Q1 (`TotalCount` is a distinct terminal, never implicit), #224→W1+W3, #225→K1 (native keys, no 1350-byte tie-break), #226→P1, #227→Q9 |
| **H2** | Archive v1: freeze at `a5fec7a`, README banner, no further feature work |
| **H3** | Delete `src/Groundwork/Materialization/` and `tests/Groundwork/Groundwork.Materialization.Tests/` — untracked empty `bin`/`obj` remnants, absent from the solution |
| **H4** | Publish v2 packages to nuget.org from first preview. External users are the forcing function for consumer-neutrality; v1 never had one |

> **H1 note:** #200, #224 and #227 carry agreed designs and detailed analysis. When closing them as
> superseded, copy that content into the successor issue rather than relying on a link — a closed
> issue is not a work queue and the analysis is the expensive part.

---

## Verification

**Per phase.** Every work item's proof column is a test that must be green before the next phase
starts. Four provider containers in CI from Phase 2 onward.

**The three standing gates**, run on every PR:

1. **Cross-provider differential suite** (from G2) — identical declaration, identical observable
   result, all four providers. Any divergence is a build failure, not a known issue.
2. **Concurrency conformance harness** (W2) — the invariant table under contention. This is the
   highest-value test in the repository and it must exist before the second provider does.
3. **Architecture tests** (K7) — reference closure and API vocabulary.

**End-to-end acceptance, in order:**

```bash
# 1. A plain table, no document model, no projections
dotnet test tests/Groundwork.Kernel.Tests --filter Customer
# 2. Same declaration, four engines, identical structures read from each catalog
dotnet test tests/Groundwork.Conformance.Tests
# 3. Write cost
dotnet run --project benchmarks/Groundwork.Benchmarks -- roundtrips --workload upsert --n 1000
#    expect <= 2 + ceil(n/1000) round trips
# 4. Coverage: an uncovered query fails the build
dotnet build samples/Groundwork.Samples.CoverageNegative   # expect GW-COVER-006
# 5. The second-family proof
dotnet build samples/Groundwork.Samples.EventLog           # kernel-only references
```

**The number that decides whether v2 worked:** re-express Elsa's `SecretsStorageManifest` and
publish the before/after line count. 301 lines is the baseline.

---

**Durability check.** Pick any three issues at random, in different phases, and read only the issue.
If an executor with no access to this conversation could not start, the capture failed and the issue
gets rewritten. Do this before declaring Deliverable 0 done.

---

## Effort

| Stream | Weeks |
|---|---|
| Phase 0 gates | 2 (parallel, throwaway) |
| Kernel + schema evolution | 5–7 |
| Substrates + four providers | 11 |
| Write path + batching | 6 |
| Query (AST → LINQ) | 12 |
| Stream capabilities | 3 |
| Records + Documents + tooling | 5 |
| **v2 total** | **~30 engineer-weeks, one senior engineer** |
| Elsa migration | 6–10 — the program's long pole |

Target `src` size: **28–32k lines against v1's 59,011.**

## The three risks that decide this

1. **The analyzer cannot see the schema or the query.** v2's premise is "drop the per-read
   declaration because the compiler can read the real query." If real applications compose queries
   the analyzer cannot follow, it degrades into noise, teams disable it, and v2 is v1 minus the
   safety net — strictly worse than today. **G1 gates this, week 1, before any AST code.**
2. **The portable semantics are not portable.** Every claim is a claim about four engines and some
   are wrong. The BSON `BinData` length-first ordering trap (Q9) was caught during design; the
   uncaught ones are the dangerous ones. **G2 gates this before the AST is public.**
3. **The kernel boundary is asserted, not proven.** This exact failure already happened in v1 with
   the diagnosis written down in advance. **S5 as a milestone-1 deliverable and L2 built
   out-of-repo are the structural defenses** — not discipline.
