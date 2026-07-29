# Tasks: Fenced Relationship Transition

## Phase 1: Specification

- [x] T001 Record the ratified #141 transition semantics, scope, nonclaims, and acceptance scenarios in Spec 022 — Evidence: `spec.md` binds all four user stories, FR-001–FR-011, and deliberate exclusions to the 2026-07-25 ratification.
- [x] T002 Record the SQLite-first implementation plan and public fail-closed constraint — Evidence: `plan.md` sequences the internal provider slice and keeps `GW-RELATIONSHIP-012` plus four-provider certification intact.

## Phase 2: Durable Transition State

- [ ] T002A Ratify and encode the provider-neutral expected-absent inaugural-transition representation — Blocked: Core currently requires a non-null active generation.
- [ ] T003 Define provider-owned active/candidate transition state and legal phase transitions
- [ ] T004 Materialize versioned SQLite transition, reference-sidecar, and target-fence storage
- [ ] T005 Add internal test-only admission that cannot enable public relationship capability

## Phase 3: Backfill And Validation

- [ ] T006 Backfill exact non-null source references and target-key fences from the compiled relationship plan
- [ ] T007 Reject dangling legacy references with candidate-bound `GW-RELATIONSHIP-013` and no raw data
- [ ] T008 Persist bounded progress and make same-request backfill/revalidation replay-safe

## Phase 4: Cutover And Recovery

- [ ] T009 Atomically compare-and-swap the validated candidate into the active generation
- [ ] T010 Reject stale/competing candidates without changing active state
- [ ] T011 Recover deterministically across distinct-instance reopen before validation, after validation, and after cutover acknowledgement loss
- [ ] T012 Preserve prior active generation on failure, cancellation, or incomplete candidate state

## Phase 5: Evidence

- [ ] T013 Add real durable SQLite tests for valid backfill, dangling failure, cutover competition, replay, cancellation, and restart
- [ ] T014 Prove public `GW-RELATIONSHIP-012` admission remains fail-closed before provider I/O
- [ ] T015 Run focused/Core/materialization/provider gates and record exact evidence and nonclaims in the quickstart
- [ ] T016 Run three adversarial exact-range reviews, remediate findings, and record dispositions
- [ ] T017 Land by Model B, verify remote main containment, and update Groundwork #141 plus Elsa #643
