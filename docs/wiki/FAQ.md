# FAQ

Common questions, errors, and diagnostics, grouped by theme. Diagnostic codes are stable — search
this page for the code you are seeing.

## Design questions

### Why is there no `IQueryable` / LINQ support?

Deliberately. Arbitrary LINQ translation is a large, provider-sensitive surface whose unsupported
corners only surface in production. Groundwork's bounded contracts state their whole shape up
front so every provider can certify them **before traffic**, and required queries provably execute
server-side. Anything outside the declaration fails at startup instead of silently degrading. See
[[Declaring-Storage]] and
[ADR 0003](https://github.com/valence-works/Groundwork/blob/main/docs/adr/0003-adopt-three-physical-storage-forms.md).

### Why did my query fail at startup instead of just running slowly?

Because Groundwork never emits an unbounded client fallback. A declaration the provider cannot
execute server-side (or, for scale-bearing queries, cannot serve from an index) returns
diagnostics and no plans — the store never serves traffic. Fix the declaration, add the demanded
index, or pick a provider/form that supports the shape. See [[Querying]].

### What happened to the portable document model / `CreateAsync` / `PortableDocumentQuery`?

Retired. The shared `groundwork_documents` tables, `RelationalDocumentStore`,
`MongoDbDocumentStore`, the portable `CreateAsync` factories, and the
`IndexDeclaration`/`PortableQueryDeclaration`/`PortableDocumentQuery`/`DocumentStoreQuery` types
carry `GW0001`–`GW0005` obsolete diagnostics and are removed in the announced breaking cleanup.
Migrate to `OpenPhysicalAsync` + declared `DocumentQuery` — this wiki teaches only that path. See
[ADR 0006](https://github.com/valence-works/Groundwork/blob/main/docs/adr/0006-retire-the-portable-document-model.md).

### Does Groundwork provide queues, outboxes, or distributed locks?

No — by decision, not omission. Groundwork owns the portable primitives those protocols need
(optimistic concurrency, monotonic ordering keys, multi-document atomic commit) and consumers own
the protocols, which is where implementations legitimately differ. The former
`Groundwork.Operational` packages were retired
([ADR 0004](https://github.com/valence-works/Groundwork/blob/main/docs/adr/0004-retire-groundwork-operational.md)).
The [[Inbox sample|Samples]] shows how to contribute such a semantic as your own capability
module.

### Can I add my own provider or persistence semantic?

External providers implement `IDocumentStore` and expose their own admission-first factory (the
built-in stores' constructors are internal). New persistence *semantics* are contributed as
capability modules — see the Inbox walkthrough in [[Samples]] and the capability model in
[[Providers]].

## Errors and diagnostics

### SQL Server: "requires bounded String key column"

Your manifest has a string/keyword index key with no declared length, and SQL Server's index keys
are sized. Declare `length:` on the `LogicalIndexDeclaration` (or per field). See
[[Declaring-Storage]] and [[Provider-SQL-Server]].

### `GW-PHYSICAL-018` on a Number index field

Numeric scale-bearing demand needs an explicit decimal shape — the resolver refuses to invent
precision/scale. Declare `Precision`/`Scale` on the index (or field). Precision 1–28 portably;
SQLite narrows to 1–18. See [[Declaring-Storage]].

### `GW-PHYSICAL-038` / `GW-PHYSICAL-039`

Invalid declared numeric shape (038) or key length (039): partial precision/scale pairs,
declarations on non-matching value kinds, or two indexes demanding different shapes for the same
path. See [[Declaring-Storage]].

### `GW-ROUTE-007`: unique index that keeps rows without a value

A unique index with `MissingValueBehavior.IncludedAsNull` is refused because providers genuinely
disagree about whether two null rows collide. Use `Excluded` — uniqueness then applies only to
rows that have a value (null-distinct semantics, identical on every provider).

### `GW-SCHEMA-003`: non-additive conflict

You changed or removed an already-applied column or index. The runtime pipeline is additive-only;
destructive and semantic changes go through `dotnet groundwork apply` with explicit authorization.
One exception is admitted: widening an index's `MissingValueBehavior` from `Excluded` to
`IncludedAsNull`. See [[Schema-Evolution]].

### `GW-SCHEMA-005`: semantic migration required

A projected field is declared `SemanticMigrationRequired`; the additive pipeline never substitutes
canonical-JSON extraction for an explicitly required transform, and authorization flags don't
change that — an authored provider-neutral semantic migration must exist.

### `GW-CAP-014`: unregistered capability

A manifest declares a `CapabilityId` requirement that no registered module contributes. Register
the owning module in your `GroundworkModuleCatalog` (see [[Samples]]).

### `GW-RELATIONSHIP-012`: relationship declarations rejected

All shipped providers currently reject every manifest containing a relationship declaration or
relationship guard, before schema or document I/O. Cross-storage relationship guards are declared
surface without provider execution yet — don't use them.

### `UnsupportedAtomicCommitException` on MongoDB

You called `BeginAsync` against a standalone deployment. MongoDB multi-document transactions
require a replica set or sharded cluster; run a single-node replica set for local development, or
check `store.TransactionBoundary` and take a compensation path. See [[Provider-MongoDB]].

### Materialization failed after adding a unique index

The existing documents contain duplicate values for the newly-unique field. The backfill violates
the constraint and the whole materialization rolls back — loudly, by design. Reconcile the data
first. See [[Schema-Evolution]].

### `GW-DIAG-DEPLOY-*` when opening a diagnostic session

Diagnostic-record session factories perform read-only admission and never create or repair
schema. Missing or drifted stream storage means deployment hasn't run (or drifted) — deploy with
the CLI. `GW-DIAG-DEPLOY-004` from `apply` means the document target applied but stream
materialization is unfinished; rerun `apply` to converge. See [[Diagnostic-Records]].

## Operations

### How do I add an index to a unit that already has data?

Add the `LogicalIndexDeclaration` to the manifest. The diff detects it — even without a manifest
version bump — and plans index creation plus a canonical-JSON backfill so pre-existing documents
become visible. Apply at startup (`AutoApplyOnStartup`, development) or via
`dotnet groundwork apply --safe` (deployment). See [[Schema-Evolution]].

### Should I use `AutoApplyOnStartup` in production?

Prefer not to. It only ever applies safe additive work, but the production posture is inspect-only
startup plus explicit CLI deployment from your pipeline, with `plan` gates (exit codes 0/2) and
`apply --safe` or fingerprint-bound authorized applies. See [[Schema-Evolution]].

### Which package versions must match?

Use the same Groundwork release for `Groundwork.Tool`, `Groundwork.Core`, and your provider
package. The manifest-source assembly is loaded into the tool process and must be
binary-compatible; `dotnet groundwork --version` reports the exact package version so pipelines
can assert the match.

### Can I edit the GitHub wiki directly?

No — the wiki is generated from
[`docs/wiki/`](https://github.com/valence-works/Groundwork/tree/main/docs/wiki) in the main
repository and republished on every push to `main`. Open a PR against those files instead; direct
wiki edits will be overwritten.
