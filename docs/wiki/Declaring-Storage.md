# Declaring storage

Everything Groundwork does starts from a `StorageManifest`: a provider-neutral declaration of the
storage units your module owns, the logical indexes over them, and the bounded queries and
mutations your application will execute. The manifest is the whole contract — providers certify it
before traffic, the schema tooling deploys it, and nothing outside it is executable.

## The manifest

```csharp
var manifest = new StorageManifest(
    new StorageManifestIdentity("support-tickets"),      // stable manifest identity
    new StorageManifestOwner("sample.support"),          // owning module
    new StorageManifestVersion("1.0.0"),                 // schema version
    [ /* storage units */ ],
    new HashSet<string> { "schema-history", "optimistic-concurrency" },  // required facilities
    []);
```

Manifest identity, owner, and version travel into schema history and fingerprints. Note that the
schema diff is driven by durable *semantic* fingerprints, not the version string alone — adding an
index without bumping the version is still detected as pending work (see [[Schema-Evolution]]).

## Storage units

A `StorageUnit` declares one document kind and its policies:

```csharp
StorageUnit.Create(
    new StorageUnitIdentity("supportTicket"),
    "Support ticket",
    StorageIntent.PortableDocument(),
    LifecyclePolicy.Mutable,
    IdentityPolicy.StringId(),
    TenancyPolicy.Global,
    ConcurrencyPolicy.Optimistic(),
    SerializationPolicy.Json(),
    new StorageUnitPhysicalStorage(...));
```

- **`StorageIntent`** declares the provider capabilities the unit requires; provider fit is
  computed from those declared requirements, never from author self-declaration.
  `StorageIntent.PortableDocument()` is the default document/table contract;
  `StorageIntent.Operational(rationale, descriptor, requirements)` declares `CapabilityId`
  requirements plus the rationale when correctness depends on more — atomic commit across units,
  concurrency evidence, or custom semantics contributed by external modules such as the
  [Inbox sample](https://github.com/valence-works/Groundwork/tree/main/samples/Groundwork.Modules.Inbox)
  (see [[Samples]]).
- **`TenancyPolicy`** is `Global` or `Scoped` — there is no ambient or payload-derived tenancy.
  A scoped unit is only reachable through a session opened with a matching `StorageScope`; see
  [[Storage-Scopes]].
- **`ConcurrencyPolicy.Optimistic()`** enables version-gated saves and deletes via
  `ExpectedVersion`.
- **`SerializationPolicy.Json()`** — documents are canonical JSON envelopes; the JSON stays
  authoritative in every physical form.

## Physical storage: forms and policies

`StorageUnitPhysicalStorage` fixes how the unit is provisioned:

```csharp
new StorageUnitPhysicalStorage(
    StorageUnitProvisioningMode.Declared,
    PhysicalStoragePolicy.Default(),      // or PhysicalStoragePolicy.Explicit(definition)
    logicalIndexes: [...],
    boundedQueries: [...],
    boundedMutations: [...]);             // optional
```

Physical intent uses exactly three provider-neutral forms
([ADR 0003](https://github.com/valence-works/Groundwork/blob/main/docs/adr/0003-adopt-three-physical-storage-forms.md)):

1. **Shared documents** — dynamic/runtime-defined units share a provider-level documents structure
   with linked index storage.
2. **Dedicated document table** — a declared unit without scale-bearing projected-field demand
   gets its own table with the standard envelope and canonical JSON.
3. **Physical entity table** — a declared unit whose bounded queries mark stable non-envelope
   paths as scale-bearing gets envelope, canonical JSON, *and* typed projected columns in one row.

All three retain the standard envelope and authoritative canonical JSON; projected columns are
rebuildable derivatives, never a second source of truth. The form is a manifest and planner
decision, not a caller-visible query choice — callers always use the same store and query
contract.

`PhysicalStoragePolicy.Default()` synthesizes the projected columns and physical indexes your
declarations demand. When you need to state the physical shape yourself — exact columns, index key
order, identity tie-breaks — use `PhysicalStoragePolicy.Explicit` with a
`PhysicalTableDefinition`; the
[sample manifest](https://github.com/valence-works/Groundwork/blob/main/samples/Groundwork.SupportTickets/SupportTicketManifest.cs)
declares explicit physical-entity tables that match what the default policy would synthesize.

## Logical indexes

A `LogicalIndexDeclaration` names an index over stable serialized content paths:

```csharp
new LogicalIndexDeclaration(
    "by-ticket-number",
    [new IndexField("ticketNumber")],
    IndexValueKind.Keyword,
    isUnique: true,
    MissingValueBehavior.Excluded,
    length: 128)
```

- **`IndexValueKind`** supplies the default value semantics for the whole index (`Keyword`,
  `String`, `Number`, date-time, …). In a heterogeneous compound index an individual
  `IndexField.ValueKind` may override the default, making differences such as keyword identity
  plus date-time ordering explicit.
- **`isUnique`** — uniqueness is enforced within the storage scope: the same unique value can
  exist in independent scopes because scoped physical unique indexes include the scope key.
  Adding a unique index over pre-existing duplicate values fails materialization loudly; the data
  must be reconciled first.
- **`MissingValueBehavior`** decides what happens to documents with no value for a nullable keyed
  column. `Excluded` omits those rows from the index (a filtered/partial index on every provider)
  and gives *null-distinct* uniqueness — the constraint applies only to rows that have a value.
  `IncludedAsNull` keeps them. A unique index that keeps rows without a value is refused at route
  compilation (`GW-ROUTE-007`) because providers genuinely disagree about whether two such rows
  collide.

### Declared key lengths (strings)

`IndexValueKind.String`/`Keyword` fields synthesize string projected columns. Unbounded string
keys are valid on PostgreSQL, SQLite, and MongoDB, but SQL Server's index keys are sized and
reject unbounded key columns. Declare the maximum key length — a count of UTF-16 code units — on
the field or as a declaration-level default
([ADR 0008](https://github.com/valence-works/Groundwork/blob/main/docs/adr/0008-declared-index-key-lengths.md)):

```csharp
new LogicalIndexDeclaration("by-status", [new IndexField("status")],
    IndexValueKind.Keyword, isUnique: false, MissingValueBehavior.Excluded,
    length: 128)
```

Validation (`GW-PHYSICAL-039`): a length is at least 1, only `String`/`Keyword` fields may declare
one, and all scale-bearing demand for one path within a unit must agree on a single declared
length. An absent length stays unbounded — providers that require a bound keep rejecting it at
route compilation.

### Declared precision and scale (numbers)

`IndexValueKind.Number` fields map to portable decimal columns, which require an explicit
precision and scale — the resolver deliberately refuses to invent them. Declare the pair on the
field or as a declaration-level default
([ADR 0007](https://github.com/valence-works/Groundwork/blob/main/docs/adr/0007-declared-index-numeric-precision-and-scale.md)):
precision 1–28 (the provider-portable envelope set by SQL Server index-key sizing; SQLite narrows
it to 1–18), scale 0–precision, both components declared together. Validation is
`GW-PHYSICAL-038`; numeric scale-bearing demand with no declared shape fails with
`GW-PHYSICAL-018`.

## Bounded queries

One `BoundedQueryDeclaration` per read the application performs:

```csharp
new BoundedQueryDeclaration(
    "list-by-status",              // bounded-query identity, used by runtime DocumentQuery
    "by-status",                   // the logical index it addresses
    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
    QuerySortSupport.Ascending,
    QueryPagingSupport.Offset,     // or Cursor for keyset paging, or None
    BoundedQueryExecutionClass.ScaleBearing,
    supportsTotalCount: true)
```

A declaration owns its whole shape up front:

- **Operators** — equality, inequality, membership (`In`), prefix/declared substring
  (`StartsWith`/`Contains`), and ranges. Operators apply per value kind: substring and prefix only
  to string/keyword values; ranges to string/keyword, number, and date-time values.
- **Predicate fields** — explicit paths for compound-prefix validation. An equality predicate
  prefix may be followed by a sort suffix; runtime requests using the suffix must supply exactly
  one standalone equality comparison for every skipped prefix field.
- **Sort and paging** — per-path compound sort directions; offset or cursor/keyset paging. Every
  ordered plan appends the document identity as an ascending total-order tie-breaker.
- **Result operations** — documents, count, any, and first.
- **Execution class** — `Ordinary` or `ScaleBearing`. Marking a query `ScaleBearing` makes its
  referenced stable content paths *binding* demand: they must be served by typed projected columns
  with one physical index per referenced logical index, and the provider must certify an indexed
  server-side plan. A provider that cannot is rejected at startup — never scanned around.
- **Residual predicate fields** — `BoundedQueryResidualPredicateField` declares an optional typed
  server-side filter that does not add its path to the index key or predicate-prefix evidence.
  Residual paths remain closed and typed; a scale-bearing residual path must be physically
  available on the indexed primary route (use a physical entity table).

Omitting `predicateFields` filters the first path of the logical index. An explicit
`predicateFields: []` declares a truly unfiltered bounded route and must also declare its
provider-applied `sortFields`; unfiltered declarations are never valid bounded-mutation
predicates.

## Bounded mutations

Bounded document mutations are named lifecycle operations over existing bounded-query plans —
not a general update/delete language. The manifest fixes both the predicate shape and the only
allowed effect; a runtime caller supplies an operation identity and values for the declared
predicates:

```csharp
boundedMutations:
[
    new BoundedMutationDeclaration(
        "revoke-pending",
        "by-authorization-and-status",
        BoundedMutationAction.Transition("status", ["pending"], "revoked")),
    new BoundedMutationDeclaration(
        "revoke-authorization",
        "by-authorization",
        BoundedMutationAction.Assign("status", "revoked")),
    new BoundedMutationDeclaration(
        "prune-expired",
        "by-authorization-and-expiration",
        BoundedMutationAction.Delete())
]
```

- **`Transition`** changes one content path only from its finite manifest-declared source values
  to its manifest-declared target.
- **`Assign`** sets one manifest-declared scalar content projection to its manifest-declared
  target for every selected row — already-target, null, missing, and other source values are all
  processed.
- **`Delete`** removes the selected documents.

A mutation is executable only when its predicate is scale-bearing and has a physical index.
Envelope and linked-relationship fields are immutable through value actions. Execution is
transactional, ledgered, and idempotent: an identical retry returns `Replayed` with the original
count, and reusing an operation identity for a different request throws
`BoundedMutationOperationConflictException`. Ordinary point `Save`/`Delete`/`Load` by document
identity are *not* mutations — they need no declaration beyond the unit itself. See
[bounded document mutations](https://github.com/valence-works/Groundwork/blob/main/docs/bounded-document-mutations.md)
for the full execution contract.

> **Relationship guards.** `RequireNoReferences` and `RequireRelatedTargetNotEqual` exist as
> closed manifest declarations, but every shipped provider currently rejects any manifest
> containing a relationship declaration or guard with `GW-RELATIONSHIP-012` before schema or
> document I/O. Do not declare them yet.

## Diagnostic streams are not storage units

Applications that also use immutable diagnostic records compose those stream definitions through
`DiagnosticRecordDeploymentManifest`; streams are deployed alongside document units through the
same schema tooling but are a separate contract family. See [[Diagnostic-Records]].

## See also

- [[Getting-Started]] — a complete minimal manifest in context.
- [[Querying]] — how declarations become runtime `DocumentQuery` requests.
- [[Schema-Evolution]] — how a changed manifest becomes pending schema work.
- [Executable storage routes](https://github.com/valence-works/Groundwork/blob/main/docs/executable-storage-routes.md)
  and
  [bounded physical query plans](https://github.com/valence-works/Groundwork/blob/main/docs/bounded-physical-query-plans.md)
  — the deep dives behind compilation and certification.
