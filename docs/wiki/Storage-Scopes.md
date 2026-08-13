# Storage scopes

A storage scope is a provider-neutral, opaque partition identity — typically a tenant — bound to a
document-store session. Groundwork stamps it into the envelope and every dependent physical key;
it is never inferred from document payload data and never read from document JSON. There is no
query flag that disables isolation.

## Every session declares its access

Every ordinary document store is created with exactly one explicit access context:

```csharp
// Serves storage units declared TenancyPolicy.Scoped:
DocumentStoreAccess.Scoped(new StorageScope("tenant-a"))

// Serves only units deliberately declared TenancyPolicy.Global:
DocumentStoreAccess.Global
```

There is no null, ambient, or payload-derived scope. A scoped session cannot access a global
unit, a global session cannot access a scoped unit, and the mismatch is rejected before provider
I/O. Application authorization for acquiring either session remains outside Groundwork — Groundwork
enforces the partition, your application decides who gets which session.

## Scope values

Scope values are opaque and compared ordinally, including case and Unicode code-unit
distinctions. For portable provider behavior they are limited to 128 UTF-16 code units, cannot
have leading or trailing whitespace, and cannot use Groundwork's reserved `__groundwork_` prefix.
The explicit envelope field name is `storageScope`; a payload field named `tenantId` remains
ordinary payload with no isolation meaning.

## What isolation means in practice

- The same document identity and the same unique projected value can exist in independent scopes:
  document primary keys and unique physical indexes include the scope key.
- Wrong-scope point reads and deletes return the same not-found outcomes as absent records — a
  scoped session cannot even observe that another scope's document exists.
- A compare-and-swap update in the wrong scope cannot observe the other scope's version or
  dependent rows.
- Every query plan carries a mandatory scope field compiled from the storage route; it is not
  copied from caller payload and not exposed as a removable predicate. See [[Querying]].
- An explicit unit of work inherits its store's access context, and a commit scope that mixes
  global and scoped units is rejected before a provider transaction opens. See
  [[Transactions-and-Unit-of-Work]].

## Privileged access

Cross-scope work requires an explicitly acquired privileged session carrying a
`PrivilegedStorageAccess` capability, through one of three deliberate paths:

1. **Targeted scoped access** — a privileged session for one specific scope.
2. **Targeted global access** — a privileged session for global storage.
3. **Cross-scope query access** — queries spanning scopes. Cross-scope sessions cannot perform
   point writes, loads, or deletes, because those operations require an unambiguous target scope.

Privileged acquisition is never the result of a *missing* ordinary scope, and acquisition and
rejected access emit audit evidence through `IStorageScopeObserver` and the
`Groundwork.Documents.StorageScope` activity source — with low-cardinality event shapes that
contain no scope value.

## How providers key it

- Relational providers persist `storage_scope` in the document envelope; primary keys, dependent
  foreign keys, unique indexes, and linked projection keys include it, and synthesized scoped
  physical indexes place it first.
- SQL Server retains exact binary-collated originals and uses persisted fixed-width SHA-256 shadow
  columns for native composite keys, so maximum legal values cannot exceed its 900/1700-byte index
  limits (see [[Provider-SQL-Server]]).
- MongoDB uses a composite `_id` containing scope and logical id, also persists `storage_scope`,
  and prefixes declared native indexes with it (see [[Provider-MongoDB]]).

Each provider's conformance suite proves the same bound operations and native keys, so isolation
semantics do not vary by provider.

## See also

- [[Declaring-Storage]] — `TenancyPolicy.Global` vs `TenancyPolicy.Scoped` on the unit.
- [[Opening-Stores]] — passing `DocumentStoreAccess` to the factories.
- [Storage-scope sessions](https://github.com/valence-works/Groundwork/blob/main/docs/storage-scope-sessions.md)
  — the full design note, including observability and conformance evidence.
