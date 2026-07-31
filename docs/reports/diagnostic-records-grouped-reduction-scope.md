# Diagnostic-records grouped reduction: scope review

Status: closed — removed 2026-07-31. This note records the evidence and the conditions under which
the capability should return.

Related: [ADR 0004](../adr/0004-retire-groundwork-operational.md).

## Decision

The grouped-reduction contract and its four provider executors have been removed. Groundwork's
diagnostic-record store now exposes four bounded operations — append, query, inspect, trim.

## Evidence

`Groundwork.DiagnosticRecords` exists to serve Elsa Foundation's structured-log and OpenTelemetry
stores. That workload is specified in the consumer's `docs/reports/diagnostics-storage-workload.md`,
dated 2026-07-12, which enumerates the required predicate set and then states:

> "No current Elsa caller requires numeric rollups, grouping, generic reduce, or map/reduce."

Grouped reduction was added to Groundwork on 2026-07-24 (`feat(diagnostics): define grouped reduction
contract`, commit `774085e`) — twelve days after that statement, with no corresponding update to the
consumer workload report.

Consumer-side inspection found no grouped-reduction profile declared in the Groundwork diagnostics
adapter (`src/Elsa/Diagnostics/Persistence/Groundwork`) or in the OpenTelemetry Groundwork persistence
project, whose files are binding, schema, store, codec, and feature registration. No call site for
`QueryGroupsAsync` was located.

This is strong but not conclusive: the concrete stream definitions live in
`GroundworkOpenTelemetryPersistenceFeature` and `GroundworkStructuredLogsPersistenceFeature`, whose
bodies were not read. The most plausible future requirement remains an OpenTelemetry trace-list
endpoint — grouping spans by trace id and reducing to first/last timestamp, span count, and a bounded
service-name set is close to what the removed reducers computed.

The removal was made on the principle that a specialized contract is justified by a named workload,
which is the same principle this package's README uses to justify its own existence. Grouped
reduction did not have one on record.

## What was removed

- `DiagnosticGroupedReduction.cs` (563 lines) and `RelationalDiagnosticGroupQueryBuilder.cs` (539).
- `IDiagnosticRecordStore.QueryGroupsAsync`, `IDiagnosticGroupedQueryHandler`,
  `DiagnosticGroupedQueryHandlerCapabilities`, and the grouped slot on `DiagnosticRecordStoreHandlers`.
- `DiagnosticRecordStreamDefinition.GroupReductionProfiles` and
  `DiagnosticRecordLimits.MaxGroupedQueryInputRecords`.
- `IDiagnosticRecordPlanInspector.InspectGroupedQueryAsync` and the `GroupedQuery` plan operation.
- The `query_groups` activity, its three tags, and its operation constant.
- Native grouped executors in all four providers, including MongoDB's typed aggregation pipeline with
  its overflow facet and the relational grouped SQL builder.
- `DiagnosticGroupedReductionContractTests` (348 lines) and the grouped sections of the conformance
  suite and the four provider diagnostic suites.

## Breaking change: stream-definition fingerprints

`DiagnosticRecordPhysicalSchemaState` wrote a `groupReductionProfiles` array into every stream's
canonical definition, so it contributed to the definition fingerprint even when no profiles were
declared. Removing it changes the canonical JSON and therefore the fingerprint for **every** stream,
not only streams that used grouping.

An already-deployed stream will fail its persisted-definition compatibility check after this change
and must be resolved through explicit schema evolution. This is acceptable at `0.0.1-preview` while
the only consumer is mid-adoption, but it is a real migration and must not be treated as inert.

## Conditions for return

Reintroduce grouped reduction only with a named consumer requirement recorded in the consumer's
workload document — for example a concrete OpenTelemetry trace-list endpoint with its declared
reducers, ordering, and page size. The removed implementation is recoverable from git history at the
commit preceding this change.
