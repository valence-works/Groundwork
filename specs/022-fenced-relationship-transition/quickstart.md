# Quickstart: Fenced Relationship Transition

## Scope

This work unit proves internal SQLite candidate backfill, validation, atomic cutover, and restart
recovery for Groundwork #141. Public relationship admission remains closed until all four providers
complete the full fencing and guarded-mutation gate.

Implementation is paused until the program owner ratifies how an inaugural transition represents
expected-absent active-generation state. A synthetic prior generation is not admissible.

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

Implementation and independent review evidence will be added here as tasks land.
