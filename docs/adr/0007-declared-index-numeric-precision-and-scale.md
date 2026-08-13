# Declared index numeric precision and scale

Status: accepted (2026-08-13).

Date: 2026-08-13.

Related: [ADR 0003](0003-adopt-three-physical-storage-forms.md) (the physical storage forms and
default resolution this ADR extends).

## Context

Default physical-storage resolution synthesizes projected columns from scale-bearing path demand:
each demanded stable path becomes a `ProjectedColumnDefinition` whose portable type is derived from
the logical index's `IndexValueKind`. `IndexValueKind.Number` maps to `PortablePhysicalType.Decimal`,
and portable decimal columns require an explicit total precision and fractional scale — SQL Server's
index-key sizing (`SqlServerPhysicalIndexValidator.DecimalBytes`) admits only precision 1–28, and
relational DDL cannot be emitted without a concrete `decimal(p, s)` shape.

The resolver deliberately refuses to invent that shape. A declared-mode storage unit whose
scale-bearing demand touches a `Number` path therefore failed validation with `GW-PHYSICAL-018`
("invalid portable metadata" on the synthesized precision-less decimal column), forcing manifests to
abandon default resolution and hand-write a full `PhysicalStoragePolicy.Explicit` definition — an
entire physical table declaration — just to state two integers.

## Decision

### 1. Numeric index fields declare their portable decimal shape

`IndexField` gains optional `Precision` and `Scale`. `LogicalIndexDeclaration` gains optional
declaration-level `Precision`/`Scale` defaults with per-field override, mirroring how a field's
`ValueKind` overrides the declaration default; `GetPrecision`/`GetScale` resolve the effective
values. The shape is one pair: a field declaring either component overrides the declaration
defaults as a whole, so a partial field pair fails validation rather than silently inheriting the
missing component.

### 2. The declared shape travels through demand into synthesis

`ScaleBearingPathDemand` carries the effective `Precision`/`Scale` of `Number` fields, and
`PhysicalStorageResolver.SynthesizeProjectedColumns` places them on the synthesized decimal
`ProjectedColumnDefinition`. Declared-mode default resolution now serves numeric scale-bearing
demand without an explicit physical definition. The demand fingerprint serializes the shape only
when declared, so existing fingerprints are unchanged.

### 3. Declared shapes are validated as GW-PHYSICAL-038

- Precision and scale are declared together, with precision 1–28 (the provider-portable decimal
  envelope set by SQL Server index-key sizing) and scale 0–precision.
- Field-level precision or scale on a non-`Number` field is rejected, as are declaration-level
  defaults on an index with no `Number` field.
- All scale-bearing demand for one stable path within a storage unit must agree on a single
  declared shape; indexes that leave the shape undeclared inherit the declared one.

### 4. Undeclared numeric demand remains rejected

The resolver still cannot invent decimal precision: numeric scale-bearing demand with no declared
shape keeps failing with `GW-PHYSICAL-018`. This ADR adds a way to state intent, not a default.

## Consequences

- Declared-mode manifests state two integers on the index instead of hand-writing an explicit
  physical table definition for every numeric scale-bearing path.
- Demand fingerprints are unchanged for every existing manifest; a manifest that adopts a declared
  shape changes its fingerprint, which is correct — the physical schema now depends on it.
- The declaration surface covers index fields only. A `Number` path demanded solely by a residual
  predicate field still has no way to declare its shape and keeps failing `GW-PHYSICAL-018` unless
  the same path is also an index field; extending `BoundedQueryResidualPredicateField` the same way
  is a follow-up if that demand materializes.
- `ScaleBearingPathDemand` gains two positional parameters after `ValueKind` — a source-breaking
  change for external constructors of that record (none known; elsa-foundation does not construct
  it). `IndexField` and `LogicalIndexDeclaration` gain only trailing optional parameters.
