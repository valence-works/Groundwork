# Diagnostic records

`Groundwork.DiagnosticRecords` provides bounded **append/query/inspection/retention** contracts
for immutable, time-ordered, tenant-scoped diagnostic streams — structured logs, telemetry traces,
and similar. It is a separate contract family from document storage: streams are **not** storage
units, they have no update path, and they are deliberately not an event store, outbox, or
arbitrary query engine.

## Declaring and deploying streams

Stream definitions compose with the document manifest through
`DiagnosticRecordDeploymentManifest` and deploy alongside document units through the same schema
tooling:

```csharp
using Groundwork.DiagnosticRecords;

public sealed class ApplicationDeployment : IDiagnosticRecordDeploymentManifestSource
{
    public StorageManifest CreateManifest() => ApplicationManifests.Storage;

    public DiagnosticRecordDeploymentManifest CreateDeploymentManifest() => new(
        ApplicationManifests.Storage,
        ApplicationManifests.DiagnosticStreams);
}
```

The `dotnet groundwork` commands plan, inspect, validate, and apply both declarations from one
application source. Combined deployment is a convergent two-resource protocol — see
[[Schema-Evolution]] for the `apply` semantics, `GW-DIAG-DEPLOY-004`, and why a non-zero apply
means *incomplete*, not rolled back.

## Opening a session

`IDiagnosticRecordStoreSessionFactory` is the provider-neutral host boundary. Provider packages
expose `CreateSessionFactory` helpers (for example
`SqliteDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)`). A host opens a
session with the combined deployment and one `DiagnosticStorageScope`, then opens only a declared
stream:

- The returned store and every handler exposed through `Handlers` reject a different scope or
  stream.
- Every session factory performs **read-only admission** when the session opens and again
  immediately before its first store lease is exposed. Missing or drifted storage fails with a
  stable `GW-DIAG-DEPLOY-*` code; runtime session creation never creates, repairs, or materializes
  schema — deployment is the CLI's job.
- Concurrent opens of the same stream share one provider lease; session disposal coordinates with
  in-flight opens.
- MongoDB's session factory preserves the replica-set/sharded topology gate.

## The store contract

```csharp
public interface IDiagnosticRecordStore
{
    DiagnosticRecordStoreHandlers Handlers { get; }

    ValueTask<DiagnosticAppendResult> AppendAsync(DiagnosticRecordBatch batch, CancellationToken cancellationToken = default);
    ValueTask<DiagnosticRecordPage> QueryAsync(DiagnosticRecordQuery query, CancellationToken cancellationToken = default);
    ValueTask<DiagnosticRecordGroupPage> QueryGroupsAsync(DiagnosticRecordGroupQuery query, CancellationToken cancellationToken = default);
    ValueTask<DiagnosticStreamStatistics> InspectAsync(DiagnosticStreamInspectionRequest request, CancellationToken cancellationToken = default);
    ValueTask<DiagnosticTrimResult> TrimAsync(DiagnosticTrimRequest request, CancellationToken cancellationToken = default);
}
```

- **Append** — batches of immutable records into one tenant/scope/stream.
- **Query** — bounded, declared query shapes over one stream (like document queries, the shape is
  closed; ordinary query handlers can never accidentally group or scan in the client — grouped
  reads not backed by a grouped handler are rejected before provider I/O).
- **QueryGroups** — declared grouped reductions (serving workloads like trace-list endpoints).
- **Inspect** — stream statistics and metadata.
- **Trim** — retention: trimming records by policy while stream metadata preserves cursor
  continuity.

## Cursors and continuations

Two concepts keep traversal stable:

- A **diagnostic cursor** is an opaque, provider-assigned monotonic position within one tenant,
  storage scope, and stream. It is the total-order tie-breaker and survives record trim through
  stream metadata. It is not an application sequence or an occurrence timestamp.
- A **diagnostic continuation** is a query-shape-bound keyset value carrying the first page's
  committed cursor high-water and the last ordered key/cursor. It provides a stable traversal that
  **excludes later and backdated appends** — unlike a document-query continuation, which
  intentionally has live-view semantics between pages.

## See also

- [[Schema-Evolution]] — deploying streams with the CLI.
- [[Glossary]] — diagnostic cursor, continuation, and record-store definitions.
- [ADR 0005](https://github.com/valence-works/Groundwork/blob/main/docs/adr/0005-separate-kernel-facilities-from-contract-families.md)
  — the kernel/contract-family split and the planned evolution of this contract family's
  ownership.
