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
- [x] T019 Add a versioned recovery request/result/evidence contract and fail-closed schema — Evidence: `RecoveryProtocol.cs` and `recovery-evidence.schema.json` require exact versioned members, reject unknown or omitted members, and seal the retained semantic record.
- [x] T020 Add a distinct-process SQLite recovery worker using the production physical-target admission path — Evidence: `SqliteRecoveryWorker.cs` opens the durable file through `SqliteBenchmarkTarget` and the public document-store/unit-of-work contracts in a child process.
- [x] T021 Add a bounded parent orchestrator that releases and terminates the worker at the declared barrier — Evidence: `SqliteProcessFailureRecovery.cs` owns the whole-proof deadline, validates a live barrier, kills the worker tree, confirms exit, and starts a distinct verifier process.
- [x] T022 Bind and verify exact source, provider/form, process outcome, recovered state, and safe retained metadata — Evidence: recovery records bind Git commit/dirty/tree digest, harness assembly SHA-256, provider/form, process receipts, exact state digest, retry result, and bound without paths or connection values.
- [x] T023 Prove pre-commit rollback, committed-before-acknowledgement recovery, and stale-retry rejection with real durable SQLite process tests — Evidence: `SqliteProcessFailureRecoveryTests` proves version 1 rollback, version 2 committed recovery, and `ConcurrencyConflict` on the stale retry.
- [x] T024 Add timeout, same-process, incomplete, and resealed-tamper rejection tests — Evidence: focused tests reject canceled/incomplete children, same-process identities, missing required JSON members, source drift, semantic resealing, and a missing durable database while confirming bounded child cleanup.
- [x] T025 Run the focused/full benchmark gates and record evidence and deliberate nonclaims in the quickstart — Evidence: 41 focused and 351 container-free benchmark tests pass; compiler, targeted-format, and diff gates pass with the existing NU1903 advisory disclosed in `quickstart.md`.
