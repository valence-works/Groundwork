# Querying

All reads go through `IBoundedDocumentStore` with a `DocumentQuery` — one closed runtime model
bound to a `BoundedQueryDeclaration` identity from your manifest. Feature code never submits a
table, index, provider expression, `IQueryable`, or physical plan. Query planning validates every
shape against the declaration and the provider's handlers at startup; unsupported server-side
shapes fail compilation rather than falling back to an unbounded in-memory scan.

## The store contract

```csharp
public interface IBoundedDocumentStore
{
    Task<DocumentQueryResult> QueryAsync(DocumentQuery query, CancellationToken cancellationToken = default);
    Task<long> CountAsync(DocumentQuery query, CancellationToken cancellationToken = default);
    Task<DocumentEnvelope?> FirstOrDefaultAsync(DocumentQuery query, CancellationToken cancellationToken = default);
    Task<bool> AnyAsync(DocumentQuery query, CancellationToken cancellationToken = default);
}
```

How you get one differs per provider: relational providers create a query runtime per executable
route; the MongoDB store is itself the bounded store for every unit. See [[Opening-Stores]].

## Building a `DocumentQuery`

A query names its document kind and bounded-query identity, then adds declared comparisons:

```csharp
var query = new DocumentQuery(
    "supportTicket",
    "list-by-status",
    [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "open"))],
    take: 25);

DocumentQueryResult page = await boundedStore.QueryAsync(query);
long total = page.TotalCount;
```

The clause list is an **AND of OR-groups**: each `DocumentQueryClause` holds one or more
comparisons that are OR-ed together, and all clauses must hold.

- `DocumentQueryClause.Of(comparison)` — a single-comparison clause.
- `DocumentQueryClause.AnyOf(a, b, ...)` — a disjunction (requires the declaration to support it).
- `DocumentQueryClause.MatchNone` — a constant-false sentinel.
- Zero clauses match all documents of the kind (within the session's scope).

Fluent copies compose a query without mutating it: `Where`, `OrderBy`/`ThenBy`, `Page(skip,
take)`, `ContinueAfter(continuation)`, `LatestPerKey(path)`, and
`Select(BoundedQueryResultOperation...)`.

Richer declarations unlock richer runtime queries — membership (`In`), declared substring/prefix
(`Contains`/`StartsWith`), ranges, compound predicates and compound order, keyset paging, and
latest-per-key selection — always validated against what the declaration and provider support.

## Operator semantics

Operator semantics match EF Core exactly:

- `Equal` with a `null` value matches documents whose field is null/absent.
- `In` over an empty set matches nothing.
- `Contains` is case-insensitive, and a null field yields no match (never throws).
- `NotEqual` is the exact complement of `Equal`: a null/absent field is *not* equal to
  `"archived"`, so it comes back from `NotEqual("archived")`; conversely `NotEqual(null)` returns
  only documents that have a value. This is deliberately not SQL's three-valued `<>` — every
  document lands on exactly one side of `Equal`/`NotEqual`, so a two-branch query can neither lose
  a document nor count it twice.

## Result operations

`BoundedQueryResultOperation` selects what executes: `Documents` (default), `Count`, `First`, or
`Any`. The convenience members dispatch accordingly:

```csharp
DocumentEnvelope? first = await boundedStore.FirstOrDefaultAsync(
    query.Select(BoundedQueryResultOperation.First));
bool any = await boundedStore.AnyAsync(query.Select(BoundedQueryResultOperation.Any));
long count = await boundedStore.CountAsync(query.Select(BoundedQueryResultOperation.Count));
```

`DocumentQueryResult` carries the page window (`Documents`), the total predicate count
(`TotalCount`, when the declaration set `supportsTotalCount: true`), and an opaque
`NextContinuation` when the declared cursor route has another page.

## Paging: offset and keyset

- **Offset paging** (`QueryPagingSupport.Offset`): use `Skip`/`Take` (or `Page(skip, take)`).
- **Keyset paging** (`QueryPagingSupport.Cursor`): take the result's `NextContinuation` and pass
  it back via `ContinueAfter(...)`. A document-query continuation is opaque and bound to its plan,
  query shape, and scope; it is stable across restart but intentionally uses live-view semantics
  between page requests. Offset and keyset paging cannot be requested together.

Every ordered plan appends the document identity as an ascending total-order tie-breaker, so
paging is deterministic even when your declared sort keys tie.

Note that not every provider certifies every paging mode — the current SQLite profile, for
example, does not advertise keyset paging or latest-per-key, and such declarations fail before
traffic. See [[Providers]].

## Ordering and compound prefixes

Requested order must match the declaration's per-path sort directions, either forward or fully
reversed against the physical index. When a declaration lists an equality predicate prefix
followed by a sort suffix, a runtime request using the suffix must supply exactly one standalone
equality comparison for every skipped prefix field; an absent prefix or an equality inside a
disjunction is rejected before dispatch.

## Failure model

Compilation is atomic and happens when the query runtime is constructed: unsupported operations,
terminals, disjunction, compound predicates, paging, latest selection, field paths, prefixes,
directions, or sources return diagnostics and **no plans** — the store never serves traffic.
Scale-bearing declarations additionally require an indexed physical or provider-native route.
Groundwork never emits an unbounded client fallback. At runtime, requests resolve by bounded-query
identity and stable predicate/order paths before dispatch; a shape outside the declaration is
rejected without provider I/O.

## Explaining a query

`IPhysicalDocumentQueryExplainer.ExplainAsync` accepts the same `DocumentQuery` used for execution
and returns the compiled plan, a runtime-invocation fingerprint, and the ordered provider-native
commands planned for that operation (formats: `sqlite-query-plan`, `sqlserver-statistics-xml`,
`postgresql-json`, `mongodb-json`). Explanation is a diagnostic operation, not a dry run — it can
consume database resources and observe live data, and native plans are returned unsanitized, so
treat them as sensitive.

## See also

- [[Declaring-Storage]] — the declarations queries bind to.
- [[Storage-Scopes]] — scope is injected from the session, never a query field.
- [Bounded physical query plans](https://github.com/valence-works/Groundwork/blob/main/docs/bounded-physical-query-plans.md)
  — source selection, compound-prefix rules, index pinning, and plan diagnostics in depth.
