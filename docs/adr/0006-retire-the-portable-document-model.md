# Retire the portable document model

Status: accepted (2026-08-12).

Date: 2026-08-12.

Related: [ADR 0003](0003-adopt-three-physical-storage-forms.md) (the physical storage forms this
ADR completes the transition to), [ADR 0005](0005-separate-kernel-facilities-from-contract-families.md)
(the kernel/contract-family split that follows this retirement),
[Groundwork vocabulary and public API](../reports/groundwork-vocabulary-and-public-api.md).

## Context

ADR 0003 adopted three physical storage forms and the route-driven runtime: manifests declare
bounded queries, `PhysicalStorageResolver` and `ExecutableStorageRouteCompiler` compile certified
routes, and the physical document stores execute exactly those routes. The portable model — the
shared `groundwork_documents`/`groundwork_document_indexes` tables served by
`RelationalDocumentStore` and `MongoDbDocumentStore` — was the transition's starting point, and the
`GW0001–GW0004` obsolete markers already retired its declaration and query types
(`IndexDeclaration`, `PortableQueryDeclaration`, `PortableDocumentQuery`, `DocumentStoreQuery`).

The transition then stalled halfway, with measurable cost:

- The portable stores themselves carried **no retirement marker**. `RelationalDocumentStore`,
  `MongoDbDocumentStore`, and the provider factories' `CreateAsync` entry points looked like
  supported first-class surface while the types they traffic in were obsolete.
- The flagship sample and the README taught the portable path exclusively, so new consumers were
  onboarded onto the retiring model.
- New semantics were being implemented twice — once per stack — including four hand-written copies
  of the same unit-of-work protocol, of which the portable relational copy silently swallows
  cleanup failures that the physical copy aggregates and rethrows.
- `IDocumentStore` declares portable query members that the physical stores implement as
  `NotSupportedException` throws, wrapped in `#pragma warning disable GW0004` around Groundwork's
  own central contract.

A repository-wide `NoWarn` additionally muted GW0001–GW0004 everywhere, so none of this produced a
diagnostic anywhere in the repository. That suppression is now scoped to `tests/` only.

## Decision

### 1. The physical, route-driven model is Groundwork's only supported document runtime

The portable document model is retired in place. `RelationalDocumentStore` (and its
SQLite/PostgreSQL/SQL Server subclasses), `MongoDbDocumentStore`, `MongoDbDocumentStoreHandle`, and
every portable factory `CreateAsync` entry point now carry `[Obsolete(..., DiagnosticId = "GW0005")]`.
Consumers open stores with the provider factories' `OpenPhysicalAsync` and execute declared bounded
`DocumentQuery` plans.

### 2. Samples and documentation teach only the physical path

`Groundwork.SupportTickets` and the README are migrated to `OpenPhysicalAsync` + `DocumentQuery`.
No first-party documentation demonstrates the portable path from this ADR forward.

### 3. New behavior lands in the physical stack only

The double-implementation practice ends. A feature that lands in the portable stores from this ADR
forward is a review defect. Bug fixes remain permitted where consumers still run the portable path.

### 4. Removal follows in the announced breaking cleanup

`GW0005` surface, the GW0001–GW0004 declaration/query types, `LegacyPhysicalStorageBridge`, and the
portable store implementations are removed together in the already-announced breaking cleanup, after
the consuming repository (elsa-foundation) has migrated off the portable entry points. The scoped
`tests/` NoWarn shrinks as portable coverage is deleted alongside the code it covers.

### 5. Interface split lands with the cleanup, not before

Splitting `IDocumentStore` into a mutation contract and a legacy portable-query contract is
deferred to the breaking cleanup so the churn lands once: the portable query members disappear from
the interface at the same time their implementations do.

## Consequences

- The stalled transition is now visible at every call site: consuming the portable model produces
  `GW0005` in any consumer, including elsa-foundation on package update — that is the migration
  signal, deliberately.
- Consolidation work (shared unit-of-work core, generic store factories) targets the physical stack
  and treats portable code as frozen; the portable stores' known error-swallowing divergence is
  fixed by adopting the physical semantics in the shared core rather than by patching the frozen
  copy twice.
- The `tests/Directory.Build.props` NoWarn (GW0001–GW0005) is the single remaining sanctioned
  suppression, scoped to suites that intentionally pin legacy behavior until removal.

## Outcome

2026-08-13: the breaking cleanup announced in decision 4 has been executed. The `GW0005` surface
(the portable stores and factory `CreateAsync` entry points), the GW0001–GW0004 declaration/query
types, `LegacyPhysicalStorageBridge`, the portable materializers and planners, and the
`Groundwork.Materialization` package are removed. `IDocumentStore` now carries only the physical
surface (`SaveAsync`/`LoadAsync`/`DeleteAsync` plus session creation); bounded querying lives on
`PhysicalQueryDocumentStore` via declared `DocumentQuery` plans.
