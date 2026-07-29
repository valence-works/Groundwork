# Benchmark artifact schemas v1

These schemas define the stable interchange surface for Groundwork physical-storage benchmark
automation:

- `run-manifest.schema.json` describes run status and artifact locations.
- `raw-measurement.schema.json` describes one line of `raw/measurements.jsonl`, including the
  directly timed per-operation latency observations used for percentiles and bootstraps, plus
  target-scoped provider database-work telemetry and workload-conditioned concurrent-load
  evidence. Concurrent-create records retain only counts (configured parallelism, complete
  released-together waves, attempts, completions, outcomes, and peak in-flight public
  production-store calls). Each sample's operations equal its attempts, its latency inventory,
  and its configured wave count multiplied by parallelism. The peak is provider characterization, not a universal overlap gate;
  records never infer physical overlap from configuration or retain payloads or connection values. The telemetry captures only its source,
  availability, and positive counts; it never includes connection values, database names, command
  text, or provider secrets. A signal that cannot be observed is represented by `null` counts and
  an explicit `unavailable` reason, never by a zero measurement.
- `elsa-migration-evidence.schema.json` describes explicitly insufficient Groundwork-only evidence;
  it is not an Elsa migration decision.
- `worker-invocation.schema.json` describes the immutable parent-to-worker subprocess request.
- `worker-response.schema.json` describes the Git-bound response and artifact digest set.
- `run-group.schema.json` describes the coordinator manifest and integrity index.
- `run-group-regression.schema.json` describes process-cluster comparison results.
- `consumer-evidence.schema.json` describes redacted evidence joinable by workload, provider, form,
  version, fingerprint, complete payload profile/data shape, result digest, and (for concurrent
  create) a reconstructed concurrent-load evidence digest. It is always non-promotable until the
  external EF oracle is joined.
- `native-plan-assertions.schema.json` describes the required `<native-plan>.assertions.json`
  sidecar. It binds a canonical benchmark request to a parsed provider-native plan and a redacted
  receipt of the actual provider command: relational SQL is admitted in memory and represented by
  a SHA-256 command identity plus typed parameter roles and structural pagination only; MongoDB
  retains a redacted `find`/`aggregate`/`count` command with terminal,
  filter fields/operators, exact semantic-plus-scope-plus-identity sort binding, skip, and limit.
  Provider plans retain only allowlisted structural operator/relation/index topology and safe
  numeric estimates. The sidecar is validated on write and
  read, rejects unknown members and schema versions, and records 1,000 versus 5,000 basis-point
  selectivity. It proves internal consistency of a sealed artifact tree, not external command
  provenance or authenticity.

Batch elapsed time remains available for throughput and steady-state accounting. Latency
percentiles and confidence intervals consume `operationLatencyNanoseconds` only; a normalized batch
mean is not a latency observation.

This software and contract are unreleased. After v1 is released, a breaking rename, meaning change,
enum removal, or required-field change will require a new schema directory and an explicit
converter. Consumers must reject unknown major schema versions rather than guessing.
