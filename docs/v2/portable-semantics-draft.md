# Draft portable semantics contract

Status: Phase 0 input for Groundwork v2, produced by the G2 differential gate in issue #230.
This document records only two outcomes: a portable shape is normalized, or it is refused before
provider I/O. It is evidence for the v2 contract, not a promise that the v1 public API will grow.

The standing test uses 40 accepted edge rows, two rejected inputs, and exactly 300 unique query
shapes against SQLite, PostgreSQL, SQL Server, and MongoDB. One accepted document omits its null
properties while other documents contain explicit JSON nulls. The independent oracle and every
provider must return the same document IDs in the same order.

## Decisions

| Family | Decision | Portable rule | Cost or restriction | Rationale |
|---|---|---|---|---|
| Unicode equality, membership, inequality, and substring search | Normalize | Compare a persisted `UnicodeOrdinalIgnoreCase` search key, not a provider collation or case-folding function. Empty substring matches every non-null string. | An extra bounded projected string column and index; the declared maximum is enforced in UTF-16 code units. | Provider-native `ILIKE`, `LOWER`, collations, and regex folding disagree for Turkish I, sharp S, and composed/decomposed text. |
| Case-insensitive prefix and negative-substring search on a scale-bearing route | Refuse | `StartsWith` and `NotContains` are rejected before provider I/O for this route class. | These shapes are unavailable until every provider can certify the same indexed semantics. | MongoDB cannot certify Groundwork's case-insensitive regex semantics on the declared ordinary B-tree for these operations. |
| Malformed UTF-16 and implicit Unicode normalization | Refuse | Input must be well-formed UTF-16; canonically equivalent strings remain distinct unless the caller explicitly normalizes them. | Callers choose and pay for normalization before persistence. | Silent normalization would change identity and length semantics. |
| Overlength projected strings | Refuse | A value exceeding its declared UTF-16 bound fails validation before a provider write. | Every portable string projection needs a finite declared bound. | Truncation and provider-specific index limits are not portable. |
| Null and missing projected values | Normalize | Missing and explicit null are the same logical null. Equality and `In` may target null; `In []` is false; `In [x, null]` matches either. Negation is the total complement of the positive predicate. | Materialization must preserve the declared `IncludedAsNull` behavior; providers may not expose their native missing/null distinction. | MongoDB `$in: [null]` also matches missing fields, while relational engines have no missing column state. |
| Range comparison with a null operand | Refuse | `<`, `<=`, `>`, and `>=` against null do not compile. Null stored values never satisfy a non-null range. | Callers express null membership separately. | A total ordering for null inside predicates would invent semantics that SQL does not provide. |
| Nullable ordering and paging | Normalize | Ascending and descending order have an explicit null position and a portable ordinal document-ID tie-break; paging is applied after that total order. | Plans require deterministic tie-break columns and explicit null-order rendering. | PostgreSQL's default null position differs from the other engines, and paging without a total order is unstable. |
| Decimal values | Normalize | Decimal predicates use a declared `decimal(18,4)` domain and compare numerically. Excess scale and incompatible mixed physical types fail closed; values are never rounded implicitly. | Precision and scale must be declared and validated before I/O. | SQLite dynamic typing and provider coercion otherwise admit different values or comparisons. |
| Boolean values | Normalize | Boolean/null values are projected into an explicit three-state key before comparison and ordering. Textual boolean constants are parsed case-insensitively at the suite boundary. | One bounded projected key is required. | Provider-native truth-value ordering and coercion are not used as contract semantics. |
| Date/time values | Normalize | Instants are projected as UTC ticks, preserving sub-millisecond precision and comparing numerically from year 1 through year 9999. | Offset identity is discarded; the contract represents instants, not local clock readings. | Provider date ranges, offset handling, and timestamp precision differ. |
| GUID equality, membership, inequality, and order | Normalize | GUIDs use an RFC 4122/network-byte-order hexadecimal key. | One projected key replaces native GUID ordering. | SQL Server `uniqueidentifier` byte ordering differs from lexical/RFC ordering. |
| Binary equality and membership | Normalize | Binary values use an exact base64 equality key; null and empty remain distinct. | One bounded projected key and base64 expansion. | Equality is portable when the representation is exact. |
| Binary range, prefix, negation-by-range, and order | Refuse | Only equality and membership compile for binary data. | No portable binary sorting or prefix ranges. | BSON `BinData` orders by length, subtype, and bytes, which is incompatible with relational byte/text order. |
| Conjunction and disjunction | Normalize | Clauses are ANDed; comparisons within a clause are ORed. Results still use the ordinal ID tie-break when no explicit sort is declared. | Plans must declare every participating predicate path. | This avoids provider optimizer order becoming observable. |

## Standing proof

Docker must be available because PostgreSQL, SQL Server, and MongoDB run in containers; SQLite is
in memory.

```bash
dotnet test tests/Groundwork/Groundwork.Differential.Tests/Groundwork.Differential.Tests.csproj
```

The same project is a named provider-matrix entry in `.github/workflows/ci.yml`. A new provider or
new portable operation is incomplete until it participates in this gate and every new divergence
has a normalize-or-refuse row above.
