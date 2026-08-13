# Identity generators

Document ids in Groundwork are caller-supplied — `IDocumentStore.SaveAsync` receives the id from
you. When *your* application needs to mint those ids (or any other identifier that lands in an
indexed or primary-key column), the id format matters: a random GUID is not sortable and fragments
the B-tree it lives in. `Groundwork.Core` ships a small reusable catalog of generators under the
`Groundwork.Core.Identity` namespace.

The abstraction is a single method — no DI container, no `services.Add…` registration; construct
one and pass it where you need it:

```csharp
public interface IIdentityGenerator
{
    string Generate();
}
```

## The catalog

The time-ordered generators take a `TimeProvider` for their time source (so tests can drive them
deterministically); `GuidIdentityGenerator` takes no parameters. All except `Guid` are sortable by
ordinal string comparison.

| Generator | Output | Length | Sortable | Coordination | When to use |
| --- | --- | --- | --- | --- | --- |
| `ShortIdentityGenerator` | Base62 | 11 chars | yes (to the ms) | none | Default choice. Compact, time-ordered, no setup. |
| `UuidV7IdentityGenerator` | lowercase hex (`"N"`) | 32 chars | yes | none | Full UUID width with chronological ordering. |
| `SnowflakeIdentityGenerator` | Base62 | 11 chars | yes (strictly increasing per worker) | a unique worker id per instance | Strict monotonic ordering / explicit worker partitioning. |
| `GuidIdentityGenerator` | lowercase hex (`"N"`) | 32 chars | **no** | none | Parity / callers that don't need ordering. Not recommended for indexed keys. |

### `ShortIdentityGenerator`

A 64-bit value: a 42-bit millisecond timestamp (relative to the epoch `2020-01-01T00:00:00Z`,
valid until ~2159) in the high bits and 22 random bits in the low bits, Base62-encoded to 11
characters. Sortable to the millisecond, no coordination required. Under extreme per-millisecond
throughput the 22 random bits carry a small collision probability; use the Snowflake generator if
you need a hard guarantee.

### `UuidV7IdentityGenerator`

`Guid.CreateVersion7(...)` rendered as 32 lowercase hex chars. 128 bits, effectively
collision-free, and sortable by its canonical string because the high bits are a millisecond
timestamp.

### `SnowflakeIdentityGenerator`

A short 64-bit Snowflake, Base62-encoded to 11 chars. Layout (high → low): 41-bit ms timestamp
(from a configurable epoch, default `2020-01-01Z`) | 10-bit worker id (0–1023) | 12-bit sequence.

Within the same millisecond it increments the sequence; on sequence overflow it spins to the next
millisecond; if the clock moves backwards it throws `InvalidOperationException`. Ids are strictly
increasing per worker, and distinct workers never collide. **Create one instance per worker and
reuse it** — the instance *is* the coordination point.

```csharp
var generator = new SnowflakeIdentityGenerator(
    TimeProvider.System,
    new SnowflakeIdentityGeneratorOptions { WorkerId = 1 }); // 0–1023, must be unique per instance
```

`WorkerId` outside `[0, 1023]` throws `ArgumentOutOfRangeException`.

### Base62 encoding

`ShortIdentityGenerator` and `SnowflakeIdentityGenerator` share one Base62 encoder: alphabet
`0-9 A-Z a-z` (ascending ASCII), fixed 11-char width. Fixed width plus an ascending alphabet means
ordinal string order equals numeric order; 11 chars is the smallest width that holds the full
`ulong` range.

### Convenience factory

```csharp
var gen = GroundworkIdentityGenerators.Create(IdentityGeneratorKind.Short, TimeProvider.System);
```

`IdentityGeneratorKind.Snowflake` requires `SnowflakeIdentityGeneratorOptions`.

## Format compatibility with Elsa

This catalog deliberately mirrors Elsa's `Elsa.Primitives.Identity` catalog — same Base62 alphabet
and the same bit layouts — so identifiers produced by either repository are format-compatible. The
compatibility is pinned by golden-value tests with identical literals in both repositories.

## See also

- [[Getting-Started]] — where document ids enter `SaveAsync`.
- [Identity generators](https://github.com/valence-works/Groundwork/blob/main/docs/identity-generators.md)
  — the source design note.
