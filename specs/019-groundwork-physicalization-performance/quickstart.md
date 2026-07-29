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
