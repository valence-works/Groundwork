# Declared index key lengths

Status: accepted (2026-08-13).

Date: 2026-08-13.

Related: [ADR 0003](0003-adopt-three-physical-storage-forms.md) (the physical storage forms and
default resolution this ADR extends); the declared numeric precision/scale ADR (the numeric twin of
this declaration surface).

## Context

Default physical-storage resolution synthesizes projected columns from scale-bearing path demand:
each demanded stable path becomes a `ProjectedColumnDefinition` whose portable type is derived from
the logical index's `IndexValueKind`. `IndexValueKind.String` and `IndexValueKind.Keyword` map to
`PortablePhysicalType.String` with no declared `Length` — an unbounded string column.

Unbounded string projections are valid portable metadata: PostgreSQL, SQLite, and MongoDB index
them without a declared bound. SQL Server does not — its index keys are sized, and
`SqlServerPhysicalIndexValidator.ProjectedKeyBytes` rejects any physical index whose String or
Binary key column has no `Length`. A declared-mode storage unit resolved through the default
policy therefore compiled everywhere except SQL Server, where route materialization failed with
"requires bounded String key column". The only way to state a bound was to abandon default
resolution and hand-write a full `PhysicalStoragePolicy.Explicit` definition — an entire physical
table declaration — just to state one integer.

## Decision

### 1. String index fields declare their maximum key length

`IndexField` gains an optional `Length`: a maximum count of UTF-16 code units, matching the
existing `ProjectedColumnDefinition.Length` semantics. `LogicalIndexDeclaration` gains an optional
declaration-level `Length` default with per-field override, mirroring how a field's `ValueKind`
overrides the declaration default; `GetLength` resolves the effective value.

### 2. The declared length travels through demand into synthesis

`ScaleBearingPathDemand` carries the effective `Length` of `String` and `Keyword` fields, and
`PhysicalStorageResolver.SynthesizeProjectedColumns` places it on the synthesized string
`ProjectedColumnDefinition`. Declared-mode default resolution now serves string scale-bearing
demand on providers with sized index keys without an explicit physical definition. The demand
fingerprint serializes the length only when declared, so existing fingerprints are unchanged.

### 3. Declared lengths are validated as GW-PHYSICAL-039

- A declared length is at least 1.
- Field-level length on a non-`String`/`Keyword` field is rejected, as is a declaration-level
  default on an index with no `String`/`Keyword` field.
- All scale-bearing demand for one stable path within a storage unit must agree on a single
  declared length; indexes that leave the length undeclared inherit the declared one.

### 4. Undeclared string demand stays unbounded

Unlike decimal precision, an absent length is not an error: unbounded string projections remain
valid portable metadata, and providers without sized index keys keep serving them. Providers that
require a bound keep rejecting unbounded key columns at route compilation, exactly as before —
this ADR adds a way to state the bound, not a synthesized default.

## Consequences

- Declared-mode manifests state one integer on the index instead of hand-writing an explicit
  physical table definition for every string scale-bearing path that must run on SQL Server.
- Demand fingerprints are unchanged for every existing manifest; a manifest that adopts a declared
  length changes its fingerprint, which is correct — the physical schema now depends on it.
- The declaration surface covers index fields only. A string path demanded solely by a residual
  predicate field still has no way to declare its length (residual projections are not index key
  columns, so no provider requires one); extending `BoundedQueryResidualPredicateField` the same
  way is a follow-up if that demand materializes.
- `ScaleBearingPathDemand` gains one positional parameter after `ValueKind` — a source-breaking
  change for external constructors of that record (none known; elsa-foundation does not construct
  it). `IndexField` and `LogicalIndexDeclaration` gain only trailing optional parameters.
- The support-ticket sample declares `length: 128` on its keyword indexes, which is what lets its
  SQL Server provider test materialize physical routes.
