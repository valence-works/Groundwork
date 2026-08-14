# Provider: MongoDB

`Groundwork.MongoDb` serves the same manifest as the relational providers with native collections
and indexes. It consumes executable storage routes directly for shared, dedicated, and
physical-entity documents; canonical JSON stays authoritative, stored as addressable BSON and
serialized back to standard JSON on read.

## Opening: the handle owns the client

`OpenPhysicalAsync` returns an **open handle that owns the `MongoClient`**, and the store itself
is also the bounded-query store for every unit — no per-route query runtime:

```csharp
await using var handle = await MongoDbDocumentStoreFactory.OpenPhysicalAsync(
    "mongodb://localhost:27017",
    "support",                       // database name
    manifest,
    new ProviderIdentity("groundwork-mongodb", "1.0.0"),
    DocumentStoreAccess.Global,
    options: new MongoDbPhysicalDocumentStoreOptions { AutoApplyOnStartup = true });

IDocumentStore store = handle.Store;                // CRUD and unit-of-work
IBoundedDocumentStore boundedStore = handle.Store;  // declared bounded queries
```

`MongoDbPhysicalDocumentStoreOptions` carries the `AutoApplyOnStartup` boolean (MongoDB's
equivalent of `GroundworkRuntimeSchemaAdmissionOptions`). Keep the handle alive for the store's
lifetime and dispose it with `await using`.

## Topology: replica set or sharded cluster

- **Transactions require a replica set or sharded deployment.** On such topologies the store
  reports `TransactionBoundary.CrossUnitAtomic` and multi-document units of work run over a client
  session. On a standalone deployment the boundary is `PerOperation` and `BeginAsync` throws
  `UnsupportedAtomicCommitException` — a loud failure rather than silent non-atomic writes.
- A cached transaction-topology gate protects factory and direct materializer/store entry points
  **before** side effects or sessions; bounded mutations and the diagnostic-record session factory
  check the same gate. For local development, run a single-node replica set.

## Storage and route validation

- The composite `_id` contains storage scope and logical id; `storage_scope` is also persisted,
  and declared native indexes are prefixed with it. See [[Storage-Scopes]].
- Collections are created with the **simple binary collation**; a preexisting collection with
  another default collation is rejected.
- Route validation rejects **views, time-series namespaces, and capped collections**.
- Exact linked/native handler certifications bind bounded queries to resolved collections,
  fields, and indexes. Typed numeric projections validate their original JSON lexemes and
  declared shape; DateTime projections use exact UTC ticks. Native Number/DateTime paths without
  a typed projection, and unsupported keyset/latest query declarations, fail before traffic.
- JSON numbers that exceed BSON's native numeric envelope are retained through a provider-owned
  raw-number tag and come back as standard JSON numbers on read. Original JSON whitespace is not
  retained.

## Indexes and query behavior

- `MissingValueBehavior.Excluded` is realized as a partial filter expression
  (`{field: {$exists: true}}`) — presence is what is proved, so a document holding an explicit
  null *is* in the index.
- MongoDB accepts an index hint whose partial filter the query does not imply and silently returns
  fewer documents; Groundwork therefore decides the hint per invocation from the predicate, and a
  scale-bearing query that cannot prove the excluded columns are matched is refused rather than
  silently under-served.
- Indexes added to a populated collection cover pre-existing documents natively — no backfill
  step, verified by conformance tests.
- Explain output format: `mongodb-json`; explanation may execute bounded selector reads to explain
  the exact linked primary hydration.

## Bounded mutations

Compiled mutations contribute provider-owned typed mutation mirrors, strict collection validators,
and exact compound selector indexes, all deployed through the ordinary physical-schema plan (same
lease, ledger, applied snapshot, validation, and CLI lifecycle). A bounded mutation then executes
one hinted `UpdateMany`/`DeleteMany` per physical object — no per-document writes — updating
canonical BSON, native BSON, typed mirrors, projections, and versions in the same transaction as
the durable operation outcome. The validators reject writes from a host still running a
pre-mutation model during rolling coexistence instead of allowing selector-invisible documents.
`Assign` uses the matched count, so rows already at the target keep their place in the durable
replay result.

## Schema application

Generation-fenced leases atomically protect operation evidence and applied state, and
document-incarnation tokens keep restart backfill safe across delete/recreate races. With the
schema tool, use provider alias `mongodb` and pass `--database` unless the connection URI already
names the database. See [[Schema-Evolution]].

## See also

- [[Providers]] — side-by-side comparison.
- [[Transactions-and-Unit-of-Work]] — boundary detection in application code.
