# Implementation Plan: Groundwork Physicalization And Performance

**Branch**: `codex/groundwork-physicalization-performance` | **Date**: 2026-06-10 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `specs/019-groundwork-physicalization-performance/spec.md`

## Summary

Implement G7 by extending Groundwork's existing manifest/planner/provider stack with opt-in optimized physicalization for declared single-field equality indexes. Portable units keep the generic document/index tables and MongoDB content-path indexes. Optimized units additionally project eligible index values into provider-native physical structures, and provider stores route eligible equality queries through those structures without changing `IDocumentStore`. The issue #50 evidence-completion amendment adds a bounded, real process-failure/recovery protocol and SQLite proof while preserving the harness's non-promotable state. Its native-plan amendment also makes scale-bearing index certification cover Groundwork's complete stable provider order, including the runtime identity tie-break, and proves the corrected MongoDB ordered form without weakening the strict gate.

## Technical Context

**Language/Version**: C# / .NET 10 (`net10.0`)

**Primary Dependencies**: Existing `Groundwork.Core`, `Groundwork.Documents`, `Groundwork.Relational`, `Groundwork.Sqlite`, `Groundwork.MongoDb`

**Storage**: Groundwork portable document storage with opt-in optimized projections

**Testing**: xUnit tests with SQLite in-memory provider, durable-file cross-process SQLite recovery,
and Testcontainers MongoDB

**Target Platform**: Groundwork provider packages inside standalone Groundwork

**Project Type**: Library/provider framework

**Performance Goals**: Correctness of optimized physical query path, exact bounded recovery
evidence, and strict native-plan proof without provider-side blocking sort; controlled performance
promotion remains gated by issue #50

**Constraints**: Portable default remains unchanged; no caller API changes; host-specific concepts cannot leak into generic Groundwork packages; optimized projections must honor optimistic concurrency

**Scale/Scope**: Physicalization plan metadata, relational optimized projections for SQLite
validation, MongoDB optimized projections, provider tests, one non-promotable SQLite
process-failure recovery slice, and complete stable-order certification for the benchmark's bounded
offset-query forms without weakening unique business-key constraints

## Constitution Check

| Gate | Status | Note |
|---|---|---|
| Framework §2.1 three-layer separation | PASS | Manifest vocabulary stays generic; host integration bridge is not involved. |
| Framework §2.9 persistence invariants provider-neutral | PASS | `IDocumentStore` remains the caller contract. |
| Framework §2.20 provider module decomposition | PASS | Provider-specific physicalization stays in provider packages. |
| Runtime migration guardrail | PASS | Workflow runtime stores remain out of scope. |
| Framework §2.23 tests | PASS | SQLite and MongoDB provider-backed tests prove optimized behavior. |
| Issue #50 evidence integrity | PASS | Recovery runs in distinct processes, binds exact source and recovered state, and remains non-promotable. |
| Scale-bearing native-plan integrity | PASS | Certified indexes cover the full provider-applied order; strict MongoDB evidence rejects collection scans and blocking sorts. |

No justified violations.

## Project Structure

### Documentation (this feature)

```text
specs/019-groundwork-physicalization-performance/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── optimized-physicalization.md
├── checklists/
│   └── requirements.md
└── tasks.md
```

### Source Code

```text
src/Groundwork/Core/
├── Manifests/StoragePolicies.cs
├── Materialization/MaterializationPlan.cs
└── Physicalization/
    ├── PhysicalizationProjection.cs
    └── PhysicalizedFieldPlan.cs

src/Groundwork/Documents/Planning/
└── DocumentManifestPlanner.cs

src/Groundwork/Relational/
├── Documents/RelationalDocumentStore.cs
├── Documents/RelationalDocumentStoreDialect.cs
├── Materialization/RelationalMaterializerBase.cs
└── Physicalization/RelationalPhysicalizationNames.cs

src/Groundwork/MongoDb/
├── Documents/MongoDbDocumentStore.cs
└── Materialization/MongoDbGroundworkMaterializer.cs

tests/Groundwork/Groundwork.Tests/
├── PlannerContractTests.cs
└── PhysicalizationProjectionTests.cs

tests/Groundwork/Groundwork.Sqlite.Tests/
└── SqliteOptimizedPhysicalizationTests.cs

tests/Groundwork/Groundwork.MongoDb.Tests/
└── MongoDbOptimizedPhysicalizationTests.cs

benchmarks/Groundwork.PhysicalStorage.Benchmarks/
├── Recovery/
│   ├── RecoveryProtocol.cs
│   └── SqliteRecoveryWorker.cs
└── schemas/
    └── v1/
        └── recovery-evidence.schema.json

tests/Groundwork/Groundwork.PhysicalStorage.Benchmarks.Tests/
├── SqliteProcessFailureRecoveryTests.cs
├── BenchmarkModelFactoryTests.cs
├── MongoWinningPlanInspectorTests.cs
└── MongoDbBenchmarkSignalEvidenceTests.cs
```

**Structure Decision**: G7 extends existing Groundwork core/provider packages. The issue #50
amendment stays inside the core physical-storage compiler/resolver plus the benchmark executable and
tests; it does not introduce a provider package or host integration dependency.

## Complexity Tracking

No constitution violations or complexity exceptions are introduced in G7.
