# Declare index key lengths on logical indexes

Status: proposed (2026-08-13).

Date: 2026-08-13.

## Context

Declared-mode storage units (`StorageUnitProvisioningMode.Declared` with the default policy) synthesize
their physical form from logical indexes and scale-bearing bounded queries. Synthesis mapped
`IndexValueKind.String` and `IndexValueKind.Keyword` demand to unbounded `String` projected columns
because neither `LogicalIndexDeclaration` nor `IndexField` carried a length.

SQL Server certifies every physical index against a finite key budget (1700 bytes, 32 columns; the
storage-scope prefix and identity tie-break columns count toward it) and refuses unbounded string key
columns before DDL. A declared-mode keyword index therefore could never certify on SQL Server, while
the same manifest deployed fine on SQLite, PostgreSQL, and MongoDB. The SupportTickets sample hit this
the first time its SQL Server provider-contract tests ran, and the only escape was abandoning the
declared teaching story for `PhysicalStoragePolicy.Explicit` with hand-bounded columns.

Two shapes could close the gap:

1. a documented default keyword length applied during synthesis; or
2. an optional, declared length on the logical index surface.

A synthesized default silently changes the synthesized definition — and therefore the schema
fingerprint — of every existing declared-mode deployment on SQLite, PostgreSQL, and MongoDB, which
demands an evolution and migration story before it can ship. It also invents a data contract (a
maximum value width the author never stated) that projection value validation would then enforce.

## Decision

Declare the bound where the rest of the logical index contract lives:

- `IndexField` gains an optional `Length` — a maximum count of UTF-16 code units, the same unit
  `ProjectedColumnDefinition.Length` already uses.
- `LogicalIndexDeclaration` gains an optional declaration-level `Length` default, mirrored on the
  existing declaration-default `ValueKind` pattern; `GetLength(field)` resolves field-over-declaration.
  The declaration default only reaches String and Keyword fields, so a heterogeneous compound index
  does not leak a string bound onto DateTime or Number fields.
- Synthesis carries the resolved length through scale-bearing demand into the synthesized projected
  column, so declared-mode keyword indexes certify on SQL Server exactly like explicit bounded columns.
- Validation (GW-PHYSICAL-038) rejects non-positive lengths, explicit lengths on fields whose kind
  cannot be bounded, and inconsistent lengths for one path within a storage unit — including one
  index declaring a length while another leaves the same path unbounded. An omitted length is an
  unbounded contract, not a missing opinion; letting a declared length win would silently narrow the
  shared projected column and reject writes the unbounded declaration permits (GW-PHYSICAL-037).
- Residual predicate fields are typed declaration sites exactly like index fields — they already
  declare value kinds that must agree unit-wide (GW-PHYSICAL-036) — so they gain the same optional
  `Length` and participate in the same one-length-per-path rule. A residual sharing a path with a
  bounded index must declare the matching length; leaving it unbounded is the same silent-narrowing
  conflict as between two indexes and is rejected, not inherited.

Unbounded string demand stays legal: providers without a key budget accept it unchanged, and SQL
Server keeps rejecting it at certification time with its existing bounded-key diagnostic. This mirrors
how declared-mode Number demand is handled (GW-PHYSICAL-018 refuses to invent decimal precision).

## Consequences

- Existing declared-mode manifests synthesize byte-identical definitions; schema fingerprints do not
  change unless an author opts into a length.
- Declaring a length is a schema change like any other: the synthesized column becomes bounded, the
  fingerprint changes, and projection value validation (GW-PHYSICAL-037) starts rejecting over-length
  values on every provider. Authors adopt it through the normal evolution path.
- The SupportTickets sample returns to the declared teaching story with one `KeywordLength` constant
  instead of an explicit physical definition.
- Declared-mode Number demand still requires the explicit policy because precision and scale remain
  undeclarable on the logical surface; extending `IndexField` the same way is open follow-up work.
