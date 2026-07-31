# Diagnostic-records grouped reduction: scope review

Status: open question. Requires a consumer-side answer before any removal.

Related: [ADR 0004](../adr/0004-retire-groundwork-operational.md),
[Groundwork runtime evaluation](groundwork-runtime-evaluation.md).

## The question

**Which consumer call site executes `IDiagnosticRecordStore.QueryGroupsAsync`?**

If the answer is a real endpoint, this note closes as "keep" and the consumer workload report should
be updated to record the requirement. If the answer is "none", the grouped-reduction contract and its
four provider executors are the largest single removable feature in the repository.

## Why the question is open

`Groundwork.DiagnosticRecords` exists to serve Elsa Foundation's structured-log and OpenTelemetry
stores. That workload is specified in the consumer's `docs/reports/diagnostics-storage-workload.md`,
dated 2026-07-12, which enumerates the required predicate set and then states:

> "No current Elsa caller requires numeric rollups, grouping, generic reduce, or map/reduce."

Grouped reduction was added to Groundwork on 2026-07-24 (`feat(diagnostics): define grouped reduction
contract`, commit `774085e`), twelve days after that statement. No corresponding update to the
consumer workload report has been observed, and the consumer's Groundwork diagnostics adapter
(`src/Elsa/Diagnostics/Persistence/Groundwork`) declares no grouped-reduction profiles at the
manifest level — although the concrete stream definitions live in
`GroundworkOpenTelemetryPersistenceFeature` and `GroundworkStructuredLogsPersistenceFeature`, which
were not inspected.

This is suggestive, not conclusive. Two readings are consistent with the evidence:

1. **A requirement emerged after 2026-07-12.** The most plausible candidate is an OpenTelemetry
   trace-list endpoint: grouping spans by trace id and reducing to first/last timestamp, span count,
   and a bounded service-name set is very close to what the shipped reducers compute (`FirstBy`,
   timestamp min/max, `Int64` sum, bounded string-set union). If so, keep the feature and correct the
   workload report.
2. **The feature was built ahead of demand.** If so, it is speculative generality of the same kind
   ADR 0004 records for `Groundwork.Operational`, and it should be removed while the removal is still
   cheap.

## What is in scope if the answer is "none"

Dedicated files:

| File | Lines |
|---|---|
| `src/Groundwork/DiagnosticRecords/DiagnosticGroupedReduction.cs` | 563 |
| `src/Groundwork/DiagnosticRecords.Relational/RelationalDiagnosticGroupQueryBuilder.cs` | 539 |

Plus the grouped paths inside `DiagnosticRecordStore`, `RelationalDiagnosticRecordStore`,
`MongoDbDiagnosticRecordStore` (typed aggregation pipeline with an overflow facet),
`DiagnosticRecordTelemetry` (the `query_groups` activity and its instruments),
`DiagnosticRecordDeploymentManifest` (profile declarations), grouped continuations, and the
per-provider grouped conformance coverage — including the dedicated
`DiagnosticGroupedReductionContractTests` (348 lines) and grouped sections of the four provider
diagnostic suites.

Estimated total: roughly 1,500–2,000 lines of `src` and a comparable amount of test code.

## Recommended disposition

Treat "keep" as requiring positive evidence. The feature is well-built — named profiles rather than
generic map/reduce, closed reducers, overflow detection instead of truncation, and native execution
on all four providers — but its own package README's design principle is that a specialized contract
is justified by a named workload. Grouped reduction currently does not have one on record.

Do not remove it as part of the ADR 0004 change set. It is a surgical removal across four provider
implementations and should land as its own reviewed change, after the question above is answered.
