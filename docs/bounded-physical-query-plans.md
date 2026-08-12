# Bounded physical query plans

Tracking: [Groundwork #45](https://github.com/valence-works/Groundwork/issues/45) and
[Groundwork #94](https://github.com/valence-works/groundwork/issues/94), including the type-filtered
lookup requested by [#24](https://github.com/valence-works/Groundwork/issues/24).

Groundwork has one logical declaration/runtime-query family and one provider-selected diagnostic
plan. Feature code declares a `BoundedQueryDeclaration` and submits a `DocumentQuery`; it never
submits a table, index, provider expression, `IQueryable`, or `PhysicalQueryPlan`.

## Startup compilation

`PhysicalQueryPlanCompiler` combines:

- the immutable `ExecutableStorageRoute` compiled from the provider physical definition;
- the storage unit's logical indexes and bounded query declarations; and
- a provider-owned `PhysicalQueryPlannerCapabilities` profile backed by executable handlers.

The provider profile supplies an ordered source preference, registered handler identity for every
source, and provider-resolved native field identifiers. Each handler additionally supplies immutable
`PhysicalQueryHandlerCertification` values that bind its provider, storage unit, bounded-query and
logical-index identities, logical paths, access kind, physical target, lookup and primary objects,
physical index, provider field identifiers, and executable-route fingerprint.
`PhysicalQueryDocumentStore` verifies those claims against executable handler instances and rejects
wrong-provider, stale-route, unrelated-object/index, or mismatched-field certifications before
returning a traffic-capable store. The compiler selects the first compatible server-side handler and
records one of these access strategies:

- linked index lookup followed by primary-document lookup;
- primary envelope/index access;
- primary canonical-JSON path access;
- in-primary entity projected columns; or
- provider-native document fields.

This ordering lets a document provider prefer native fields while a relational provider prefers
linked, envelope/JSON, or entity-column handlers. Core contains no SQL, BSON, provider SDK types,
native explain model, or client-evaluation plan.

## Closed declaration

A bounded declaration owns:

- equality, inequality, membership, prefix/declared substring, and range operators;
- explicit predicate paths for compound-prefix validation;
- optional typed residual predicate paths that do not add physical-index predicate-prefix evidence;
- per-path compound sort directions;
- offset or cursor/keyset paging;
- document, count, any, and first result operations;
- optional disjunction and latest-per-key selection; and
- its `Ordinary` or binding `ScaleBearing` execution class.

A logical index supplies one default `IndexValueKind`. `IndexField.ValueKind` may override that
default for a field in a heterogeneous compound index, making differences such as keyword identity
plus date-time ordering explicit rather than inferring semantics from provider storage.

For compatibility, omitting `predicateFields` filters the first path in the logical index. Passing
an explicit empty `predicateFields: []` is different: it declares a truly unfiltered bounded route.
An explicitly unfiltered route must also declare its provider-applied `sortFields`; implicit
tie-break ordering is not sufficient evidence for a scale-bearing physical index. Its logical and
physical index must cover that declared order plus the structural document-identity tie-break:
comparison-key order for offset paging, and lookup-key order for an implicit cursor tie-break.
Unfiltered query declarations are never valid bounded-mutation predicates.
New compound declarations should always list their predicate fields. An equality predicate prefix
may be followed by a sort suffix; requested directions must match the physical index either forward
or fully reversed. Runtime requests using that suffix must provide exactly one standalone equality
comparison for every skipped prefix field; an absent prefix or an equality inside a disjunction is
rejected before dispatch. Every ordered plan appends the document identity as an ascending
total-order tie-breaker.

`BoundedQueryResidualPredicateField` declares an optional server-side filter without independently
adding its path to the logical or physical index key or contributing predicate-prefix evidence.
The same path may already be a sort-only logical and physical index key field for the query; it may
not also be a resolved predicate-prefix field. Residual paths remain closed and typed: each declares
its allowed operations, participates in scale-bearing storage demand and physical-plan fingerprints,
and is validated against provider capabilities and projected physical types. Providers apply
requested residual comparisons before count, any, first, paging limits, continuation generation,
hydration, or materialization. Runtime requests may omit optional residual comparisons; a residual
field declared with `IsRequired` must appear in the runtime request before handler dispatch.

A scale-bearing residual path must be physically available on the indexed primary route. Default
resolution therefore synthesizes a projected column for it, while explicit physical definitions
must declare a compatible projected column. A linked-index route cannot currently certify residual
filtering because its side table does not contain the residual value and filtering after primary
hydration would violate the execution order above. Use a physical entity table when a scale-bearing
query needs residual predicates. Bounded mutations reject residual predicate queries until mutation
semantics explicitly support them.

Every compiled plan also owns one document-identity binding for its selected primary, linked, or
native source. The binding carries the original, comparison-key, and lookup-key fields plus the
versioned projection algorithms. Equality, membership, and inequality bind lookup plus full
comparison evidence; prefix and range operations bind the comparison key; identity ordering and
the implicit identity tie-break use the comparison key. Identity substring matching is rejected
during plan compilation. Provider handlers consume these fields and projected values and do not
reapply the manifest's case policy.

An explicit physical index certifies the evidence shape that execution actually consumes. Exact
identity predicates require lookup-key-leading comparison-key evidence, while prefix and range
predicates require comparison-key evidence only. An index over the retained original identity does
not certify either projected shape. A scale-bearing declaration that mixes exact and ordered
identity operations is rejected with an explicit unsupported-shape diagnostic; this work does not
choose an automatic synthesized index order for that mixed demand, and automatic index synthesis
otherwise remains unchanged.

## Pinning an index that excludes null rows

Some providers pin the planned index into the emitted statement — SQL Server as `WITH (INDEX(...))`,
SQLite as `INDEXED BY`. Some indexes also exclude rows rather than merely ordering them, and exactly
one thing decides that: an index declared `MissingValueBehavior.Excluded` omits rows that have no
value for its nullable keyed columns. Every provider realises that identically — a filtered index on
SQL Server and PostgreSQL, a partial index on SQLite, a partial filter expression on MongoDB — from a
single shared rule, `PhysicalIndexNullExclusion`. Where both hold, pinning is only sound for a
predicate that cannot match the excluded rows.

Uniqueness does not enter into it. SQL Server unique indexes treat nulls as equal to one another, so
a unique index over a nullable column used to acquire a null-excluding filter as a side effect of
emulating null-distinct uniqueness — which silently turned a constraint choice into a row-visibility
choice, and made one manifest mean different things on different providers. Null-distinct uniqueness
is now what `Excluded` states: the constraint applies only to rows that have a value. The opposite
pairing, a unique index that keeps rows without a value, is refused at route compilation
(`GW-ROUTE-007`) because the providers genuinely disagree about whether two such rows collide and
SQLite cannot be told otherwise.

The pin is therefore decided per invocation, from the predicate rather than from the plan alone. A
clause proves a column non-null only when *every* alternative in that disjunction rejects nulls on
it. Equality against a non-null value, the range operations, `StartsWith` and `Contains` all reject
nulls. Equality against a null value renders `IS NULL` and does not. Neither negation does either,
because both are complements that match a null or absent field — except against a null value, where
`NotEqual` renders `IS NOT NULL` and is the one negation that rejects nulls. See
[Inequality is the complement of equality](#inequality-is-the-complement-of-equality).

MongoDB reaches the same decision from the same shared rule, but reads two operators differently
because its own predicates do. Its partial filter is `{field: {$exists: true}}`, so what has to be
proved is presence rather than non-nullness — a document holding an explicit null is in the index.
And `{$ne: v}` matches a document that has no such field, which is why inequality proves nothing there
either. Unlike SQL Server, MongoDB accepts a hint whose partial filter the query does not imply: it
serves the query from the smaller index and returns fewer documents with no error at all, which makes
deciding the pin from the predicate the only thing standing between a null-excluding index and a
silently short result set.

When the predicate proves every excluded column non-null, the query keeps its pin and carries an
`IS NOT NULL` conjunct per excluded column. Those conjuncts are redundant by construction — they are
only emitted where the predicate already rejects nulls — and exist so the provider can match the
index's own filter. SQL Server's filtered-index matching reasons over simple comparison forms and
cannot see through an expression such as `LOWER(column) LIKE @p`, so without the conjunct it refuses
to produce a plan at all.

When the predicate does not prove it, the index would drop rows the query can match. A scale-bearing
query is refused by name rather than degraded to a scan; any other query drops the pin and leaves the
choice to the optimizer. PostgreSQL emits the filter but never pins an index, so it is unaffected by
the pin decision while still honouring the declaration.

## Inequality is the complement of equality

`NotEqual` matches a document precisely when `Equal` does not, for the same value. A field that is
null or absent is therefore *not* equal to `'archived'` and comes back from `NotEqual('archived')`;
against a null value the complement runs the other way, so `NotEqual(null)` returns only documents
that have a value.

This is deliberately not SQL's three-valued `<>`, which is unknown for NULL and drops those rows. The
providers used to disagree in silence — MongoDB's `{$ne: v}` already matched documents with no field
— and nothing in the portable contract said which was right, so one manifest answered the same
question two ways. Taking the complement settles it in the direction `NotContains` was already
documented to have, and buys the property the split exists for: every document lands on exactly one
side of `Equal`/`NotEqual` for every value, so a two-branch query can neither lose a document nor
count it twice. Relational providers render the null branch explicitly, `(col IS NULL OR col <> @p)`,
the same shape `NotContains` already used. Where the column cannot be null the two forms agree, so the
sargable one is kept: document identity columns, and projected columns declared non-nullable. Anything
whose nullability is not declared — a canonical-JSON path reads as NULL when the document omits it —
takes the null-safe form, because guessing the other way drops rows.

The pin decision follows from it: because inequality against a non-null value can now match a null
row, it no longer proves a column non-null, and a scale-bearing query bound to a null-excluding index
through such a predicate is refused rather than silently under-served.

## Isolation and failure rules

Every plan contains a mandatory scope field and the scoped/global-sentinel policy compiled from the
storage route. It is not copied from caller payload or exposed as a removable query predicate.
Shared linked lookups use the linked relationship scope and discriminator before primary lookup;
primary routes use the envelope scope and discriminator.

Compilation is atomic. Unsupported operations, terminals, disjunction, compound predicates,
paging, latest selection, field paths, prefixes, directions, or sources return diagnostics and no
plans. Scale-bearing declarations additionally require an indexed physical or provider-native
route. Groundwork never emits an unbounded client fallback.

`PortableQueryOperationCompatibility` is the provider-neutral executable floor beneath provider
capabilities. Equality, inequality, and membership apply to every logical value kind; substring and
prefix operations apply only to string/keyword values; range operations apply to string/keyword,
number, and date-time values. Projected fields are checked against their compiled physical scalar
type as well, so a numeric, Boolean, date-time, GUID, JSON, or binary column cannot acquire text
semantics from a mismatched logical declaration. Incompatible explicit logical/physical pairs fail
storage resolution, and plan compilation repeats the check before certification. Providers may
certify a subset of this matrix but cannot compile or certify a combination outside it.

Plan diagnostics are canonically serialized and fingerprinted with the provider, selected objects,
index/fields, mandatory scope, predicates/operators, ordering/tie-break, paging, result operations,
latest selection, scale class, and executable-route fingerprint.

## Compatibility bridge

`DocumentQuery` is the runtime contract and binds each request to a bounded-query identity.
`PortableDocumentQuery` and `DocumentStoreQuery` carry `GW0004` obsolete guidance.
`PhysicalQueryDocumentStore` is the executable runtime seam: construction verifies registered
handler identities, compiles every declaration, and returns no traffic-capable store when planning
fails. Runtime requests resolve by bounded-query identity and stable predicate/order paths before
dispatch to the selected handler. `DocumentStoreQuery.ToDocumentQuery(queryIdentity, path)` requires
both values explicitly; it never guesses a query identity from an index name.

`LegacyPortableDocumentQueryHandler` is an explicit ordinary-query bridge for the old provider
surface. It certifies only single-field logical indexes, applies a representable planned default
order, and rejects scale-bearing, compound/multi-path, keyset, latest, and operator shapes the legacy
contract cannot express. It never collapses several stable paths into one legacy index identity.
Providers must not add a third query family.

## Runtime plan explanation

`IPhysicalDocumentQueryExplainer.ExplainAsync` accepts the same `DocumentQuery` used for execution
and dispatches through its `ResultOperation` (`Documents`, `Count`, `First`, or `Any`). Its result
contains the compiled `PhysicalQueryPlan`, a runtime-invocation fingerprint, and the ordered
`Commands` planned for that operation. Commands have stable stage identities such as count, page,
first, any, linked-identity collision check, and primary hydration; shape-conditional stages are
omitted when they are not needed, while data-dependent early exits may still stop later work.

Each command carries a provider-native plan and format. Current formats are `sqlite-query-plan`,
`sqlserver-statistics-xml`, `postgresql-json`, and `mongodb-json`. Explanation is a diagnostic
operation, not a dry-run contract: SQL Server executes the exact parameterized read under runtime
statistics collection, and MongoDB may execute bounded selector reads to explain the exact linked
primary hydration. It can therefore consume database resources and observe live data.

Native plans are provider output and are returned unsanitized; treat them as sensitive. The
runtime-invocation fingerprint excludes raw query values, but it is only a pseudonymous correlation
identifier. Low-entropy inputs may still be guessable, so the fingerprint is not a secrecy boundary.

The [relational physical storage runtime](relational-physical-storage-runtime.md) implements the
reusable relational handler and SQLite reference execution for linked+primary, dedicated, and entity
plans. Exact handler certifications are built from compiled plans; predicates, compound filters,
ordering, offset pages, counts, any, and first execute in SQL, and SQLite explain assertions prove
physical-index selection. The SQLite profile does not advertise keyset or latest-per-key execution.
Typed projected predicates bind provider values through the same portable conversion used by live
writes and backfills. Plan fields retain the declared logical semantic kind; a checked conversion
boundary then emits the compatible native representation, including GUID and binary parameters,
without changing comparison semantics. Numeric literals are validated lexically before CLR
conversion, including exponent and fixed-scale representability, and date-time literals reject
sub-100ns fractions before parsing. Literal `LIKE` wildcard input is escaped, and provider
parameter ceilings are enforced before SQL dispatch.
Intrinsic envelope paths reject a conflicting declared logical kind instead of silently switching
between numeric and lexical semantics. SQLite uses exact fixed-scale integer Decimal projections and
UTC-tick DateTime projections; its canonical-JSON source does not certify Number or DateTime plans.
SQL Server, PostgreSQL, SQLite, and MongoDB now expose provider-native bounded-query explanations.
#24 is superseded by the provider implementations in this execution slice.

## Independent review record for explicit unfiltered routes

The 2026-07-25 adversarial review of Groundwork #140 found two blockers in the initial candidate.
Correctness review demonstrated that an explicit-empty declaration could certify an index that did
not cover runtime's offset comparison-key or cursor lookup-key identity tie-break; admission and
compilation now require the paging-specific full provider order and fail incompatible shapes closed,
with resolver and direct-compiler regressions. Evidence review found that
the relational provider tests relied on compiled metadata instead of native plans; the conformance
gate now checks real SQLite, SQL Server, and PostgreSQL page plans for the declared index and absence
of a provider sort, while separately proving that count executes provider-side. Scope review found
that an unfiltered query could otherwise be reused as a bounded-delete selector; mutation compilation
and runtime validation now reject predicate plans with no indexed predicates. Exact-head
re-verification remains a required PR merge gate.
