# Groundwork physical-storage benchmark harness scaffolding

This project is an honest, mergeable harness-scaffolding slice for issue #50. It exercises
Groundwork's production document-store path across SQLite, SQL Server, PostgreSQL, and MongoDB and
the three physical storage forms. It materializes real manifests, creates real storage, uses
production sessions and bounded-query translation, and records provider-native query plans.

It does **not** complete issue #50. The scheduled protocol now carries the ratified 1K/100K/1M
dataset dimension, the accepted one-warm-up/three-measured-process scheduled protocol, closed
reviewed payload-profile bindings, and a ratified selectivity policy: 10% is the mandatory
indexed-query acceptance shape and 50% is a retained scan characterization. The harness contains no EF Core comparison, cannot promote
baselines, and cannot make an Elsa migration go/no-go decision.

## Bounded SQLite process-failure proof

The `recovery-proof` command creates a durable SQLite target, reopens it through the production
factory's inspect-only admission path, starts a distinct mutation worker, kills that process tree at
one declared instrumentation barrier, and verifies the exact durable state in another process:

```bash
dotnet run --project benchmarks/Groundwork.PhysicalStorage.Benchmarks -- \
  recovery-proof \
  --form shared \
  --failure-point pre-commit \
  --output artifacts/recovery-pre-commit.json
```

The parent never releases the worker's response gate. At `pre-commit`, recovery first observes the
committed v1 state and replaying the original expected-version-1 request succeeds to v2. At
`committed-before-ack`, recovery first observes v2 and the same replay conflicts. In both cases the
requester acknowledgement remains absent before and after the forced kill.

The retained document binds the exact Git commit and worktree digest, a deterministic digest of the
complete referenced `Groundwork.*` assembly closure, provider/form, three distinct process receipts,
forced termination, before/after retry state digests, retry outcome, and configured recovery-
execution bound. The timing interval begins with seed setup and ends after both child exits and the
exact recovery result; evidence persistence/readback and best-effort scratch deletion are outside
that field. Failed executions use a separate five-second cleanup grace to confirm child termination
after the recovery deadline. The evidence contains no database path, connection value, or
credential and is always marked non-promotable.

The command prints a SHA-256 of the exact retained evidence file bytes. CI or workflow metadata must
retain that digest separately; it is an integrity anchor, not a signature. Verify later with:

```bash
dotnet run --project benchmarks/Groundwork.PhysicalStorage.Benchmarks -- \
  recovery-evidence-verify \
  --evidence artifacts/recovery-pre-commit.json \
  --expected-evidence-sha256 <digest-retained-out-of-band>
```

This is one SQLite correctness slice. It does not prove four-provider recovery, approve an immutable
baseline, select a physical form, compare EF, or issue an Elsa migration verdict.

## Current correctness and plan gates

Before timing, every selected provider and storage form must prove:

1. storage-scope isolation, optimistic concurrency, unit-of-work rollback, bounded query/count
   agreement, and mixed-direction ordering; and
2. on a separately initialized disposable target with the exact configured measured cardinality,
   selectivity, and provider statistics, capture provider-native `EXPLAIN`, `STATISTICS XML`, or
   MongoDB `explain` evidence for every applicable timed selection and count shape. At the
   mandatory 10% indexed-query acceptance shape, selection of the declared index is required and
   a full scan of the predicate-bearing indexed relation is rejected. At the retained 50% scan
   characterization shape, the exact native plan is retained but an optimizer-selected scan is
   recorded rather than treated as a failure. A linked form may still use an optimizer-selected
   scan of its separate primary payload relation after the linked predicate index has selected the
   bounded owner set; treating that as predicate fallback would be a false positive.

The backfill workload has an additional post-measurement check. Outside the timed region, it uses
the additive model to run the bounded query and directly queries the newly projected `category`
field. Both counts must match the seeded migration row count.

Relational statistics are finalized as part of deterministic seeding on both measured and plan
targets. Correctness-gate documents are removed and statistics are finalized again before timing.
Native-plan capture is read-only: it does not add or remove rows, change selectivity, or refresh
statistics. A provider that chooses a scan at the mandatory 10% acceptance shape fails the gate;
the retained 50% characterization is never changed or inflated just to force an index plan.
Before persistence, plan validation parses the provider-native plan in both modes and binds its
observed collection, relation, table, or SQLite alias to the exact parameterized command/object
binding captured by the production query path. Relational SQL remains in memory only for that
admission check; the sidecar retains its SHA-256 identity plus the typed query/parameter receipt,
never raw command text, connection data, or literal query values. This binding strengthens
sealed-directory internal consistency; like the artifact-integrity ledger, it is not an external
authenticity or cryptographic-provenance root.
Provider plan retention constructs fresh structural documents: SQLite canonicalizes access rows
and redacts predicate details; SQL Server keeps only ShowPlan
elements, known gate-bearing access operators, and bound table/index identities; PostgreSQL keeps only the plan
tree plus allowlisted operator/relation/index/estimate members, and MongoDB keeps only the bound
namespace plus winning stage/index topology. Statement text, expressions, predicates, commands,
arbitrary aliases, namespace declarations, comments, and processing instructions are discarded.
SQLite's fixed `p` and `l` aliases are retained only so the strict gate cannot hide a primary or
linked full-table scan; every other alias is replaced by a stable redacted token.
Declared-index gates reject any different bound index. Scan characterization instead retains a
provider-selected alternative as `alternative-index-redacted`, preserving the access-path fact
without disclosing its identity or making a declared-index claim.
MongoDB command admission binds the renderer's complete canonical sort: optional semantic rank
first, followed by the scope and identity-comparison tie breaks in their exact directions.
The unordered equality workload and the ordered/paged workloads use separate declared query
identities and physical indexes (`by-status` and `by-status-rank`). This prevents the query plan's
default compound order from silently making `indexed-query` and `mixed-compound-ordering` the same
physical operation, and lets the retained command receipt prove which shape actually executed.

After materialization, SQLite, SQL Server, and PostgreSQL stores are opened through their public
production `OpenPhysicalAsync` factories. Factory admission must succeed before correctness gates
or timing begin, and restart paths re-enter through the same admission boundary.

These are harness correctness gates only. Passing them does not make performance evidence complete.

## Matrix and independent-run protocol

The profiles provide repeatable controls. Each provider/form/workload/data-shape/repetition tuple is
serialized as an immutable worker request and measured in a separate process:

| Control | Smoke | Scheduled scaffold |
|---|---:|---:|
| Seed | 20260713 | 20260713 |
| Primary dataset | 250 | 1,000; 100,000; 1,000,000 |
| Payload profile | reviewed workload binding | `ordinary-json-v1` for ordinary workloads; `storage-growth-1k-v1` for `storage-growth` |
| Query selectivity | 1,000 basis points (mandatory index gate) | 1,000 basis points (mandatory index gate); 5,000 basis points (scan characterization) |
| Untimed warm-up processes | 1 per tuple | 1 per tuple |
| Independent measured processes | 1 | 3 |
| Migration dataset | 100 | 5,000 |
| Warmup iterations | 2 | 5 |
| Minimum measured iterations | 7 | 30 |
| Minimum measured operations | 1 | 100 |
| Minimum steady-state execution | 0 seconds | 30 seconds |
| Operations per measured batch | 10 | 100 |
| Concurrency | 4 | 16 |
| Default providers | SQLite | All four |
| Storage forms | All three | All three |

Use `--payload-profiles`, `--selectivity-bps`, and `--independent-runs` to supply reviewed
scheduled controls without changing code. Payload profiles are recorded in full in worker requests,
configuration artifacts, data-shape identities, and consumer-evidence fingerprints. A legacy
`--payload-padding-bytes` override remains available only to make non-promotable smoke diagnostics
explicit; scheduled runs fail closed rather than accepting raw padding. The reviewed
5,000-basis-point shape is the only non-gating characterization;
every other selectivity retains the declared-index gate unless a later reviewed policy says
otherwise. Providing payload values does not make a run promotable.

Both profiles always emit `baselineEligibility.eligible: false`. Diagnostics explain that issue #50
still requires controlled execution of the complete reviewed matrix, exact-HEAD live evidence from
all four providers, and the Elsa-owned EF Core oracle. A caller-provided `--baseline` path is
diagnostic-only; it is not a scheduled-promotion selector. A promotable Groundwork baseline can
only be selected through the committed versioned registry after a reviewed `main` merge binds the
run-group and every retained artifact to immutable SHA-256 content digests.

The GitHub workflow is named `Physical Storage Benchmark Evidence (Scaffolding)`. Pull requests run
a deliberately narrow SQLite/shared-form smoke over seven representative workloads. Weekly/manual
runs split the four-provider scheduled scaffold into deterministic provider/form/dataset shards on
the controlled self-hosted runner pool. Every artifact remains non-promotable; the workflow does
not perform candidate promotion or migration-decision gating.

The scheduled cardinality is calculated, not inferred:

- `4 providers × 3 forms × 3 dataset sizes = 36` shards;
- each shard has `2 selectivity shapes × 14 workloads × (1 untimed warm-up + 3 measured repetitions) = 112` workers; the reviewed payload profiles are bound per workload, not multiplied into a second dimension;
- the complete schedule therefore has `4,032` workers: `1,008` warm-up and `3,024` measured; and
- the mandatory 30-second measured floor alone is `3,024 × 30 = 90,720 seconds`, or 25.2 aggregate
  measured hours before setup, seeding, validation, and artifact work.

With all 36 shard slots available, each shard carries 84 measured workers and therefore 42 minutes
of mandatory measured execution. The workflow budgets 20 minutes for one contract preflight, 280
minutes for the parallel shard critical path, and 60 minutes for final verification/aggregation:
360 minutes total execution budget, excluding external runner queueing. The controlled runner pool
must supply the declared 36-way capacity for that worst-case critical-path calculation to hold.
Reduced runner concurrency adds queue waves and increases end-to-end elapsed time; it does not
change shard contents or invalidate otherwise complete evidence. The 280-minute limit is enforced
per running shard job, not as a guarantee that the organization will schedule all shards at once.

All 36 shard artifacts are retained separately and downloaded into a retained aggregate artifact.
The final job checks the exact 4,032 request tuples, successful responses, consumer-evidence file
digests, exact Git commit, and provider/form equality of the canonical result digest. It writes
`coverage-verification.json` with `coverageVerified: true` only after every check succeeds. A
missing, timed-out, duplicated, or unequal shard therefore cannot be described as complete
scheduled coverage.

## Running the harness

Run SQLite smoke evidence:

```bash
dotnet run --project benchmarks/Groundwork.PhysicalStorage.Benchmarks -- run --profile smoke
```

Run a narrow scheduled-control diagnostic:

```bash
dotnet run -c Release --project benchmarks/Groundwork.PhysicalStorage.Benchmarks -- run \
  --profile scheduled \
  --providers postgresql \
  --forms entity \
  --workloads indexed-query,mixed-compound-ordering \
  --payload-profiles ordinary-json-v1,storage-growth-1k-v1 \
  --selectivity-bps 1000,5000 \
  --independent-runs 3
```

Run all cases represented by the scheduled scaffold locally (serial, and therefore at least 25.2
hours of mandatory measured time before setup overhead):

```bash
dotnet run -c Release --project benchmarks/Groundwork.PhysicalStorage.Benchmarks -- run \
  --profile scheduled \
  --providers all \
  --forms all \
  --workloads all
```

Server providers use pinned Testcontainers images by default:

- SQL Server: `mcr.microsoft.com/mssql/server:2022-CU21-ubuntu-22.04`
- PostgreSQL: `postgres:17.6-alpine3.22`
- MongoDB: `mongo:7.0.24` with a replica set

For controlled infrastructure, set the relevant variable and pass `--no-containers`:

```text
GROUNDWORK_BENCHMARK_SQLSERVER_CONNECTION_STRING
GROUNDWORK_BENCHMARK_POSTGRESQL_CONNECTION_STRING
GROUNDWORK_BENCHMARK_MONGODB_CONNECTION_STRING
```

Connection strings are not written to artifacts. Metadata records the source description,
database-reported version, isolation strategy, pooling behavior, and session lifecycle.

## Workloads and precise semantics

| Workload | Measured batch |
|---|---|
| `client-reset-point-read-batch` | Clear provider/client pools once, reopen stores, then perform the point-read batch |
| `reused-client-point-read-batch` | Perform a point-read batch through already-open client state |
| `indexed-query` | Repeat the bounded equality query using the declared physical index |
| `mixed-compound-ordering` | Repeat equality queries with descending rank after ascending scope/status keys |
| `insert`, `update`, `delete` | Repeat single-document mutations through the production store |
| `unit-of-work` | Perform batched writes and one commit |
| `concurrent-create` | Repeated synchronized-contention creates for one identity; every contender reaches the same barrier before release, and each wave records one winner plus all remaining conflicts. Public call-window overlap is provider characterization only |
| `optimistic-concurrency` | Stale writes that must return concurrency conflicts |
| `pagination-and-count` | Page and count operations with agreement asserted |
| `backfill-migration` | Time materialization/backfill, then validate projection/query correctness outside timing |
| `client-restart-validation` | Dispose/clear client-side state, recreate it, and verify durable reads |
| `storage-growth` | Writes with the declared `storage-growth-1k-v1` fixed 1 KiB payload profile |

`client-reset-point-read-batch` resets client/provider state once before the batch. It does not flush
the database buffer pool, operating-system page cache, or disk cache, and it is not cold-disk or
individual-cold-read latency.

`client-restart-validation` is limited to client/factory/pool restart. It is not process-crash,
database-crash, power-loss, or disaster-recovery evidence.

## Metric semantics

Each raw sample is one measured target invocation and retains the invocation's aggregate elapsed
time for throughput and steady-state accounting. It also carries `operationLatencyNanoseconds`: one
positive, directly timed observation for every operation reported by the target. Summary
`operationLatencyP50Nanoseconds`, p95, and p99 values flatten those raw observations within one
worker; they never divide an invocation duration by its operation count. Run-group acceptance keeps
workers as independent process clusters: it computes each process statistic, uses the median of the
independent processes, and resamples processes before resampling observations within a selected
process.

An operation is the smallest semantically complete unit that the workload promises:

- point-read batch: the complete reused-client or reset-client batch (including reset when selected);
- indexed/mixed query, insert, update, delete, stale write, and storage-growth: one store call;
- unit of work: one begin/save-batch/commit transaction;
- concurrent create: one competing create attempt. Every retained concurrent-create sample seals its requested parallelism, wave count, released-together wave count, attempts, completions, successful/conflict outcomes, and observed peak in-flight public production-store calls. Every contender must reach the same barrier before release, every wave must release all contenders together, and every outcome must be accounted. The sample operation count equals its attempts, the latency inventory equals that operation count, and the wave count equals the configured operations per iteration. The observed call-window peak is retained as provider characterization in `[1, N]`; it is not an eligibility gate and does not claim physical database overlap. Such an overlap claim requires provider-specific instrumentation at a lower execution boundary;
- pagination and count: one page query or one count query;
- backfill: one complete materialization/backfill application;
- client restart validation: one client/factory/pool restart plus its durable-read validation batch.

The scheduled process therefore continues whole invocations until it has at least 100 of these raw
operation observations and at least 30 seconds of measured target execution. Reports also contain
aggregate throughput, allocation per operation, observable round trips, net storage growth per
logical payload byte, net physical-row growth per logical mutation, provider work signals, and
native-plan evidence where observable. These are net cardinality/storage ratios, not database
write-amplification measurements. A missing round-trip signal is `null`, never zero.

Consumer evidence binds this behavior as measurement protocol
`direct-operation-latency/v1`; the protocol participates in each workload fingerprint so evidence
produced by the former batch-mean implementation cannot compare as the same workload evidence.

Regression comparisons consume a coordinator run-group root, never a single warm-up or measured
worker directory. Candidate and baseline measured workers are matched by provider, storage form,
workload, complete data shape, and independent-run number. Scheduled comparisons reject tuples
with fewer than three independent measured processes. Current evidence remains non-promotable
because its evidence readiness is insufficient and its baseline eligibility is false. The
committed baseline registry is empty and disabled.

## Artifact contract

```text
run-group.json
protocol/requests/<ordinal>.json
protocol/responses/<ordinal>.json
reports/regression.json (when --baseline is supplied)
runs/<ordinal>/manifest.json
runs/<ordinal>/metadata/configuration.json
runs/<ordinal>/metadata/machine.json
runs/<ordinal>/metadata/providers.json
runs/<ordinal>/plans/<provider>/<form>/<workload>-<selection|count>.<native-extension>
runs/<ordinal>/plans/<provider>/<form>/<workload>-<selection|count>.<native-extension>.assertions.json
runs/<ordinal>/raw/measurements.jsonl
runs/<ordinal>/reports/summary.json
runs/<ordinal>/reports/summary.md
runs/<ordinal>/reports/regression.json
runs/<ordinal>/reports/elsa-migration-evidence.json
runs/<ordinal>/reports/consumer-evidence.json
```

The v1 JSON Schemas live in [`schemas/v1`](schemas/v1). The evidence report deliberately exposes:

- `readiness: insufficient`;
- `elsaEfOracleRequired: true`;
- `baselineEligibility.eligible: false` with concrete diagnostics;
- Groundwork case evidence and diagnostic regression signals; and
- `remainingAcceptanceWork` for the later Elsa-owned evidence join.

No artifact in this slice is a migration decision or baseline-promotion authorization.

Every native-plan file has a required versioned `.assertions.json` sibling. The sidecar records the
canonical typed request, plan identity, a redacted actual-command receipt, parsed filter/order/
terminal/pagination shape, parameter roles, and the 1,000 or 5,000 basis-point policy. The verifier
reparses that receipt and rejects a substituted selection/count, ordering, page, physical object,
or provider-plan pairing even if the artifact-integrity tree has been resealed. It does not claim
to turn the local integrity ledger into an external provenance signature.

The preceding warm-up worker executes the configured untimed warm-up iterations as a preflight and
emits no consumer evidence. Each independent measured worker also executes its own configured
untimed warm-up iterations against the same target instance before timing begins, so process-local
JIT and target state are warm without admitting warm-up samples. Measured workers continue
writing whole raw samples until the iteration, operation-count, and steady-state execution-duration
floors are all satisfied; setup, schema materialization, seeding, correctness, and validation time
do not contribute to the duration floor.

`consumer-evidence.json` deliberately omits provider configuration values. It records a digest of
the redacted provider configuration plus workload identity/version/fingerprint, provider identity
and version, storage form, data shape, raw-sample digest, measurement digest, native-plan digest,
and a provider/machine-independent correctness result digest.

That correctness digest is SHA-256 over an ordered
`groundwork.physical-storage.observable-result/v1` vector. Vector entries carry canonical sequence,
stable identity, status, version, count, and payload outcomes. Provider identity/version,
configuration, storage form, machine metadata, timestamps, and timings do not participate. The
scheduled aggregate requires equality for every matching workload/data-shape group across all
providers, forms, and independent runs before the timing artifacts are accepted as complete
scaffold evidence. Elsa #646 can join on those fields without Groundwork embedding Elsa or EF
domain code.

The coordinator binds every worker request to the expected Git commit and worktree digest. The
run-group manifest records SHA-256 digests for every request, response, worker manifest, Elsa
evidence report, and measured consumer-evidence report. The verifier rejects path escapes, unknown
JSON members, non-canonical artifact slots, symbolic-link/reparse-point traversal, identity
mismatches, Git drift, and digest mismatches before a group can be used as a baseline. Scheduled
and regression consumers read the same canonical files that those digests bind. Connection strings
and provider secrets remain excluded.

The aggregate's `coverage-verification.json` has its own strict v1 schema. It records the exact
provider/form/data-size/selectivity/workload/repetition matrix and a SHA-256 digest over that
canonical matrix claim. Its verification mode distinguishes the complete deep scheduled-scaffold
path from the explicitly narrowed, matrix-only test fixture. The `--run-id` argument locates the
expected shard directories; it is not serialized as attested workflow-run provenance. Both modes
remain non-promotable: this is a closed execution-coverage claim, not a baseline, attestation, or
performance verdict.

## Target-scoped database-work signals

Every measured raw sample now carries a sealed `databaseSignal` object. SQLite and SQL Server
accept provider diagnostic command starts only when the command is bound to the disposable measured
database target. PostgreSQL uses a target-specific `Application Name` on every production-path
connection so its diagnostic commands cannot be confused with another schema sharing the same
server database. MongoDB configures the production target client with the driver's public
`CommandStartedEvent` subscriber and accepts a command only when its database namespace matches the
measured target. The target selector and all connection values remain in memory; artifacts contain
only the provider-neutral source, availability, and positive counts.

These are observable client command/activity signals, not a claim that the harness has exact
wire-level server round-trip accounting. `roundTrips` is written exclusively from the matching
target-scoped signal snapshot; a target-reported compatibility counter cannot fill a telemetry
gap. If a provider does not expose the relevant public telemetry during a workload, `roundTrips`
and every signal count are `null`, with an explicit `unavailable` reason and no synthetic zero.
Raw measurement digests, artifact-integrity ledgers, consumer-evidence reconstruction, and
scheduled-worker raw/summary equality bind those signals to the exact run group.
Those hashes prove internal consistency and detect mutation relative to the retained ledger; they
are not an authenticity root against an actor that can replace and re-seal the entire directory.
Baseline promotion therefore still requires trusted external CI provenance or attestation.

Machine metadata records CPU model, memory, storage/filesystem capacity, and power/governor state
when the host exposes them, otherwise the literal `unavailable`. Provider metadata distinguishes
declared configuration from effective settings and explicitly marks settings unavailable when the
target cannot query them. Container sources include an immutable image digest when available and
otherwise record `immutableDigest=unavailable`.

## Independent review record

Three adversarial reviewers examined the initial candidate from base `c6d40b589a9296b2ada461caf6b4b0d58da401bb`
through `a7dea39d3c44809d32ff6c4313c6399424cc72e6` on distinct axes. All three blocked it:

- correctness/mechanism found that server-provider targets bypassed production factory admission
  and native-plan capture used a different, noise-inflated distribution;
- evidence integrity found weak correctness digests and provenance, incomplete group schemas and
  metadata, flattened-process statistics, and baseline comparison that did not require exact tuple
  equality; and
- scope/test preservation found that a serial 12.6-hour protocol could not fit the six-hour
  workflow, group verification was incomplete, and child exit status was not propagated.

The candidate was remediated by using the production factories and the same measured shape for
native plans; emitting canonical observable-result vectors from real outcomes for all 14
workloads; enforcing exact tuple/run identity, hierarchical process-first bootstrap statistics,
strict group schemas/digests, and nonzero child-exit propagation; and sharding the scheduled matrix
into 36 provider/form/cardinality jobs with an exact 4,032-worker aggregate verifier. The pull
request smoke remains deliberately narrow and every workflow artifact remains non-promotable.

The originating reviewers re-verified the frozen remediation tree at `c69e7eefc14c04ceec2416d984cc5d51797d757c`.
All three final verdicts were **CLEAN**:

- **Correctness and mechanism — addressed:** the benchmark target contract now requires
  a complete `BenchmarkDataShape` for seeding, and the runner regression double records the full
  shape for both the measured and isolated plan targets.
- **Evidence integrity — addressed:** hosted run `30141568325` passed 200 tests and all 12 smoke
  workers. Its retained artifact contains the warm-up and measured `storage-growth` workers bound
  to `storage-growth-1k-v1`, with 1,024 declared padding bytes, exact-tree provenance, matching
  internal digests, and `promotable: false`. The SQLite target test independently checks the
  returned and directly persisted padding against the profile's exact UTF-8 byte count.
- **Scope and test preservation — clean:** the pull-request smoke remains SQLite/shared-form and
  non-decision; scheduled four-provider cardinality remains 4,032; no test or provider path was
  removed or weakened; immutable-baseline eligibility remains disabled; and no #50 completion is
  claimed.

A second three-axis review examined the target-scoped database-work signal follow-up from
`ba8e87f7a48d8fbfbe1db260f0472af15c6c2387` through
`c7f9137b0088015724298190af4ffe2eec233dea`. It initially found that callers could override an
unavailable observation and that SQL Server/PostgreSQL lacked positive selector and runner-path
coverage. After remediation, the originating reviewers returned **PASS**:

- **Correctness and mechanism — addressed:** the runner accepts only the scoped snapshot's
  observable count; persisted diagnostic-command and client-activity evidence states are exclusive,
  and their authoritative count must agree exactly with the persisted round-trip count; unavailable
  evidence cannot claim a count.
- **Evidence integrity — addressed:** writer, reader, summarizer, report reconstruction, JSON
  Schema validation, and fully resealed scheduled-group verification reject internally
  inconsistent signal counts. This is an internal-consistency claim, not external authenticity.
- **Scope and test preservation — addressed:** SQL Server and PostgreSQL now have positive and
  negative selector coverage, all four provider runner paths are exercised without retaining
  selector values, no provider/workload/form declaration or worker protocol was removed, and live
  controlled evidence remains explicitly outstanding.

The focused verification suite passed 74 tests and `git diff --check` remained clean.

The final exact-head audit then found two additional blockers. Evidence review proved that a
diagnostic signal could retain a conflicting lower-precedence activity count and survive complete
artifact resealing. Correctness review proved that MongoDB's runner-only synthetic activity test
did not bind telemetry to the production target client. Both findings were remediated:

- persisted signal states are now exclusive, every authoritative count must equal `roundTrips`,
  and writer/reader plus fully resealed scheduled-group tests reject the forged dual-signal vector;
- MongoDB now subscribes to the driver's public `CommandStartedEvent` on the target-owned client,
  passes that client's database through the production `CreatePhysicalAsync` admission overload,
  recreates the instrumented client for reset/restart, and uses it for measured backfill work; and
- a real replica-set runner test covers indexed query, client reset, backfill, and client restart
  while proving positive exact signal equality and absence of connection/target values in raw
  evidence.

The originating correctness, evidence-integrity, and scope/test-preservation reviewers all returned
**PASS** on `63e166d62d6dd56bc00c12d2c860fc6d71fa77aa`. A standards follow-up also identified duplicated
integration-test workspace setup and duplicated provider-source classification; both were extracted
into single shared definitions without changing the protocol or evidence schema. The full benchmark
test project passed 227 tests, including the live MongoDB evidence test, and the hosted SQLite
non-decision smoke remained green.

## Remaining issue #50 acceptance work

- Execute the ratified 10% indexed-query acceptance and 50% scan-characterization shapes across
  the 1K/100K/1M dataset matrix for the reviewed workload-bound payload profiles.
- Capture exact-HEAD live evidence from SQLite, SQL Server, PostgreSQL, and MongoDB.
- Exercise the target-scoped provider database-work signals and sealed concurrent-create evidence
  under controlled live evidence for all providers. The current observable client signals do not
  by themselves close the provider-work acceptance item.
- Operationalize the reviewed-main approval trust input, execute the controlled matrix, populate
  and activate eligible append-only registry generations, and exercise committed selection.
- Extend the bounded SQLite process-failure slice to the remaining providers and any additional
  ratified crash/failure modes required for issue #50. The current slice is real process
  termination evidence, but it is deliberately non-promotable and does not close that acceptance
  work by itself.

Until all applicable items are ratified and complete, the harness stays non-promotable and
non-decisional.

## Elsa consumer prerequisites

Groundwork #50 publishes provider-native evidence and never takes an EF dependency. Elsa #646 owns
the matched EF Core oracle join, physical-form benefit verdicts, and any additional payload profiles
needed by its frozen workloads. Those consumer decisions do not block delivery of a complete,
immutable Groundwork baseline, but they do block an Elsa migration verdict.
