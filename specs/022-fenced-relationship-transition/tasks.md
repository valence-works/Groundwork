# Tasks: Fenced Relationship Transition

## Phase 1: Specification

- [x] T001 Record the ratified #141 transition semantics, scope, nonclaims, and acceptance scenarios in Spec 022 — Evidence: `spec.md` binds all four user stories, FR-001–FR-011, and deliberate exclusions to the 2026-07-25 ratification.
- [x] T002 Record the SQLite-first implementation plan and public fail-closed constraint — Evidence: `plan.md` sequences the internal provider slice and keeps `GW-RELATIONSHIP-012` plus four-provider certification intact.

## Phase 2: Durable Transition State

- [x] T002A Ratify and encode the provider-neutral expected-absent inaugural-transition representation — Evidence: `RelationshipMaterializationExpectedActive` is the closed `Absent | ExactGeneration` contract and Core tests cover both forms.
- [x] T003 Define provider-owned active/candidate transition state and legal phase transitions — Evidence: `SqliteRelationshipTransitionExecutor` persists Preparing, Validated, Active, and Failed state with candidate-bound expected-active preconditions, an opaque HMAC of the complete normalized candidate input, and a separate domain-authenticated failure envelope.
- [x] T004 Materialize versioned SQLite transition, reference-sidecar, and target-fence storage — Evidence: executor transactionally creates and validates the exact `*_v1` SQLite state, active, sidecar, target-index, and fence schema; it upgrades only the exact pre-`failure_mac` state-v1 shape and otherwise fails closed.
- [x] T005 Add internal test-only admission that cannot enable public relationship capability — Evidence: `CreateForTestOnly` is internal and `Public_factory_still_rejects_relationship_manifest_before_it_opens_the_connection` preserves `GW-RELATIONSHIP-012`.

## Phase 3: Backfill And Validation

- [x] T006 Backfill exact non-null source references and target-key fences from the compiled relationship plan — Evidence: executor projects each source via `PhysicalRelationshipPlan.ProjectReferenceIdentity` and inserts idempotent candidate sidecar/fence rows.
- [x] T007 Reject dangling legacy references with candidate-bound `GW-RELATIONSHIP-013` and no raw data — Evidence: focused SQLite tests verify opaque candidate-bound diagnostics, no activation, no dangling sidecar/fence rows, and terminal replay only after authenticating the persisted closed failure envelope; cross-candidate swaps and MAC corruption fail closed.
- [x] T008 Persist bounded progress and make same-request backfill/revalidation replay-safe — Evidence: cancellation and validated-acknowledgement-loss tests require the durable HMAC of the complete normalized source/target input before progress, validation, or activation can resume.

## Phase 4: Cutover And Recovery

- [x] T009 Atomically compare-and-swap the validated candidate into the active generation — Evidence: SQLite immediate transaction uses absent INSERT-CAS or exact generation/fingerprint UPDATE-CAS before marking candidate Active; reopen accepts Activated only when state, input, progress, and exact candidate-scoped sidecar/fence tuples remain intact.
- [x] T010 Reject stale/competing candidates without changing active state — Evidence: concurrent expected-absent candidate test yields exactly one Active result and one RelationshipConflict.
- [x] T011 Recover deterministically across distinct-instance reopen before validation, after validation, and after cutover acknowledgement loss — Evidence: focused suite covers bounded-progress cancellation, validated-state acknowledgement loss, and post-cutover acknowledgement loss with distinct executors; changed pending input fails closed rather than resuming by index.
- [x] T012 Preserve prior active generation on failure, cancellation, or incomplete candidate state — Evidence: dangling and cancellation cases keep active state absent; CAS conflicts do not overwrite the winning generation.

## Phase 5: Evidence

- [x] T013 Add real durable SQLite tests for valid backfill, dangling failure, cutover competition, replay, cancellation, and restart — Evidence: `SqliteRelationshipTransitionTests` has twenty-four file-backed SQLite cases, including authenticated failure replay, exact tuple/isolation assertions, pre-MAC schema upgrade, and missing/corrupt/duplicate-capable state/reference/fence/active evidence rejection.
- [x] T014 Prove public `GW-RELATIONSHIP-012` admission remains fail-closed before provider I/O — Evidence: focused factory test observes `GW-RELATIONSHIP-012` while the supplied SQLite connection remains Closed.
- [x] T015 Run focused/Core/materialization/provider gates and record exact evidence and nonclaims in the quickstart — Evidence: quickstart records the third remediation's 24 SQLite, 51 Core, and 1 materialization relationship test passing on the exact worktree diff over the identified source head.
- [ ] T016 Run three adversarial exact-range reviews, remediate findings, and record dispositions
- [ ] T017 Land by Model B, verify remote main containment, and update Groundwork #141 plus Elsa #643
