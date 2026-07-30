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

2026-07-31 candidate evidence. The prior 26 / 51 / 1 rerun tested source head
`7cf41e3db4db87c28cefd096961dc25869ebfff7`; the exact fifth-remediation worktree rerun below
extends that source with the complete schema-semantic review fixes recorded in this section.

```bash
dotnet test tests/Groundwork/Groundwork.Sqlite.Tests/Groundwork.Sqlite.Tests.csproj \
  --filter RelationshipTransition --no-restore
# Passed: 29, Failed: 0

dotnet test tests/Groundwork/Groundwork.Tests/Groundwork.Tests.csproj \
  --filter Relationship --no-restore
# Passed: 51, Failed: 0

dotnet test tests/Groundwork/Groundwork.Materialization.Tests/Groundwork.Materialization.Tests.csproj \
  --filter Relationship --no-restore
# Passed: 1, Failed: 0
```

Twenty-eight SQLite transition cases use file-backed temporary databases and distinct executor
instances; the twenty-ninth is the in-memory closed-connection public-admission guard. Together they
prove valid exact sidecar/fence backfill, opaque dangling diagnostics, durable bounded progress,
validation/cutover acknowledgement loss, concurrent candidate competition, and the closed public
admission gate. Pending candidates bind a keyed, collision-safe frame of every normalized source
identity/scope/reference and target identity before any restart can skip progress, replay validation,
or activate. Active reopen additionally requires the exact Active state, completed progress, input
digest, and candidate-scoped reference/fence tuples to agree with the supplied snapshot. A terminal
failure restores only its persisted `GW-RELATIONSHIP-013` code and opaque correlation after a
domain-separated authenticated envelope binds the candidate, input digest, code, and correlation;
it does not inspect current source or target data. Infrastructure initialization serializes schema
creation and validation, upgrades only the exact pre-`failure_mac` state-v1 layout, and rejects
unexpected visible/generated/hidden columns, primary-key shapes, text-column collations, index sort
directions, or target-index definitions before reading transition evidence. Active materialization
checks require both exact tuple cardinality and set equality; the suite independently proves
duplicate-capable, case-insensitive primary-key, case-insensitive non-key identity, non-ordinal
target-index, and descending-index schema substitutions cannot reopen an active candidate. The
test-only inspection exposes exact candidate and source/target identity tuples but omits serialized
references, failure material, and key data. This slice does not claim ordinary fence maintenance,
guarded prunes, a non-SQLite provider, production HMAC custody, or public relationship capability.

## Independent Exact-Range Review

Three read-only reviewers adversarially inspected
`eacb3209e3710834cd78c49e65d4bc763b746769..6713916147ee8fa61ba22e88f39a23e15fd4cccf`
on distinct axes. Each was instructed to assume the implementation had green-washed its evidence.
The implementation candidate passed all three axes after the following confirmed findings were
remediated and re-verified by their originating reviewers:

- Correctness/mechanism: the first candidate could resume from a count without authenticating the
  complete input and could not restore a persisted dangling diagnostic. Later candidates trusted
  incomplete active materialization evidence and accepted SQLite schema substitutions hidden by
  basic pragma metadata. The final mechanism authenticates complete inputs and failure envelopes,
  verifies exact candidate tuples and cardinality, migrates only the exact legacy state shape, and
  validates visible/hidden columns, table options, every text collation, every primary key, the
  target index, foreign keys, triggers, and the complete index inventory in one immediate
  transaction. Originating-reviewer verdict: **PASS**.
- Evidence integrity: prior evidence did not authenticate stored failure correlation, did not prove
  exact backfill tuples, lacked a pre-MAC schema-upgrade case, and overstated all 29 SQLite cases as
  file-backed. The final record binds the exact 29 / 51 / 1 rerun, distinguishes 28 file-backed
  transition cases from the in-memory closed-connection admission guard, and retains the exact
  source head and nonclaims. Originating-reviewer verdict: **PASS**.
- Scope and test preservation: earlier backfill tests asserted only row counts and could not see
  generated/hidden schema drift. The final range is confined to the ratified internal SQLite proof
  and Spec 022, preserves the public pre-I/O `GW-RELATIONSHIP-012` gate, removes or weakens no test,
  and leaves public capability, ordinary fence maintenance, guarded prune, non-SQLite certification,
  and production key custody explicitly open.
  Originating-reviewer verdict: **PASS**.
