# Feature Specification: Groundwork Physicalization And Performance

**Feature Branch**: `codex/groundwork-physicalization-performance`

**Created**: 2026-06-10

**Status**: In Progress

**Input**: User description: "Groundwork G7 physicalization and performance. Add opt-in optimized physicalization for hot storage units while preserving the portable document-store contract and portable default. Providers should materialize optimized physical structures from manifest intent, route eligible equality queries through those structures, and prove at least one relational provider plus MongoDB can use the optimized path without changing caller APIs."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Declare An Optimized Storage Unit (Priority: P1)

A persistence designer can mark a storage unit as optimized and expect providers to derive physical projections from the manifest instead of adding provider-specific code to the application.

**Why this priority**: Physicalization must remain declarative and provider-neutral or Groundwork stops being a generic framework.

**Independent Test**: Create a manifest with an optimized unit and declared indexes, run the planner, and verify the resulting plan identifies optimized physicalization work while portable units remain unchanged.

**Acceptance Scenarios**:

1. **Given** a portable storage unit, **When** a plan is generated, **Then** no optimized projection work is required.
2. **Given** an optimized storage unit with single-field indexes, **When** a plan is generated, **Then** those indexes are identified as physicalized query fields.

---

### User Story 2 - Use Optimized Equality Queries In A Relational Provider (Priority: P1)

A storage unit can be materialized by a relational provider so saves maintain optimized projections and equality queries can use the optimized structure without changing `IDocumentStore`.

**Why this priority**: G7 must prove physicalization is more than metadata and that at least one relational provider can execute the optimized path.

**Independent Test**: Materialize an optimized manifest with SQLite, save documents, query by a declared index, and verify the provider created and maintained the optimized projection table.

**Acceptance Scenarios**:

1. **Given** an optimized storage unit, **When** SQLite materializes the manifest, **Then** a provider-owned projection table exists for the unit.
2. **Given** a saved document, **When** an indexed value changes, **Then** the optimized projection row changes with the document.
3. **Given** an equality query on a declared optimized index, **When** the query runs, **Then** results match the portable document-store contract.

---

### User Story 3 - Use Optimized Equality Queries In MongoDB (Priority: P1)

A MongoDB-backed storage unit can store and index optimized projection values while preserving the same document-store save/load/query behavior.

**Why this priority**: The roadmap requires at least one document provider to prove optimized physicalization alongside relational validation.

**Independent Test**: Materialize an optimized manifest with MongoDB, save documents, inspect projected values and indexes, and verify equality queries return the expected documents.

**Acceptance Scenarios**:

1. **Given** an optimized storage unit, **When** MongoDB materializes the manifest, **Then** provider-native indexes target optimized projection fields.
2. **Given** a saved document, **When** a query uses an optimized declared index, **Then** MongoDB returns the same result as the portable contract.

---

### User Story 4 - Prove Bounded Process-Failure Recovery (Priority: P1)

An operator can retain exact-source evidence that a production-path SQLite physical store recovers
correctly when the benchmark worker process is terminated at a declared transaction boundary.

**Why this priority**: Issue #50 cannot promote a baseline from client/factory reopen checks. It
requires an actual process-failure proof before Groundwork evidence can support physical-form
decisions.

**Independent Test**: Start a distinct SQLite recovery worker process against a durable database
file, terminate it at each declared failure barrier, reopen through the production target path, and
verify the exact recovered state through a new process.

**Acceptance Scenarios**:

1. **Given** a worker that has staged a mutation but has not committed, **When** the parent
   terminates that process, **Then** a bounded recovery process observes no partial mutation and
   replaying the original expected-version request succeeds exactly once from the previously
   committed state.
2. **Given** a worker that has committed but has not acknowledged completion, **When** the parent
   terminates that process, **Then** a bounded recovery process observes the committed mutation
   exactly once, and a retry carrying the original expected version is rejected without a duplicate
   effect.
3. **Given** retained recovery evidence, **When** its source, failure point, process outcome, or
   recovered-state digest is altered without the caller's out-of-band exact-file digest changing,
   **Then** evidence verification rejects it.

### User Story 5 - Certify The Complete Stable Query Order (Priority: P1)

A persistence designer can trust a scale-bearing physical index certification to cover the complete
provider-applied order, including Groundwork's mandatory identity tie-break, rather than only the
caller-declared prefix.

**Why this priority**: Issue #50 cannot use native plans or timings from a query that Groundwork
certified as index-backed while the provider still performs a blocking sort.

**Independent Test**: Compile ordered and unordered offset-paged benchmark queries, prove that
indexes without the comparison-key tail fail admission, and run the real MongoDB benchmark plan
gate across all three physical forms.

**Acceptance Scenarios**:

1. **Given** a scale-bearing offset query whose non-unique logical index omits document identity,
   **When** Groundwork resolves or validates its physical index, **Then** the required comparison-key
   identity tail is included in the exact index shape.
2. **Given** an ordered offset query whose physical index covers only the declared predicate and
   sort fields, **When** query-plan compilation runs, **Then** certification fails before provider
   traffic because the runtime identity tie-break is not index-backed.
3. **Given** the benchmark's ordered MongoDB query on any supported physical form, **When** strict
   native-plan capture runs, **Then** the winning plan uses the declared compound index and contains
   neither a collection scan nor a blocking sort.
4. **Given** a unique logical key, **When** Groundwork certifies its stable order, **Then** the unique
   business key remains the total order and no identity tail weakens its uniqueness constraint.

### Edge Cases

- Optimized storage units with no eligible single-field indexes fall back to portable behavior.
- Portable storage units must not create optimized provider structures.
- Unique declared indexes must remain unique after physicalization.
- Missing indexed values must remain excluded from optimized projections.
- Stale expected versions must not update optimized projections.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Groundwork MUST keep `PhysicalizationPolicy.Portable` as the default storage-unit behavior.
- **FR-002**: Groundwork MUST allow a storage unit to opt into optimized physicalization without changing `IDocumentStore`.
- **FR-003**: Planning MUST distinguish optimized physicalization work from portable storage-unit work.
- **FR-004**: Relational materialization MUST create provider-owned optimized projection structures for eligible optimized units.
- **FR-005**: Relational save/update/delete operations MUST keep optimized projections consistent with document content and optimistic concurrency outcomes.
- **FR-006**: Relational equality queries on eligible optimized indexes MUST use the optimized projection path.
- **FR-007**: MongoDB materialization MUST create provider-native indexes for optimized projection fields.
- **FR-008**: MongoDB save/update/delete operations MUST keep optimized projection values consistent with document content and optimistic concurrency outcomes.
- **FR-009**: MongoDB equality queries on eligible optimized indexes MUST use optimized projection fields.
- **FR-010**: Tests MUST prove the optimized path for SQLite and MongoDB using real provider-backed stores.
- **FR-011**: Generic Groundwork packages MUST remain free of host-specific dependencies.
- **FR-012**: The benchmark harness MUST execute bounded failure/recovery evidence in distinct
  operating-system processes; disposing or recreating only a client/factory is insufficient.
- **FR-013**: The first recovery slice MUST use the production SQLite physical-target admission and
  durable storage path for both mutation and recovery.
- **FR-014**: Recovery evidence MUST bind the exact source snapshot, provider, physical form,
  declared failure point, process termination outcome, requester-acknowledgement absence,
  before/after retry state digests, and bounded recovery-execution result without retaining
  connection values or credentials. Exact-file SHA-256 retention MUST remain outside the evidence.
- **FR-015**: This SQLite slice MUST remain non-promotable and MUST NOT claim four-provider recovery,
  immutable-baseline approval, form selection, or an Elsa performance verdict.
- **FR-016**: Scale-bearing physical index resolution and admission MUST certify the complete
  provider-applied stable order, including the comparison-key identity tie-break for offset paging
  and the lookup-key identity tie-break for cursor paging when the declared logical key is not
  already unique.
- **FR-017**: The benchmark's indexed and ordered physical forms MUST include the exact identity
  tail required by the runtime order on all three physical storage forms.
- **FR-018**: The strict MongoDB native-plan gate MUST reject a winning collection scan or blocking
  sort even when the expected index also appears in the winning-plan subtree.

### Key Entities

- **Physicalization Policy**: Manifest-level policy declaring whether a storage unit is portable, optimized, or specialized.
- **Physicalized Projection Field**: Provider-derived field that maps an eligible declared index to an optimized provider structure.
- **Optimized Projection Structure**: Provider-owned table, column, index, or document field used to speed eligible queries.
- **Physicalization Plan**: Planning output that tells operators which optimized structures a provider should materialize.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Planner tests show optimized units produce physicalization operations while portable units do not.
- **SC-002**: SQLite-backed tests verify optimized projection structures are created, maintained, and queried successfully.
- **SC-003**: MongoDB-backed tests verify optimized projection fields and indexes are created, maintained, and queried successfully.
- **SC-004**: Full solution tests pass with optimized physicalization included.
- **SC-005**: Real SQLite process tests prove pre-commit rollback plus eligible replay and committed-
  before-acknowledgement recovery plus stale-retry rejection within declared recovery-execution
  bounds.
- **SC-006**: Contract tests reject tampered, incomplete, timed-out, or same-process recovery
  evidence.
- **SC-007**: Core tests reject an offset-query index without the runtime comparison-key tail and
  accept the exact corrected shape, while preserving a unique logical key without an identity tail.
- **SC-008**: A real MongoDB-backed benchmark test proves the ordered strict plan gate across shared
  documents, dedicated document table, and physical entity table forms.

## Assumptions

- The original G7 slice optimized declared single-field equality indexes first. Issue #50's
  benchmark amendment now also owns the bounded compound-index shapes exercised by its harness.
- SQLite counts as the relational provider proof for G7 because the relational document store is shared by SQLite, SQL Server, and PostgreSQL.
- Optimized physicalization remains opt-in; runtime-defined entities continue to default to portable document storage.
- G7 proves correctness of optimized paths and exposes enough plan evidence for future benchmarking; deeper benchmark harnesses remain part of G8 hardening.
- The issue #50 evidence-completion amendment starts with SQLite process-failure recovery; controlled
  four-provider execution and baseline promotion remain later reviewed units.
