# Transactions and unit of work

For write commands that persist several related documents all-or-nothing, an `IDocumentStore` is
also an `IDocumentSessionFactory`: it begins a document unit of work over a declared
`DocumentCommitScope`. Staged `Save`/`Delete` operations become visible only on `CommitAsync`;
`RollbackAsync` (or disposing without committing) discards them.

```csharp
using Groundwork.Core.Transactions;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;

// Detect native cross-document atomicity before committing to a path (no exception needed).
if (store.TransactionBoundary != TransactionBoundary.CrossUnitAtomic)
    /* use a compensation fallback */;

await using var unitOfWork = await store.BeginAsync(
    DocumentCommitScope.Of("workflow-version", "workflow-definition", "layout"));
try
{
    var saved = await unitOfWork.SaveAsync(new SaveDocumentRequest(/* version doc */));
    if (saved.Status != DocumentStoreWriteStatus.Saved)
    {
        await unitOfWork.RollbackAsync();   // all-or-nothing: caller rolls back on any non-success
        return;
    }

    await unitOfWork.SaveAsync(new SaveDocumentRequest(/* updated definition doc */));
    await unitOfWork.DeleteAsync(new DeleteDocumentRequest(/* stale layout record */));

    await unitOfWork.CommitAsync();
}
catch
{
    await unitOfWork.RollbackAsync();
    throw;
}
```

## The contract

- **Boundary detection.** `TransactionBoundary` reports `CrossUnitAtomic` when the store can
  commit multiple documents atomically, or `PerOperation` when it cannot — letting callers choose
  a compensation path without catching an exception.
- **Commit scope.** `DocumentCommitScope.Of(...)` names the storage units (document kinds) the
  unit of work will touch. A commit scope that mixes global and scoped storage-unit policies is
  rejected before a provider transaction opens, and save/delete/load of an undeclared document
  kind is rejected before provider traffic without poisoning the transaction.
- **Staging.** `SaveAsync`/`DeleteAsync` run against the open unit of work and return their normal
  `DocumentStoreWriteResult` immediately (including `ConcurrencyConflict`/`NotFound`). They are
  **not** auto-committed; the all-or-nothing guarantee is the caller's: roll back on any
  non-success result or exception. `LoadAsync` inside the unit of work sees staged writes.
- **Terminal on failure.** Any in-scope save/delete failure or non-success outcome rolls back the
  complete transaction and makes that unit terminal; callers must begin a new unit of work.
- **Scope inheritance.** A unit of work inherits its store's access context (`Global` or a
  specific `StorageScope`) for every enlisted operation. See [[Storage-Scopes]].

## Provider behavior

- **Relational** (SQLite/PostgreSQL/SQL Server) is `CrossUnitAtomic`, backed by a real
  `DbTransaction`. Some engines (e.g. PostgreSQL) abort the whole transaction on the first failed
  statement, so rollback is the only valid next step after a non-success result.
- **MongoDB** uses a multi-document transaction over a client session, which requires a replica
  set or sharded deployment (reported as `CrossUnitAtomic`). On a standalone deployment the
  boundary is `PerOperation` and `BeginAsync` throws `UnsupportedAtomicCommitException` — a loud
  failure rather than silent non-atomic writes.

The `AtomicCommit` capability (`groundwork.operational.atomic-commit`) is the executable
capability behind `IDocumentUnitOfWork`; it is the one built-in every shipped provider advertises,
and manifests whose correctness depends on cross-unit atomicity can declare it through
`StorageIntent.Operational`. See [[Providers]].

## Single-document writes don't need a unit of work

Point `Save`/`Delete`/`Load` by document identity are atomic per operation, with optimistic
concurrency via `ExpectedVersion` (see [[Getting-Started]]). Reach for a unit of work only when
several documents must commit or fail together.

## See also

- [[Opening-Stores]] — where the store and its access context come from.
- [[Storage-Scopes]] — scope and unit-of-work semantics.
- [ADR 0004](https://github.com/valence-works/Groundwork/blob/main/docs/adr/0004-retire-groundwork-operational.md)
  — why Groundwork provides the atomic-commit primitive and consumers own protocols (queues,
  outboxes, leases) built on it.
