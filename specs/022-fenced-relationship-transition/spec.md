# Feature Specification: Fenced Relationship Transition

**Feature Branch**: `codex/141-sqlite-transition`

**Created**: 2026-07-29

**Status**: Blocked on inaugural-transition ratification

**Input**: Groundwork #141 and its ratified 2026-07-25 relationship-transition decisions.

## User Scenarios & Testing

### User Story 1 - Prepare A Relationship Generation Safely (Priority: P1)

An operator can prepare a candidate relationship generation for an existing populated store without
changing the active generation until the candidate is completely backfilled and validated.

**Why this priority**: A partial sidecar or fence generation must never become authoritative.

**Independent Test**: Start from a populated store with no active relationship generation, prepare
the candidate, and verify every valid reference and target key is represented while the previous
generation remains active.

**Acceptance Scenarios**:

1. **Given** valid existing references, **When** candidate preparation completes, **Then** candidate
   reference and target-key state exactly matches the source data.
2. **Given** preparation stops or fails before validation, **When** the store reopens, **Then** the
   incomplete candidate remains non-authoritative and preparation can converge safely.

### User Story 2 - Reject Legacy Dangling References (Priority: P1)

An operator receives a deterministic candidate-bound diagnostic when existing data references a
missing target, without exposing stored scope, key, or value material.

**Why this priority**: Silent repair, skipping, or admission would make relationship integrity
untrustworthy and could disclose tenant data through diagnostics.

**Independent Test**: Prepare a candidate over a store containing one dangling reference and verify
that transition fails, active state does not change, and only an opaque stable correlation is
reported.

**Acceptance Scenarios**:

1. **Given** a dangling reference, **When** candidate validation runs, **Then** the transition fails
   with `GW-RELATIONSHIP-013` and the candidate never becomes active.
2. **Given** equivalent candidate identity and offending relationship key, **When** validation is
   retried, **Then** the opaque correlation is stable; changing either input changes the
   correlation.

### User Story 3 - Cut Over Atomically And Recover After Restart (Priority: P1)

An operator can atomically activate one fully validated candidate and recover deterministically
after interruption or lost acknowledgement.

**Why this priority**: Readers and later writers must never observe a half-active generation or two
active generations.

**Independent Test**: Interrupt preparation before validation, after validation, and after cutover
commit; reopen with a distinct provider instance and verify one authoritative state and convergent
retry.

**Acceptance Scenarios**:

1. **Given** a validated candidate, **When** cutover succeeds, **Then** exactly that generation is
   active and the previous generation is retained but non-authoritative.
2. **Given** competing or replayed cutover requests, **When** the expected active generation no
   longer matches, **Then** no second activation occurs.
3. **Given** interruption at any declared transition phase, **When** a distinct instance resumes,
   **Then** it either completes the same candidate or leaves the prior generation active.

### User Story 4 - Preserve The Public Fail-Closed Gate (Priority: P1)

A consumer cannot advertise or use relationship guards merely because one provider has an internal
transition proof.

**Why this priority**: Groundwork #141 requires all four providers to certify fencing, recovery, and
native execution before the capability becomes public.

**Independent Test**: Exercise the ordinary public manifest admission after the SQLite transition
proof lands and verify it still rejects relationship declarations with `GW-RELATIONSHIP-012`.

## Edge Cases

- The candidate is already fully prepared when the same request is replayed.
- A different candidate races the expected active generation.
- A source reference is null or absent and therefore does not require a target.
- Duplicate source references map to one target-key fence without losing source identities.
- Unicode and case-policy equivalents produce the same admitted comparison identity.
- Transition state is durable but the caller loses acknowledgement before observing validation or
  activation.
- The candidate is valid but its diagnostic key identifier is unavailable after restart.

## Requirements

- **FR-001**: The provider MUST persist a candidate transition separately from the active
  relationship generation.
- **FR-002**: Candidate preparation MUST backfill every non-null admitted source reference and every
  required target-key fence before validation can succeed.
- **FR-003**: Validation MUST reject every missing target with the existing candidate-bound opaque
  diagnostic and MUST NOT retain raw scope, comparison key, target value, or secret material.
- **FR-004**: Candidate activation MUST be a durable compare-and-swap against the expected active
  generation and MUST produce at most one authoritative generation.
- **FR-005**: Failed, cancelled, or interrupted preparation MUST leave the previous generation
  authoritative.
- **FR-006**: Replaying the same transition request after restart or lost acknowledgement MUST
  converge without duplicating relationship or fence state.
- **FR-007**: A competing candidate or stale expected-generation request MUST fail without changing
  the active generation.
- **FR-008**: The first slice MUST prove the transition with a durable SQLite store and a distinct
  reopened provider instance.
- **FR-009**: The first slice MUST be reachable only through an internal test/development admission
  seam and MUST leave public `GW-RELATIONSHIP-012` rejection intact.
- **FR-010**: Evidence MUST cover successful backfill/cutover, dangling failure, cancellation or
  interruption, restart convergence, stale cutover, and public fail-closed admission.
- **FR-011**: The slice MUST NOT claim ordinary write-fence maintenance, guarded bulk execution,
  production diagnostic-key custody, other providers, or public capability certification.

## Key Entities

- **Active Generation**: The single authoritative relationship materialization generation.
- **Candidate Generation**: A non-authoritative generation being prepared and validated.
- **Transition State**: Durable phase, expected active generation, candidate identity, progress,
  validation outcome, and activation result.
- **Reference Sidecar**: Candidate-owned materialization of admitted source-to-target references.
- **Target-Key Fence**: Candidate-owned serialization identity for one target key.
- **Dangling Diagnostic**: Candidate-bound opaque correlation for a missing target.

## Success Criteria

- **SC-001**: A valid populated store transitions with exact source-reference and target-fence
  cardinalities and one active generation.
- **SC-002**: Every injected dangling-reference case fails with `GW-RELATIONSHIP-013`, exposes no raw
  data, and leaves the prior generation active.
- **SC-003**: Reopen tests at each declared transition phase converge to the same result within a
  bounded test interval.
- **SC-004**: At least two competing activation attempts produce exactly one active candidate.
- **SC-005**: Existing public admission tests continue to reject relationship manifests before
  provider I/O.
- **SC-006**: The focused SQLite transition suite and all affected Core/materialization tests pass
  without enabling a public capability.

## Assumptions

- The ratified two guard shapes and `RelationshipConflict` outcome are unchanged.
- This slice uses an internal deterministic diagnostic-key source for repeatable evidence. Durable
  production custody and rotation are a later provider-operations decision and cannot be inferred
  from this test-only proof.
- Ordinary reference write/delete/unit-of-work fence maintenance and guarded prune execution follow
  in later Model B slices.

## Decision Needed

- **[NEEDS CLARIFICATION: How does the provider-neutral transition contract represent the first
  relationship generation when durable active-generation state is absent? Recommended decision:
  add an explicit expected-absent active state whose compare-and-swap succeeds only while no active
  record exists; same-candidate replay converges and competing candidates cannot both win.]**

The current `RelationshipMaterializationTransitionRequirement` requires non-null active and
candidate generations with the same relationship identity and different generation identities.
Fabricating a synthetic active generation would invent cross-provider semantics and is prohibited.

## Out of Scope

- A generic join language.
- Public preview switches or one-provider capability advertisement.
- Query-then-delete orchestration, client filtering, or adapter-owned shadow fences.
- SQL Server, PostgreSQL, or MongoDB execution.
- Ordinary write-fence maintenance and native guarded bulk mutation execution.
