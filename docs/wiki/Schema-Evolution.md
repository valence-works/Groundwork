# Schema evolution

Groundwork derives schema changes from the difference between your **desired state** (the
manifest, compiled to executable storage routes) and **durable applied state** (what was actually
acknowledged against the database, recorded in schema history). You never write migrations for
additive work — you change the manifest, and the planner emits the exact pending operations.

## How the diff works

`Groundwork.Core.SchemaEvolution` turns a target — manifest/provider identity plus compiled
routes — into a deterministic *additive* diff against durable applied state. The planner emits
stable, content-addressed operations for storage creation, projected-column addition,
physical-index creation, canonical-JSON backfill, target validation, and applied-state recording.

Key properties you can rely on:

- **Semantic, not version-driven.** Operation identities and fingerprints depend only on semantic
  payload. A new column or index is pending even when the manifest version did not change; an
  identical target produces no operations.
- **Additive only.** Mutating or removing an already-applied semantic slot is rejected as a
  non-additive conflict (`GW-SCHEMA-003`). One widening is admitted: an index whose
  `MissingValueBehavior` widens from `Excluded` to `IncludedAsNull` (the narrowing direction stays
  a conflict because it removes rows). Destructive and semantic-migration work requires explicit
  operator authorization through the CLI.
- **Backfill is part of the plan.** Adding an index or projected column to a unit that already
  holds documents schedules a canonical-JSON backfill so pre-existing documents become visible;
  required projected columns are staged nullable, backfilled, then enforced before their indexes
  are created. Adding a *unique* index over pre-existing duplicates fails loudly and rolls back —
  the data must be reconciled first.
- **Serialized and idempotent.** Application always holds a provider/manifest exclusion lease
  across history read, planning, authorization, execution, validation, and state recording.
  Executors apply `(operation identity, fingerprint)` idempotently, and applied state is recorded
  compare-and-swap only after every operation acknowledged.
- **A projected field declared `SemanticMigrationRequired`** blocks with `GW-SCHEMA-005` until an
  authored provider-neutral semantic migration exists — the additive pipeline never substitutes
  canonical-JSON extraction for an explicitly required transform.

Full details:
[physical schema diffs and durable applied state](https://github.com/valence-works/Groundwork/blob/main/docs/physical-schema-diffs.md).

## Two ways to apply schema

1. **Startup auto-apply (development posture).** Pass
   `GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true }` to the store factory.
   Applies safe additive work only; admission is inspect-only by default. See [[Opening-Stores]].
2. **The `dotnet groundwork` CLI (deployment posture).** Explicit validation, planning, status,
   and application from CI/CD, including authorized destructive work. This is the production path;
   Groundwork is not an application-startup migrator and has no automatic startup fallback.

## The schema tool

Install `Groundwork.Tool` in the repository that owns the deployment pipeline:

```bash
dotnet new tool-manifest
dotnet tool install Groundwork.Tool --version 0.0.1
dotnet groundwork --version
```

Use the **same Groundwork release version** for `Groundwork.Tool`, `Groundwork.Core`, and the
selected provider package — the manifest-source assembly is loaded into the tool process and must
be binary-compatible with the tool release. `--version` reports the exact installed package
version so pipelines can assert the match before loading application code.

### Expose the manifest

The application assembly implements the Core-only `IPhysicalSchemaManifestSource`. It does not
choose a provider, accept a connection, or reference a provider SDK:

```csharp
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;

public sealed class ApplicationSchema : IPhysicalSchemaManifestSource
{
    public StorageManifest CreateManifest() => ApplicationManifests.Storage;

    public IPhysicalNamePolicy CreateNamePolicy() =>
        new DelegatePhysicalNamePolicy(context => $"app_{context.FeatureDefaultLogicalName}");
}
```

When the application also declares immutable diagnostic-record streams, implement
`IDiagnosticRecordDeploymentManifestSource` from `Groundwork.DiagnosticRecords`; the tool treats
the document manifest and stream snapshots as one deployment input. See [[Diagnostic-Records]].

### Commands

Provider aliases are `sqlite`, `sqlserver`, `postgresql`, and `mongodb`. Build the application
first, then point the tool at the built assembly (add `--manifest-type` when the assembly contains
more than one source):

```bash
dotnet groundwork validate \
  --manifest-assembly ./bin/Release/net10.0/Application.dll \
  --manifest-type ApplicationSchema \
  --provider sqlite \
  --connection-env GROUNDWORK_DEPLOYMENT_CONNECTION \
  --output json
```

- **`validate`** (live, the default) opens the provider, reads durable applied state
  point-in-time, and computes readiness — without locking, creating infrastructure, or changing
  anything. It also validates the applied physical objects, detecting drift even when a target
  change is pending. `validate --offline` is the connection-free manifest/route compilation mode.
- **`plan`** computes the exact pending operations; **`status`** reports applied vs. pending.
  Both use the same non-locking inspection path and block when they find drift in the recorded
  applied schema.
- **`apply`** alone acquires the provider/manifest exclusion lock, then reads, authorizes, and
  executes one exact plan without releasing the lock between phases.

MongoDB additionally requires `--database` unless the connection URI contains the database name.
Prefer `--connection-env` over `--connection` — command-line arguments can be visible in process
listings; the variable name and value are never emitted.

### Safe vs. authorized application

```bash
# Safe: refuses every operation carrying destructive or semantic-evolution metadata.
dotnet groundwork apply ... --safe

# Authorized: bind approval to the exact plan fingerprint and operation identities
# previously retained from `plan --output json`.
dotnet groundwork apply ... \
  --expected-plan 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef \
  --allow-destructive create-physical-entity-storage:documents:example:0123456789abcdef \
  --allow-semantic reclassify-v2
```

`--allow-destructive` and `--allow-semantic` are repeatable and match exact identities. A stale
`--expected-plan` is rejected after the provider lock is acquired and before any target
operation — approval can never authorize a different plan observed after an unlocked preflight.

### Output and exit codes

`--output json` emits one compact, stable JSON object (schema version 1): outcome, provider and
target identity, plan fingerprint, deterministically ordered resolved physical names, pending and
applied operation identities, authorization-requiring identities, and blocking diagnostics. No
timestamps, connection values, exception messages, or stack traces are emitted.

| Code | Name | Meaning |
|---:|---|---|
| `0` | success | Validation passed, no work pending, or apply completed/reconciled. |
| `2` | pending changes | `plan` or `status` found applicable pending operations. |
| `3` | validation failed | Blocking manifest, route, history, or physical-schema diagnostics. |
| `4` | authorization required | Safe apply found protected work, approval incomplete, or locked plan differs from `--expected-plan`. |
| `5` | invalid invocation | Required source, provider, connection, database, or option input missing/invalid. |
| `10` | execution failed | Provider execution failed; details deliberately suppressed. |
| `130` | cancelled | Cancellation stopped the command before unapplied state was published. |

For a plan gate, accept `0` or `2` and fail every other value; for a deployment gate, require
`apply` to return `0`:

```bash
set +e
dotnet groundwork plan ... --output json > groundwork-plan.json
code=$?
set -e
if [ "$code" -ne 0 ] && [ "$code" -ne 2 ]; then
  exit "$code"
fi

dotnet groundwork apply ... --safe --output json > groundwork-apply.json
```

Grant the deployment identity only the provider permissions required for the declared target plus
Groundwork's lock, operation-evidence, and applied-state infrastructure.

### Combined document + diagnostic deployment

Combined deployment is a **convergent two-resource protocol**, not a distributed transaction: the
document schema and the diagnostic streams each use their provider's own durable idempotent
protocol. `apply` first rejects incompatible diagnostic drift without changing anything, applies
the document target, then materializes the declared streams. If the second step fails, the result
is `incomplete` with `targetMutated: true` and `GW-DIAG-DEPLOY-004`; rerunning `apply` converges
safely. Treat a non-zero apply as *incomplete*, not as rolled back.

## See also

- [[Opening-Stores]] — startup admission and `AutoApplyOnStartup`.
- [[Providers]] — provider-specific locking, identifier limits, and type mappings.
- [Groundwork schema tool](https://github.com/valence-works/Groundwork/blob/main/docs/schema-tool.md)
  — the complete CLI guide this page summarizes.
