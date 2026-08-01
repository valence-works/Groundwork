# Diagnostic-records grouped reduction: scope review

Status: closed — **retained** 2026-07-31. The capability was removed earlier the same day on the
premise that it had no consumer; the consuming repository was then read directly and the premise was
false. This note records the named workload that justifies the contract, so it is not proposed for
removal again on the same reasoning.

Related: [ADR 0004](../adr/0004-retire-groundwork-operational.md).

## Decision

Grouped reduction stays. `Groundwork.DiagnosticRecords` exposes five bounded operations — append,
query, grouped query, inspect, trim — and the four production providers keep their native grouped
executors.

## The named workload

`Groundwork.DiagnosticRecords` exists to serve Elsa Foundation's structured-log and OpenTelemetry
stores. The OpenTelemetry store uses grouped reduction on its read path, in `elsa-foundation` at
`0.0.1-preview.103`:

| Consumer site | Operation |
|---|---|
| `src/Elsa/Diagnostics/OpenTelemetry/Persistence/Groundwork/GroundworkOpenTelemetryStore.cs:453` | `QueryTracesAsync` — the trace-list endpoint: filter, page, sort by `StartTime` descending |
| `…/GroundworkOpenTelemetryStore.cs:490` | `GetTraceAsync` — trace detail, single group by trace id |
| `…/Records/OpenTelemetryRecordStreamDefinitions.cs:142` | declares the `TraceSummaryProfile` |

The profile groups span records by `TraceId` and declares ten reducers:

- `FirstBy` on `TraceId`, `RootSpanId`, and `Name`, each ordered by `StartTime` ascending with the
  cursor-ascending tie-break;
- `MinTimestamp` on `StartTime` and `MaxTimestamp` on `EndTime`;
- `MaxInt64` on `Status`;
- `SetUnionString` on `ResourceId`, `ServiceName`, and `WorkflowInstanceId`;
- `SumInt64` on `SpanCount`.

Its post-reduction predicate admission covers equality on `TraceId`, `ResourceId`, `ServiceName` and
`Status`, and `Contains` on `TraceId`, `Name` and `WorkflowInstanceId`. The stream definition also
sets `MaxGroupedQueryInputRecords` to the trace capacity
(`OpenTelemetryRecordStreamDefinitions.cs:127`) and passes `GroupReductionProfiles` at line 136.

The consumer additionally exercises `InspectGroupedQueryAsync` in
`tests/Elsa/Diagnostics/Persistence/Tests/DiagnosticsBoundedExecutionTests.cs` and records native
grouped plans on all four providers in
`specs/139-groundwork-diagnostics-persistence/evidence/preview{86,88,102}-*.json`.

Removing the contract does not merely deprive Elsa of a feature — it stops `elsa-foundation`
compiling, because `GroupReductionProfiles` and `MaxGroupedQueryInputRecords` are named directly in
its stream definitions.

## Why the removal was proposed, and why the evidence was insufficient

The consumer's workload report `docs/reports/diagnostics-storage-workload.md`, dated 2026-07-12,
states:

> "No current Elsa caller requires numeric rollups, grouping, generic reduce, or map/reduce."

Grouped reduction landed in Groundwork on 2026-07-24 (`feat(diagnostics): define grouped reduction
contract`, commit `774085e`) — twelve days later, with no corresponding update to that report. The
absence looked like speculative scope.

It was not. The trace-summary endpoint was built in the interval and the workload report was never
revised. That report is a design input dated before the requirement existed; it was read as a
current inventory of consumer needs, which it is not.

The scope review that proposed removal named its own gap accurately — it recorded that the concrete
stream definitions "were not read", and that the most plausible unfound requirement was "an
OpenTelemetry trace-list endpoint — grouping spans by trace id and reducing to first/last timestamp,
span count, and a bounded service-name set". That is exactly what exists. The conditions that review
set for the capability's return are met by the table above.

**Lesson.** Absence of a requirement in a consumer's design document is not absence of the
requirement in the consumer's code. A contract may only be retired against a read of the consuming
source at the version that consumes it — dated prose is evidence of intent at its date, and nothing
more. Where the two disagree, the code wins and the document is the thing that needs fixing.

## Follow-up

The stale statement in the consumer's workload report is corrected separately in `elsa-foundation`,
recording the trace-summary reducers, grouping key, ordering, and page size as a named requirement.
Until that lands, this note is the authoritative record.
