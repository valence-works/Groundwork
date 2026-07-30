# Implementation Plan: Fenced Relationship Transition

**Branch**: `codex/141-sqlite-transition` | **Date**: 2026-07-29 | **Spec**: [spec.md](./spec.md)

## Summary

Implement the first executable #141 provider slice as an internal SQLite relationship-transition
engine. It will materialize candidate reference and target-fence state, validate legacy references,
persist phase/progress, atomically cut over one validated candidate, and recover after reopen while
the public relationship admission gate remains closed.

## Technical Context

**Language/Version**: C# / .NET 10

**Primary Dependencies**: Groundwork Core physical relationship plans, relational physical schema
execution, SQLite provider, existing transition requirement and dangling-diagnostic contracts

**Storage**: Provider-owned SQLite transition, reference-sidecar, target-fence, and active-generation
state

**Testing**: xUnit, durable SQLite files, distinct provider instances, exact SQL/schema inspection

**Constraints**: No public capability advertisement; no raw provider workaround; no client-side
relationship validation; no production diagnostic-key configuration surface in this slice

## Constitution / Ratification Check

| Gate | Status | Note |
|---|---|---|
| Closed guard family | PASS | Uses only the two #141-ratified relationship declarations. |
| Fail-closed provider admission | PASS | Public `GW-RELATIONSHIP-012` remains unconditional. |
| Atomic transition | PASS | Candidate cannot become active before complete validation. |
| Diagnostic privacy | PASS | Existing candidate-bound HMAC correlation is reused; raw values are never retained. |
| Capability certification | PASS | SQLite evidence is internal and cannot advertise four-provider support. |
| No workaround | PASS | Transition is provider-owned; Elsa and callers do not orchestrate query-then-delete. |

## Project Structure

```text
specs/022-fenced-relationship-transition/
├── spec.md
├── plan.md
├── tasks.md
├── quickstart.md
└── checklists/
    └── requirements.md

src/Groundwork/Sqlite/PhysicalStorage/
├── SqliteRelationshipTransitionExecutor.cs
└── SqliteRelationshipTransitionState.cs

tests/Groundwork/Groundwork.Sqlite.Tests/
└── SqliteRelationshipTransitionTests.cs
```

The exact source filenames may be consolidated during implementation when an existing relational
helper is the correct shared home. Any shared relational extraction must remain provider-neutral and
must not enable SQL Server/PostgreSQL admission without their own evidence.

## Delivery Slices

1. Durable SQLite candidate/active transition state and generated sidecar/fence schema.
2. Backfill plus dangling validation through the compiled relationship plan.
3. Compare-and-swap cutover, replay, cancellation, and distinct-instance recovery.
4. Internal test-only admission and comprehensive provider tests.
5. Independent three-axis review and Model B merge while #141 remains open.

## Deliberate Nonclaims

This plan does not implement ordinary write-fence maintenance, guarded bulk prune execution,
provider-native correlated plans, production key custody, non-SQLite providers, or capability
advertisement.
