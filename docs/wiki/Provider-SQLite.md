# Provider: SQLite

`Groundwork.Sqlite` is the reference provider implementation: SQLite materialization plus
document-store and diagnostic-record providers. It is the fastest way to run Groundwork locally
and in tests, and its conformance suite is the behavioral baseline the server providers inherit.

## Opening

```csharp
var provider = new ProviderIdentity("groundwork-sqlite", "1.0.0");
SqlitePhysicalDocumentStore store = await SqliteDocumentStoreFactory.OpenPhysicalAsync(
    "Data Source=support-tickets.db",
    manifest,
    provider,
    DocumentStoreAccess.Global,
    options: new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true });

IBoundedDocumentStore boundedStore = SqlitePhysicalQueryRuntime.Create(
    store, manifest,
    store.Routes.Single(route => route.StorageUnit.Value == "supportTicket"),
    provider);
```

See [[Opening-Stores]] for the full pattern.

## Sessions and in-memory databases

- The public connection-string factory selects the provider's **serialized session policy** and
  **rejects private in-memory databases** — every pooled connection would otherwise see its own
  empty database.
- For in-memory or test scenarios, open the `SqliteConnection` yourself and pass it to the
  connection-taking `OpenPhysicalAsync` overload; schema and identity admission still run before
  construction. The
  [sample host](https://github.com/valence-works/Groundwork/blob/main/samples/Groundwork.SupportTickets/SupportTicketSampleHost.cs)
  shows both paths.
- Explicit units of work start direct-connection transactions at the immediate writer boundary.

## Capabilities and limits

- **Keyset paging and latest-per-key are not advertised** by the current certified SQLite profile.
  Declarations requiring them fail before traffic — declare offset paging for SQLite-served
  queries.
- **Decimal projections**: precision 1–18, stored as checked fixed-scale integers. Values outside
  the declared precision/scale fail before SQL mutation.
- **DateTime projections**: UTC instants at .NET tick precision (100 ns), stored as integer UTC
  ticks; they require an explicit UTC designator or numeric offset.
- **Canonical-JSON `Number`/`DateTime` query sources are not certified** — SQLite's native JSON
  numeric conversion and `julianday` would lose those semantics. Declarations that need numeric or
  date-time predicates must provide an exact projected route (a physical entity table, or declared
  precision/scale and lengths that let default resolution synthesize one).
- Requests exceeding SQLite's parameter budget fail before dispatch, and literal `LIKE` wildcard
  input is escaped.

## Schema application

`SqlitePhysicalSchemaExecutor` creates the exact compiled objects, stages and backfills projected
columns from authoritative canonical JSON, validates, records acknowledgements in a durable
ledger, and persists applied state with compare-and-swap. Existing objects are accepted only when
they *exactly* match the compiled route — `IF NOT EXISTS` is not compatibility evidence. The
executor applies an authorized plan as **one transaction per plan** (not per operation), rolling
the entire batch back if trailing validation fails.

## Query execution

Predicates, compound filters, ordering, offset pages, counts, any, and first execute in SQL;
indexed plans are pinned with `INDEXED BY`, and `EXPLAIN QUERY PLAN` conformance evidence proves
the declared physical index is selected. Explain output format: `sqlite-query-plan`.

## Diagnostic records

`SqliteDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)` provides the
provider's diagnostic-record session factory. See [[Diagnostic-Records]].

## See also

- [[Providers]] — side-by-side comparison.
- [Relational physical storage runtime](https://github.com/valence-works/Groundwork/blob/main/docs/relational-physical-storage-runtime.md)
  — the shared relational kernel SQLite is the reference for.
