# Quickstart: Fenced Relationship Transition

## Scope

This work unit proves internal SQLite candidate backfill, validation, atomic cutover, and restart
recovery for Groundwork #141. Public relationship admission remains closed until all four providers
complete the full fencing and guarded-mutation gate.

The ratified inaugural form is an explicit expected-absent state, not a synthetic prior generation.
The internal SQLite executor uses an absent-only INSERT compare-and-swap; rotations remain bound to
an exact active generation and materialization fingerprint.

## Focused Verification

```bash
dotnet test tests/Groundwork/Groundwork.Sqlite.Tests/Groundwork.Sqlite.Tests.csproj \
  --filter RelationshipTransition
```

Expected result: valid candidates backfill and cut over once; dangling candidates fail privately;
stale candidates, cancellation, and restart converge without changing the wrong active generation.

## Supporting Gates

```bash
dotnet test tests/Groundwork/Groundwork.Tests/Groundwork.Tests.csproj \
  --filter Relationship

dotnet test tests/Groundwork/Groundwork.Materialization.Tests/Groundwork.Materialization.Tests.csproj \
  --filter Relationship
```

## Nonclaims

This slice does not enable public relationship capability, maintain fences during ordinary writes,
execute guarded prunes, certify a non-SQLite provider, define production HMAC-key custody, or
complete Groundwork #141 / Elsa #643.

## Evidence

2026-07-30 candidate evidence:

```bash
dotnet test tests/Groundwork/Groundwork.Sqlite.Tests/Groundwork.Sqlite.Tests.csproj \
  --filter RelationshipTransition --no-restore
# Passed: 12, Failed: 0

dotnet test tests/Groundwork/Groundwork.Tests/Groundwork.Tests.csproj \
  --filter Relationship --no-restore
# Passed: 51, Failed: 0

dotnet test tests/Groundwork/Groundwork.Materialization.Tests/Groundwork.Materialization.Tests.csproj \
  --filter Relationship --no-restore
# Passed: 1, Failed: 0
```

The SQLite suite uses file-backed temporary databases and distinct executor instances. It proves
valid exact sidecar/fence backfill, opaque dangling diagnostics, durable bounded progress,
validation/cutover acknowledgement loss, concurrent candidate competition, and the closed public
admission gate. Pending candidates bind a keyed, collision-safe frame of every normalized source
identity/scope/reference and target identity before any restart can skip progress, replay validation,
or activate; a terminal failure instead restores only its persisted `GW-RELATIONSHIP-013` code and
opaque correlation without inspecting current source or target data. It does not claim ordinary fence
maintenance, guarded prunes, a non-SQLite provider, production HMAC custody, or public relationship
capability.
