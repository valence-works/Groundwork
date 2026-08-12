# Feature Specification: Schema Subject Seam

**Feature Branch**: `claude/elsa-data-access-strategy-5xa0d8`

**Created**: 2026-08-11

**Status**: Specified, not implemented — no .NET SDK available in the authoring session
(`builds.dotnet.microsoft.com` is denied by egress policy), so no change here has been compiled or
tested. The analysis is source-verified; the implementation is not.

**Input**: [ADR 0005](../../docs/adr/0005-separate-kernel-facilities-from-contract-families.md)
decision §2 seam B and §5 step 1.

## Why

ADR 0005 is accepted: Groundwork is divided into kernel facilities and contract families, and a
contract family must be authorable outside core. Seam B is step 1 — lift
`ProviderPhysicalSchemaDefinition` from an annotation on an `ExecutableStorageRoute` to a first-class
schema subject, so a non-document contract family can consume `Core.SchemaEvolution` without
synthesizing a document route.

The waiting consumer is `DiagnosticRecordPhysicalSchemaState` (175 lines), which reimplements
canonical serialization plus fingerprinting because the Core facility is unreachable.

## Source-verified findings

Three findings determine the cost, and two of them are favourable.

### 1. The operation model is already generalized

`PhysicalSchemaOperation`'s constructors take **`StorageUnitIdentity?`** — nullable — and canonicalize
through `storageUnit?.Value`:

```csharp
CanonicalPayload = PhysicalSchemaFingerprint.Canonicalize(
    [kind.ToString(), storageUnit?.Value, subjectIdentity, SlotIdentity, .. semanticParts]);
```

A subject with no storage unit is therefore already representable at the operation layer. No change
is needed there.

### 2. The coupling is three places, not a pipeline

`StorageUnitIdentity` appears only 20 times across the four `Core/SchemaEvolution` files
(`PhysicalSchemaOperation.cs` 9, `PhysicalSchemaState.cs` 7, `ProviderPhysicalSchemaDefinition.cs` 3,
`PhysicalSchemaAppliedStateSerializer.cs` 1). The blocking constraints are:

| # | Location | Constraint |
|---|---|---|
| 1 | `ProviderPhysicalSchemaDefinition` ctor | Requires non-null `StorageUnitIdentity`; threads it into `Fingerprint`, `Identity`, and `Canonicalize` ordering |
| 2 | `PhysicalSchemaTarget` ctor | Rejects any definition whose `StorageUnit` matches no route |
| 3 | `PhysicalSchemaAppliedSnapshot.ValidateProviderDefinitions` | Same rejection on the durable side |

Constraints 2 and 3 are the same invariant enforced on desired and applied state. Both must move
together; relaxing one alone would let desired and applied state disagree.

### 3. The change is fingerprint-compatible for existing subjects

This is the property that makes seam B safe, and it must be preserved by the implementation.

Fingerprints are persisted and compared on restart to detect physical schema drift, so a change that
shifts fingerprints for existing document definitions would present as spurious drift across every
deployed store. Because `PhysicalSchemaOperation` already canonicalizes `storageUnit?.Value`, a
definition that keeps a non-null storage unit produces byte-identical canonical payloads before and
after. A null appears only for subjects that could not previously exist.

`ProviderPhysicalSchemaDefinition.Fingerprint` must preserve the same property:

```csharp
Fingerprint = PhysicalSchemaFingerprint.Create(
    [ProviderName, StorageUnit.Value, Kind, SubjectIdentity, CanonicalDefinition]);
```

Making `StorageUnit` nullable must not change the encoding when it is present. Substituting an empty
string for a null is **not** acceptable — it would collide with a legitimately empty component.

## Requirements

### FR-1 — Admit a schema subject with no storage unit

`ProviderPhysicalSchemaDefinition.StorageUnit` becomes `StorageUnitIdentity?`. `Canonicalize`'s
ordering and `ProviderPhysicalSchemaDefinitionIdentity` handle null deterministically, sorting nulls
before non-nulls under ordinal comparison.

### FR-2 — Admit a target whose subjects are definitions rather than routes

`PhysicalSchemaTarget` accepts provider definitions that belong to no route, provided they carry no
storage unit. A definition **with** a storage unit must still match a route — that invariant protects
the document family and is not being relaxed, only scoped.

`ManifestIdentity`, `ManifestVersion`, and a non-null route list are still required by the
constructor. A contract family with no manifest needs a target shape that does not demand them;
whether that is a nullable manifest identity or a separate subject-only target is the one open design
question here, and should be settled by whichever shape keeps `PhysicalSchemaTarget.Fingerprint`
stable for existing targets.

### FR-3 — Apply the same scoping to durable state

`PhysicalSchemaAppliedSnapshot.ValidateProviderDefinitions` mirrors FR-2 exactly. Its reconstruction
of expected `AppliedSemanticOperationSnapshot` values from
`ApplyProviderPhysicalSchemaDefinitionOperation` must continue to agree.

### FR-4 — Preserve fingerprints for every existing subject

No canonical payload, definition fingerprint, target fingerprint, applied-snapshot fingerprint, slot
identity, or operation identity may change for any definition that carries a storage unit.

### FR-5 — Keep `DocumentIdentitySchemaState` with the document family

`AppliedStorageRouteSnapshot.IdentitySchemaState` is document vocabulary. It stays on the route
snapshot, which remains document-only; it must not migrate onto the generalized subject.

## Verification

No part of this has been executed. All of the following is required before merge:

1. `dotnet test tests/Groundwork/Groundwork.Tests` — includes `PhysicalSchemaDiffPlannerTests`, the
   direct test of the changed types.
2. `dotnet test tests/Groundwork/Groundwork.MongoDb.Tests` — MongoDB is the only current consumer of
   `ProviderPhysicalSchemaDefinition` (`MongoDbPhysicalMutationBinding`,
   `MongoDbPhysicalMutationSelectorSchemaDefinition`, `MongoDbPhysicalMutationSchemaDefinitionHandler`,
   `MongoDbPhysicalSchemaExecutor`) and its container conformance is the real regression gate.
3. `dotnet test tests/Groundwork/Groundwork.RelationalProviders.Tests` and
   `Groundwork.Sqlite.Tests` — schema application paths.
4. **A fingerprint-stability test proving FR-4 directly**: construct a definition with a storage unit
   before and after the change and assert the fingerprint, canonical payload, slot identity, and
   operation identity are byte-identical. This does not exist today and is the single most important
   test to add, because FR-4's failure mode is silent drift rather than a red test.

## Out of scope

- Seam A (`PhysicalTableDefinition` split) — ADR 0005 §5 sequences it after seam B.
- The MongoDB provider substrate gap — ADR 0005 §3, also after seam B.
- Replacing `DiagnosticRecordPhysicalSchemaState`. That is the *proof* of this seam and the next unit
  of work, but it is a separate change: this one must land green on its own first, or a failure will
  not be attributable to either.
