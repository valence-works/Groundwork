# Stable-Order Native-Plan Failure Reproduction

Date: 2026-07-30

Source:
`c48b5a1d04c2664211af1f14d403e3f0391846ca`

Provider:
Testcontainers `mongo:7.0.24`, replica set `groundwork-rs`

Physical form:
`SharedDocuments`

Canonical invocation from a clean detached worktree:

```bash
dotnet run -c Release --project benchmarks/Groundwork.PhysicalStorage.Benchmarks -- run \
  --profile smoke \
  --providers mongodb \
  --forms shared \
  --workloads mixed-compound-ordering \
  --dataset-sizes 250 \
  --selectivity-bps 1000 \
  --independent-runs 1 \
  --output <empty-output-directory>
```

The untimed warm-up worker completed. The measured worker then materialized and seeded its isolated
native-plan target and failed in `CaptureNativePlansAsync` before it created the measurement target
or executed a timed operation.

Redacted failure:

```text
MongoDB native-plan gate rejected MixedCompoundOrdering/Selection.
Expected IXSCAN '<normalized by-status-rank name>'.

winningPlan:
  SORT rank DESC, storage_scope ASC, document_id_comparison_key ASC
    FETCH
      IXSCAN <normalized by-status name>

rejectedPlan:
  SORT rank DESC, storage_scope ASC, document_id_comparison_key ASC
    FETCH
      IXSCAN <normalized by-status-rank name>
```

The declared `by-status-rank` index contained scope, status, and rank, but not the runtime
comparison-key identity tail. MongoDB therefore could not satisfy the complete stable order from
either candidate index and introduced a blocking sort. The strict gate failed before timing, so
this record contains no latency, throughput, or performance verdict. Ephemeral database, container,
host, connection, and generated physical-name values are deliberately omitted.
