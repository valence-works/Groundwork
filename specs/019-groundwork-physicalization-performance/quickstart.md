# Quickstart: Groundwork Physicalization And Performance

## Validate Planner And Projection Selection

```bash
dotnet test tests/Groundwork/Groundwork.Tests/Groundwork.Tests.csproj
```

Expected result: planner tests show optimized units produce physicalization operations and portable units do not.

## Validate SQLite Optimized Physicalization

```bash
dotnet test tests/Groundwork/Groundwork.Sqlite.Tests/Groundwork.Sqlite.Tests.csproj
```

Expected result: SQLite creates optimized projection tables for optimized units, maintains projected values on save/update/delete, and returns the same query results through `IDocumentStore`.

## Validate MongoDB Optimized Physicalization

```bash
dotnet test tests/Groundwork/Groundwork.MongoDb.Tests/Groundwork.MongoDb.Tests.csproj
```

Expected result: MongoDB stores `physicalized` values, creates provider-native indexes over them, and returns the same query results through `IDocumentStore`.

## Full Regression

```bash
dotnet test Groundwork.slnx --no-restore
```

Expected result: all Groundwork and host integration validation tests pass.

## Validate Bounded SQLite Process-Failure Recovery

```bash
dotnet test tests/Groundwork/Groundwork.PhysicalStorage.Benchmarks.Tests/Groundwork.PhysicalStorage.Benchmarks.Tests.csproj --filter ProcessFailureRecovery
```

Expected result: both declared process-failure cases and the strict retained-evidence contract pass.
See the [benchmark recovery-proof contract and nonclaims](../../benchmarks/Groundwork.PhysicalStorage.Benchmarks/README.md#bounded-sqlite-process-failure-proof)
for the detailed barrier, replay, source-binding, timing, and external-digest semantics.

### Bounded recovery checkpoint verification

Container-free candidate verification on 2026-07-29:

- 50/50 focused recovery and schema tests passed.
- 360/360 benchmark-project tests passed with the live MongoDB and relational-server integration
  classes excluded; no database-server container or benchmark measurement ran.
- The benchmark executable and its test project built with compiler warnings treated as errors.
  The existing `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 `NU1903` audit advisory was retained as a
  warning rather than hidden.
- A deliberately concurrent review build/test attempt produced a transient source-snapshot
  mismatch while build outputs changed. This is the intended fail-closed result; the recorded gates
  build first and then execute tests sequentially against the stable assembly closure.
- Targeted `dotnet format --verify-no-changes` passed for every changed C# file, and
  `git diff --check` passed. Whole-project format verification still reports pre-existing
  whitespace findings in untouched benchmark files.
- Root review caught and remediated an unbounded failure-cleanup wait, a recovery reopen path that
  could create or mutate a missing/corrupt database, incomplete retained-source validation, a
  process-global SQLite pool reset that broke a parallel existing test, an instrumentation barrier
  mislabeled as requester acknowledgement, self-authenticating checksum language, and omission of
  default-valued required JSON members. Final correctness re-review also found and remediated a
  deadline-exhaustion path that killed a child without waiting through a separate bounded cleanup
  grace to confirm its exit.

### Issue #50 bounded recovery checkpoint review

The initial exact range
`5772f7ee037cc246815a45a9a529b5292ece753c..25a457301fee0cc8b1ca27bd9466e632eef82d5a`
failed adversarial review. The remediated source range
`5772f7ee037cc246815a45a9a529b5292ece753c..519b4382f2278e37fd4fc37ed7e38091bcf13970`
then received three read-only **PASS** verdicts:

- **Correctness/mechanism:** the worker now separates instrumentation from the external requester
  response, withholds that response at both failure points, uses inspect-only recovery admission,
  replays the original mutation in recovery, and confirms forced child exit. A final medium finding
  added an independent five-second failure-cleanup grace plus an exhausted-deadline regression.
- **Evidence integrity/security:** retained verification now requires a caller-provided SHA-256 of
  the exact evidence-file bytes; enums and required members fail closed; Git identity plus the
  deterministic PE-metadata `Groundwork.*` assembly closure are recomputed in every process; and
  evidence contains no path, connection, credential, or self-authenticating signature claim.
- **Scope/test preservation:** Spec 019 T018–T025 match the implementation and tests; the schema is
  cataloged under `schemas/v1`; no existing test, provider path, benchmark gate, or baseline guard
  was removed or weakened; and issue #50 remains open.

Confirmed findings and dispositions:

- The original committed barrier was a coordinator receipt, not proof of a missing requester
  acknowledgement. Disposition: introduce a distinct unreleased response gate and immutable
  acknowledgement path, and assert acknowledgement absence before and after kill.
- Recovery called schema apply before rejecting drift. Disposition: reopen only through the
  production factory's non-mutating runtime admission; missing, empty, and corrupt files remain
  unmodified when rejected.
- A public checksum could be recomputed after forged process receipts. Disposition: remove the
  embedded seal and require the expected exact-file digest from separately retained workflow
  metadata before parsing and semantic validation.
- Numeric enums, default-valued omitted members, unavailable Git identity, and stale dependency
  binaries were not all bound. Disposition: strict recovery-local JSON options, conditional schema
  constraints, canonical Git validation, and a deterministic referenced-assembly closure digest.
- The first timing label included operations it did not measure, and deadline exhaustion could skip
  exit confirmation. Disposition: record only the seed-through-recovery execution interval,
  disclose persistence/deletion exclusions, and use a separate bounded cleanup grace on failures.
- A process-global SQLite pool reset interfered with an existing parallel test. Disposition:
  recovery-only connections disable pooling and cleanup has no global side effect.

Final frozen-source verification: 50/50 focused recovery/schema tests and 360/360 container-free
benchmark tests passed; warnings-as-errors builds, targeted format, and `git diff --check` passed.
No database-server container or benchmark measurement ran. Reviewers used GPT-5.6 Terra High
because the requested Luna reviewer model was unavailable.

## Issue #50 Execution-Evidence Checkpoint Review

PR #147 is a bounded checkpoint for synchronized-contention and provider-native plan evidence. It
does not claim the controlled four-provider matrix or close issue #50.

Frozen source range:
`d297147e0cd6b018d70b1f7d61fef771e32b022f..2644071cfc46f37eddb7cf0235f5fa2892b5757b`.

Local verification on the frozen source head:

- 332/332 container-free benchmark-harness tests passed. The server-backed relational and MongoDB
  test classes were deliberately excluded from this resource-constrained review.
- The one MongoDB integration test that exposed the hosted failure was reproduced locally and
  passed after remediation; its Testcontainers MongoDB instance and cleanup sidecar exited.
- `dotnet build Groundwork.slnx --no-restore` passed with zero errors.
- Targeted `dotnet format --verify-no-changes` and `git diff --check` passed.
- The only warnings were the existing `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 `NU1903` advisories.
- Hosted run `30445493298` passed the SQLite shared-form PR smoke on candidate `c66adff`.
- An exploratory expansion of the real MongoDB runner to the ordered workloads reached the repaired
  command binder, then failed the existing declared-compound-index gate because MongoDB selected the
  simpler status index plus an in-memory sort. The expansion was not retained: weakening that gate
  is outside this checkpoint, and the provider behavior remains part of the open #50 physical-form
  decision.

Three adversarial read-only reviewers independently passed the exact source range after the
following originating-review findings were remediated:

- Correctness/mechanism: PASS. Strict declared-index evidence rejects mixed, malformed, unrelated,
  or node-incompatible identities; scan characterization admits alternate access only through a
  fixed redacted sentinel. Valid PostgreSQL `Incremental Sort`, linked-relation, direct bitmap, and
  nested `BitmapAnd`/`BitmapOr` shapes remain admitted.
- Evidence integrity/security: PASS. Raw relational commands become digests before serialization;
  provider plans are rebuilt from allowlisted structural facts; hostile XML/JSON members,
  predicates, arbitrary aliases, unrelated identities, and secret-bearing values are removed or
  rejected before either artifact is written.
- Scope/test preservation: PASS. Changes remain inside the #50 harness, workflow, schemas,
  documentation, and tests; the matrix, workloads, process protocol, and universal synchronized-
  contention gate are unchanged; no tests were removed; issue #50 remains open.

Confirmed findings were remediated and re-verified before those passes:

- Added PostgreSQL `Incremental Sort` and valid bitmap-combiner support without widening identity
  placement.
- Rejected strict mixed expected/wrong indexes for PostgreSQL, MongoDB, and SQL Server instead of
  projecting them into misleading evidence.
- Preserved alternate-index scan characterization as `alternative-index-redacted`.
- Reduced SQL Server retention to safe `ShowPlanXML`/`RelOp`/`Object` structure, dropping hostile
  same-namespace element names and arbitrary attributes.
- Redacted SQLite predicates and arbitrary aliases while preserving fixed `p`/`l` aliases so a
  forbidden primary or linked full-table scan cannot be hidden from the strict gate.
- Reset PostgreSQL relation scope on explicit unrelated relations, carried it only through valid
  bitmap chains, and enforced relation/index fields against their legal node kinds.
- Bound MongoDB's production `status: {$exists: true}` plus singleton-`$or` predicate exactly:
  `$exists` is retained as non-secret structural metadata, while false, extra, or malformed guarded
  predicates fail closed.
- Bound the production ordered MongoDB predicate to exactly the request-selected projected members:
  unordered requests permit only the `status` existence guard, while ordered requests require both
  `status` and `rank`; missing, false, unrelated, or surplus guards fail closed.
- Restricted `$exists: true` retention to direct predicate-member guards. Nested equality operands
  cannot preserve Boolean values, selected-status capture rejects disguised nested values, and a
  writer-level regression proves rejected evidence creates no plan or sidecar artifact.
- Added writer-level no-artifact regressions for every fail-closed branch above, including the
  final invalid-relation-node test requested by scope review.

This review record is the only change after the frozen source head. Before merge, all three
reviewers re-verify the final record-only head so this durable account and the PR candidate cannot
diverge.

## Issue #50 Scheduled-Coverage Matrix Checkpoint Review

PR #152 is a container-free checkpoint that binds the scheduled aggregate to its exact closed
provider/form/dataset/selectivity/workload/repetition matrix. It does not execute that matrix,
publish or approve a baseline, select a physical form, produce a performance verdict, or close
issue #50.

Initial review range:
`b5d59f1abb080d2ae2d2d1f1bd0505da11f79f80..a7091f75101ffab09676166d23843c0574042569`.

Remediated source range:
`b5d59f1abb080d2ae2d2d1f1bd0505da11f79f80..c5dcdd624fb053c3945a5cca31a74a24cf9bfad7`.

Local verification on the remediated source head:

- 42/42 container-free `BenchmarkSchemaTests` and `BenchmarkWorkflowContractTests` passed.
- `python3 -m py_compile tools/verify_physical_storage_scheduled_coverage.py` passed.
- `git diff --check` passed.
- No database-server containers, provider suites, or benchmark measurements ran.
- The only warnings were the existing `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 `NU1903` advisories.

Three adversarial read-only reviewers re-verified the complete remediated source range:

- Correctness/mechanism: PASS. Test mode must remain shallow and cannot emit
  `scheduled-scaffold`; both runtime validation and the published schema reserve deep mode for the
  complete fixed production matrix.
- Evidence integrity/security: PASS. The aggregate no longer serializes or claims `runId` as
  attested provenance; exact top-level/nested members, matrix dimensions, digest, mode, integer
  counts, deep-mode Git identity, and non-promotability fail closed.
- Scope/test preservation: PASS. Changes remain within the #50 aggregate verifier, its strict
  schema/documentation, and covering tests; no workflow, provider implementation, workload,
  baseline registry, or existing test was removed or weakened.

Confirmed findings and dispositions:

- BLOCKER: a narrowed `--test-mode` run with a group verifier could be labeled
  `scheduled-scaffold`. Disposition: test mode now requires `--skip-deep-verification`, group
  verification is incompatible with that mode, and regression coverage proves rejection.
- BLOCKER: the emitted `runId` was only a caller-controlled shard-directory label, not bound
  workflow-run provenance. Disposition: remove it from the artifact/schema, retain it only as a
  shard locator, and disclose explicitly that this checkpoint is not run attestation.
- HIGH: the writer-side validator was weaker than the published nested schema and accepted Boolean
  counts. Disposition: enforce the exact nested matrix contract, complete deep-mode matrix,
  non-Boolean integer counts, digest/mode/Git constraints, and negative regressions.

This review record is the only change after the remediated source head. Before merge, the three
reviewers re-verify the final record-only head so the durable account and PR candidate cannot
diverge.

## Issue #50 Complete Stable-Order Certification Checkpoint

This checkpoint starts from Groundwork main
`c48b5a1d04c2664211af1f14d403e3f0391846ca`. It closes the false-certification gap that allowed an
ordered MongoDB benchmark query to pass core index admission while the provider selected
`by-status` and performed a blocking `SORT`. The failure was reproduced on that exact base before
timing began; no latency or throughput sample from the failed route was retained or trusted. The
[redacted reproduction record](evidence/stable-order-reproduction.md) binds MongoDB 7.0.24, the
shared form, exact canonical invocation, workload inputs, and winning/rejected plan shapes without
retaining connection, container, database, host, or generated physical-name values.

The certified mechanism is:

- a nonunique scale-bearing offset query requires the order-preserving identity comparison key as
  the final physical-index column;
- a nonunique scale-bearing cursor query requires the identity lookup key instead;
- an already-unique logical key remains the complete total order, so no appended identity column
  weakens its business-key uniqueness;
- nonpaged queries may share an index carrying the required tail, while offset and cursor queries
  cannot share one index because their required identity representations differ;
- MongoDB execution and explain bind the exact route-declared index, and the strict winning-plan
  inspector independently rejects both `COLLSCAN` and `SORT`;
- executable-route serialization preserves both the manifest definition name and the
  provider-resolved executable name when a linked identity-tail column is renamed.

Candidate verification:

- 631/631 Groundwork core tests passed.
- 556/556 MongoDB provider tests passed in 27m39s on the frozen production candidate; the focused
  final-source check also passed all 121/121 `MongoDbPhysicalStorageConformanceTests` cases.
- 590/590 SQLite provider tests passed.
- The final PostgreSQL and SQL Server inventory passed as 801/801 suite cases plus the one
  scheduler-sensitive diagnostic case in isolation. The unfiltered full invocation executed all
  802 cases but raced that unrelated test's 20ms diagnostic deadline at suite startup; it passed
  immediately in isolation in 5ms. The review-remediated ordered/identity subset passed 16/16 across
  both providers and every physical form.
- 361/361 container-free benchmark-harness tests passed with
  `--filter "FullyQualifiedName!~MongoDbBenchmarkSignalEvidenceTests&FullyQualifiedName!~RelationalServerBenchmarkTargetTests"`.
- The real MongoDB ordered-plan regression passed across all three physical storage forms and
  emitted and validated two temporary strict plan artifacts per case; fixture disposal then removed
  the temporary output.
- `dotnet build Groundwork.slnx --no-restore` passed with zero errors. The existing
  `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 `NU1903` advisory remains visible rather than suppressed.
- Targeted `dotnet format --verify-no-changes` passed for every changed C# file, and
  `git diff --check` passed.
- All task-owned Testcontainers exited; the unrelated pre-existing `elsa-keycloak` container was
  not touched.

This checkpoint does **not** execute or approve the scheduled 1K/100K/1M four-provider matrix,
promote an immutable baseline, select a final physical form, produce an Elsa performance verdict,
or close issue #50. Those claims remain gated by the controlled matrix and baseline review.
