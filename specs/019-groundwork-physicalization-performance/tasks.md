# Tasks: Groundwork Physicalization And Performance

## Phase 1: Setup

- [x] T001 Create G7 specification and implementation plan in `specs/019-groundwork-physicalization-performance/`
- [x] T002 Update Speckit and program-goal pointers for G7

## Phase 2: Core Physicalization Model

- [x] T003 Add provider-neutral physicalized field planning in `src/Groundwork/Core/Physicalization/`
- [x] T004 Extend materialization operations to describe optimized physicalization in `src/Groundwork/Core/Materialization/MaterializationPlan.cs`
- [x] T005 Update document planning to emit optimized physicalization operations in `src/Groundwork/Documents/Planning/DocumentManifestPlanner.cs`
- [x] T006 Add core planner/projection tests in `tests/Groundwork/Groundwork.Tests/`

## Phase 3: Relational Optimized Path

- [x] T007 Add relational physicalization naming helpers in `src/Groundwork/Relational/Physicalization/`
- [x] T008 Extend relational materialization to create optimized projection structures in `src/Groundwork/Relational/Materialization/RelationalMaterializerBase.cs`
- [x] T009 Extend relational document store save/update/delete/query to maintain and use optimized projections in `src/Groundwork/Relational/Documents/`
- [x] T010 Add SQLite optimized physicalization tests in `tests/Groundwork/Groundwork.Sqlite.Tests/`

## Phase 4: MongoDB Optimized Path

- [x] T011 Extend MongoDB materialization to create optimized projection indexes in `src/Groundwork/MongoDb/Materialization/MongoDbGroundworkMaterializer.cs`
- [x] T012 Extend MongoDB document store save/update/delete/query to maintain and use optimized projections in `src/Groundwork/MongoDb/Documents/MongoDbDocumentStore.cs`
- [x] T013 Add MongoDB optimized physicalization tests in `tests/Groundwork/Groundwork.MongoDb.Tests/`

## Phase 5: Validation

- [x] T014 Run `dotnet test tests/Groundwork/Groundwork.Tests/Groundwork.Tests.csproj`
- [x] T015 Run `dotnet test tests/Groundwork/Groundwork.Sqlite.Tests/Groundwork.Sqlite.Tests.csproj`
- [x] T016 Run `dotnet test tests/Groundwork/Groundwork.MongoDb.Tests/Groundwork.MongoDb.Tests.csproj`
- [x] T017 Run `dotnet test Groundwork.slnx --no-restore`

## Phase 6: Issue #50 Bounded Recovery Evidence

- [x] T018 Specify the pre-commit and committed-before-acknowledgement process-failure windows and retained nonclaims — Evidence: User Story 4, FR-012–FR-015, and SC-005–SC-006 freeze the two failure points and the non-promotable SQLite boundary.
- [x] T019 Add a versioned recovery request/result/evidence contract and fail-closed schema — Evidence: `RecoveryProtocol.cs` and `recovery-evidence.schema.json` require exact versioned members, string-only enums, conditional failure-point outcomes, and caller-anchored exact-file SHA-256 verification.
- [x] T020 Add a distinct-process SQLite recovery worker using the production physical-target admission path — Evidence: `SqliteRecoveryWorker.cs` reopens the durable file through the production factory's inspect-only admission and the public document-store/unit-of-work contracts in a child process.
- [x] T021 Add a bounded parent orchestrator that withholds response release and terminates the worker at the declared barrier — Evidence: `SqliteProcessFailureRecovery.cs` owns the recovery-execution deadline, validates a live instrumentation barrier, deliberately withholds response release, proves requester-acknowledgement absence, kills the worker tree, confirms exit, and starts a distinct verifier process.
- [x] T022 Bind and verify exact source, provider/form, process outcome, recovered state, and safe retained metadata — Evidence: recovery records bind Git commit/dirty/worktree digest, the complete referenced Groundwork assembly-closure digest/count, provider/form, three process receipts, before/after retry state, and recovery-execution bound without paths or connection values; workflow metadata retains the exact evidence-file digest separately.
- [x] T023 Prove pre-commit rollback, committed-before-acknowledgement recovery, and stale-retry rejection with real durable SQLite process tests — Evidence: `SqliteProcessFailureRecoveryTests` proves v1 rollback followed by an eligible replay to v2, and committed v2 recovery followed by `ConcurrencyConflict` with no duplicate effect.
- [x] T024 Add timeout, same-process, incomplete, and resealed-tamper rejection tests — Evidence: focused tests reject canceled/incomplete children, same-process identities, numeric/undefined enums, missing required JSON members, invalid source closure, exact-file digest mismatch, corrupt durable files, and missing databases while confirming bounded child cleanup.
- [x] T025 Run the focused/full benchmark gates and record evidence and deliberate nonclaims in the quickstart — Evidence: 50 focused and 360 container-free benchmark tests pass; compiler, targeted-format, and diff gates pass with the existing NU1903 advisory disclosed in `quickstart.md`.

## Phase 7: Complete Stable-Order Certification

- [x] T026 Reproduce the MongoDB ordered-query strict native-plan failure on current main and retain the exact mechanism without timing claims. — Evidence: `evidence/stable-order-reproduction.md` binds the clean base SHA, MongoDB 7.0.24, shared form, exact canonical invocation, redacted winning/rejected plan shapes, and pre-timing failure disposition.
- [x] T027 Require scale-bearing offset-query physical indexes to include and validate the runtime comparison-key identity tail while preserving cursor lookup-key semantics. — Evidence: shared core admission now requires the comparison tail for nonunique offset routes, the lookup tail for nonunique cursor routes, preserves already-total unique keys, and rejects incompatible shared tail shapes.
- [x] T028 Correct both benchmark indexes and harden the strict MongoDB winning-plan gate against blocking sorts. — Evidence: both ordered benchmark indexes carry the certified identity tail, MongoDB execution and explain bind the declared physical index, and the strict inspector rejects every winning-plan `SORT`.
- [x] T029 Add core, model, inspector, and real MongoDB all-form regression coverage. — Evidence: compiler/resolver/serializer, provider-model, hinting, strict-inspector, uniqueness, shared-index, and real three-form MongoDB regressions cover the certified mechanism.
- [x] T030 Run focused/container-free/full relevant gates and record exact-range adversarial review verdicts and nonclaims in the quickstart. — Evidence: `quickstart.md` binds the complete verification inventory and three-axis review of `c48b5a1d04c2664211af1f14d403e3f0391846ca..8be79be8a183380518ed1ec79b9cae12c24ad261`, including the hosted PR-smoke failure, confirmed-finding dispositions, the withdrawn discovery-count finding, and the remaining matrix/baseline/verdict nonclaims.

## Phase 8: Issue #50 Immutable-Baseline Governance

- [x] T031 Amend the Spec 019 baseline contract to make a reviewed Groundwork `main` merge naming immutable run-group/artifact content digests the sole approval authority; keep direct caller paths diagnostic and non-promotable. — Evidence: the 2026-07-30 amendment is captured in `spec.md`, `plan.md`, and the versioned registry README; `BaselineRegistrySelector` always rejects a caller-supplied direct artifact path as promotable without removing the existing diagnostic comparison path.
- [x] T032 [P] Add red container-free registry contract tests for immutable digest binding, append-only generations, explicit one-active-per-exact-compatibility-tuple activation, direct-path rejection, and fail-closed selection. — Evidence: `BaselineRegistryTests` covers direct-path rejection, byte-bound claim maps, duplicate activation, incomplete recovery/matrix, untrusted authority, authority/source binding, immutable transition history, staged-deactivation rejection, cyclic/forward supersession, explicit replacement, and the committed empty registry.
- [x] T033 Implement the versioned committed baseline registry model, strict schema, and empty truthful registry; make the registry the sole promotable selector. — Evidence: `BaselineRegistry.cs`, strict `baseline-index.schema.json`, and `baseline-index.json` use `groundwork.physical-storage.baseline-registry/v1` with immutable generations, separate activation records, and zero active generations.
- [x] T034 Implement fail-closed baseline activation/selection and complete eligibility evaluation: closed exact-HEAD tuple set; clean source; fixed workload/profile inputs; provider image/version/effective settings; machine metadata; raw/summary/correctness/result/plan digests; synchronized contention; recovery; three reviews; hosted checks; and no missing/extra tuple. — Evidence: eligibility recomputes the closed scheduled matrix; selection requires independently trusted reviewed-main authority, verifies every artifact SHA-256, parses the four retained claim maps, and requires each mapped digest to identify retained artifact content; transition validation preserves append-only history and forbids staged activation removal or replacement without explicit supersession.
- [x] T035 Record container-free evidence and nonclaims for the governance mechanism. Controlled matrix execution, immutable registry population, physical-form selection, and final reports remain open. — Evidence: the quickstart records the exact final 47/47 focused and 357/357 deterministic container-free commands, zero-error build, formatting/JSON/diff checks, the contention-sensitive process-test exclusions and failed-attempt disclosure, and explicit matrix/baseline/form/verdict nonclaims.
- [ ] T036 Execute the controlled four-provider exact-HEAD synchronized-contention matrix and retain sealed run-group evidence. — **Later, requires controlled provider execution.**
- [ ] T037 Review and activate eligible append-only immutable baseline generations for every exact compatibility tuple. — **Later, requires T036 plus three reviews and hosted checks.**
- [ ] T038 Select reviewed physical forms and publish the final provider/form reports consumable by Elsa #646. — **Later, requires T037 and Elsa-owned verdict work.**
