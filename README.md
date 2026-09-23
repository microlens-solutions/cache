# `Microlens.Cache`

Stampede-protected, container-scoped in-process caching for .NET, built on `Microsoft.Extensions.Caching.Memory`.

**`Microlens.Cache`** is a high-performance caching library that **deduplicates**, **stores**, **refreshes** and **evicts** cached values through a single, thread-safe `ICachingService`.

Unlike using `IMemoryCache` directly, **`Microlens.Cache`** guarantees that a factory runs **once per key** no matter how many callers miss at the same time, keeps serving the **previous value while a refresh loads**, and enforces **per-container eviction policies** (`LRU` or `LFU`) with their own capacity, expiration and configuration.

[Why `Microlens.Cache`?](#why-microlenscache) | [Quick Start](#quick-start) | [Quick Example](#quick-example) | [Features](#features) | [Configuration](#configuration) | [API Reference](#api-reference) | [Behavior & Guarantees](#behavior--guarantees) | [Performance Characteristics](#performance-characteristics) | [Comparison](#comparison) | [Supported Frameworks](#supported-frameworks) | [When Not To Use `Microlens.Cache`](#when-not-to-use-microlenscache) | [License](#license)

---

## Why `Microlens.Cache`?

Caching looks simple until it runs under concurrency. Most hand-rolled and `IMemoryCache`-based caches eventually hit the same problems:

- **Cache Stampede**: many concurrent misses run the same expensive factory at the same time.
- **Refresh Gaps**: evicting before reloading leaves a window where every caller misses.
- **Unbounded Growth**: entries without expiration or capacity limits grow until memory pressure hits.
- **Stale Writes**: a slow load started before an invalidation overwrites the newer state after it.
- **Mixed Workloads**: one global cache cannot give hot lookups and bulk reference data different policies.

Examples of typical use cases:

- **Database Lookups**: Cache entities by key without flooding the database on cold start.
- **Reference Data**: Load an entire collection (countries, currencies, settings) once and serve lookups from it.
- **External API Responses**: Deduplicate concurrent calls to rate-limited or slow third-party services.
- **Configuration & Feature Data**: Refresh periodically while callers keep reading the current value.
- **Hot-Key Workloads**: Keep the most frequently used entries with `LFU` eviction under a fixed capacity.

---

## Quick Start

### Installation

```
dotnet add package Microlens.Cache
```

### Register Services

```csharp
builder.Services.AddMicrolensCache();
```

### Inject and Use

```csharp
public sealed class ProductService(ICachingService cache, IProductRepository repository) {
    public Task<Product?> GetAsync(string id, CancellationToken cancellationToken) {
        return cache.GetOrAddAsync("products", id, () => repository.FindAsync(id), cancellationToken: cancellationToken);
    }
}
```

That's it.

Concurrent misses for the same key share a single factory execution.

---

## Quick Example

Given `1,000` concurrent requests for the same product on a cold cache:

```csharp
var product = await cache.GetOrAddAsync(
    "products",
    "sku-42",
    () => repository.FindAsync("sku-42"),
    CacheExpiration.AfterWrite(TimeSpan.FromMinutes(5)));
```

**`Microlens.Cache`** executes the factory **once**, and all `1,000` callers receive the same result.

```
Request 1    ─┐
Request 2    ─┤
Request 3    ─┼──► factory() ──► Product sku-42 ──► all callers
...          ─┤
Request 1000 ─┘
```

- No locks to write.
- No `Lazy<T>` wrappers to manage.
- No duplicate database round-trips.
- No partial state on failure.
- No stale writes after invalidation.

---

## Features

**`Microlens.Cache`** is **NOT** a distributed cache.
It is an in-process cache that focuses on **correctness under concurrency**, **predictable eviction** and **low overhead** on the read path.

### Stampede Protection

Every miss is coordinated through a single in-flight entry per key.

- The first caller runs the factory.
- Every concurrent caller awaits the same result.
- A failed or cancelled factory is **not** cached; all waiters receive the same exception, and the next call retries.
- `null` results are cached like any other value.

### Containers

Entries are grouped into named **containers**, each backed by its own `MemoryCache` with its own eviction policy, capacity and default expiration.

```csharp
await cache.GetOrAddAsync("users", userId, () => LoadUserAsync(userId));
await cache.GetOrAddAsync("settings", "theme", () => LoadThemeAsync());
```

### Collections

Load an entire dictionary once and serve key lookups from it.

```csharp
CacheResult<Country> result = await cache.GetOrAddAsync<string, Country>("catalog", "countries", "PK", LoadCountriesAsync);

if (result.Found) {
    Console.WriteLine(result.Value);
}
```

- `CacheResult<TValue>.Found` distinguishes an absent key from a key holding `default`.
- An absent key reloads the collection **at most once** per `AbsentKeyRebuildInterval` (default `30` seconds), so lookups of keys that do not exist can never flood the source.
- Ownership of the returned dictionary transfers to the cache; a `FrozenDictionary` gives the fastest lookups on `.NET 8+`.

### Stale-While-Revalidate Refresh

Force a fresh value without creating a miss window.

```csharp
await cache.GetOrAddAsync("settings", "theme", () => LoadThemeAsync(), refresh: true);
```

- Concurrent readers keep receiving the **previous value** until the new one completes.
- Concurrent refreshes of the same key coalesce into **one** factory execution.
- A failed refresh leaves the previous value in place.

### Expiration

```csharp
CacheExpiration.AfterWrite(TimeSpan.FromMinutes(10));                           // absolute
CacheExpiration.AfterAccess(TimeSpan.FromMinutes(2));                           // sliding
new CacheExpiration(TimeSpan.FromHours(1), TimeSpan.FromMinutes(5));            // both
```

- Lifetime starts when the value is **stored**, never while its factory is still running.
- A per-call expiration overrides the container default, which overrides the global default.

### Eviction Policies

| Policy | Implementation | Behavior at capacity |
| :---: | :--- | :--- |
| `None` | Expiration only | Unbounded |
| `Lru` | Native `MemoryCache` `SizeLimit` compaction | New entries are rejected until background compaction frees space |
| `Lfu` | Hand-rolled frequency index with aging, on top of `MemoryCache` | Least frequently used entries are evicted synchronously, oldest first on ties |

- Every scalar value or collection counts as **one** entry toward `Capacity`.
- Each pass evicts down to `Capacity − Capacity × CompactionPercentage`.
- `LFU` frequencies are halved on every pass, so formerly hot entries cannot pin the container forever.
- A refreshed `LFU` key keeps its frequency.

### Explicit Writes & Invalidation

```csharp
cache.Set("settings", "theme", theme, CacheExpiration.AfterWrite(TimeSpan.FromHours(1)));
cache.SetCollection("catalog", "countries", countries);

cache.Remove("settings", "theme");
cache.RemoveCollection("catalog", "countries");
cache.Clear("settings");
```

- `Set`, `Remove` and `Clear` **supersede** any load already in flight; data fetched before them is never written back.
- `TryGet` reads completed values only and never triggers a load.

### Cancellation

```csharp
await cache.GetOrAddAsync("products", id, () => LoadAsync(id), cancellationToken: cancellationToken);
```

A `CancellationToken` cancels **the caller's wait only**. The shared factory runs to completion and still populates the cache for every other caller.

### Safety Checks

Misuse fails loudly at the call site instead of hanging or corrupting state:

- Reading a key as a different type than it was stored with throws `InvalidOperationException`.
- A factory that awaits or refreshes its **own** in-flight key throws `InvalidOperationException` instead of deadlocking.
- `null` arguments throw `ArgumentNullException` synchronously.
- Invalid configuration throws `InvalidOperationException` when the service is constructed.

---

## Configuration

Configure a global default and per-container overrides through `CacheOptions`.

```csharp
builder.Services.AddMicrolensCache(options => {
    options.Defaults.Expiration = CacheExpiration.AfterWrite(TimeSpan.FromMinutes(30));

    var users = options.Container("users");
    users.Eviction = Registry.EvictionPolicy.Lfu;
    users.Capacity = 10_000;

    var sessions = options.Container("sessions");
    sessions.Eviction = Registry.EvictionPolicy.Lru;
    sessions.Capacity = 50_000;
    sessions.Expiration = CacheExpiration.AfterAccess(TimeSpan.FromMinutes(20));
});
```

### `ContainerOptions`

| Option | Default | Inherits from `Defaults` | Description |
| :--- | :--- | :---: | :---- |
| `Eviction` | `None` | No | `None`, `Lru` or `Lfu`. |
| `Capacity` | `0` | No | Maximum entries; required (`> 0`) when `Eviction` is `Lru` or `Lfu`. |
| `CompactionPercentage` | `0.05` | No | Fraction of `Capacity` removed per eviction pass; must be in `(0, 1)`. |
| `Expiration` | No expiration | Yes | Default expiration when a call passes none. |
| `ExpirationScanFrequency` | `MemoryCache` default | Yes | Interval of the background sweep for expired entries. |
| `AbsentKeyRebuildInterval` | `30` seconds | Yes | Minimum age of a collection before an absent key may reload it. |

- Containers without their own entry use `Defaults`.
- Container, key and collection names are **case-sensitive** (ordinal).
- Options are copied when a container is first used; later changes do not affect live containers.
- `AddMicrolensCache` can be called multiple times; configuration actions compose and the service is registered once.

---

## API Reference

All members are exposed through `ICachingService`, registered as a singleton.

| Member | Description |
| :--- | :--- |
| `GetOrAddAsync<TValue>(container, key, factory, expiration?, refresh?, cancellationToken?)` | Returns the cached value or runs the factory once for all concurrent callers. |
| `GetOrAddAsync<TKey, TValue>(container, collection, key, factory, expiration?, refresh?, cancellationToken?)` | Looks up a key in a collection loaded once by the factory. |
| `TryGet<TValue>(container, key, out value)` | Reads a completed value without loading. |
| `TryGet<TKey, TValue>(container, collection, key, out value)` | Reads a key from a loaded collection without loading. |
| `Set<TValue>(container, key, value, expiration?)` | Stores a value, superseding any load in flight. |
| `SetCollection<TKey, TValue>(container, collection, items, expiration?)` | Stores a collection, superseding any load in flight. |
| `Remove(container, key)` | Removes a value and cancels any load in flight. |
| `RemoveCollection(container, collection)` | Removes a collection and cancels any load in flight. |
| `Clear(container)` | Atomically replaces the container with an empty one. |

---

## Behavior & Guarantees

| Scenario | Behavior |
| :--- | :--- |
| Concurrent misses for one key | Factory runs once; all callers share the result. |
| Factory throws or is cancelled | Nothing is cached; current waiters share the failure; next call retries. |
| Factory returns `null` | `null` is cached. |
| `refresh: true` while a value is cached | Readers get the previous value until the new one completes. |
| Refresh fails | Previous value stays cached. |
| `Set` / `Remove` / `Clear` during a load | The in-flight result is returned to its callers but never stored. |
| Caller's token is cancelled | Only that caller stops waiting; the factory continues for everyone else. |
| Key read as a different type | `InvalidOperationException`. |
| Factory awaits its own key | `InvalidOperationException` (no deadlock). |
| `LRU` container at capacity | New entries are not cached until native compaction frees space. |
| Service disposed | Every call throws `ObjectDisposedException`. |

---

## Performance Characteristics

**`Microlens.Cache`** is designed for hot read paths and high-concurrency workloads.

Key implementation details:

- Lock-free cache hits through a single `MemoryCache.TryGetValue`
- Allocation-free scalar hits: the stored completed `Task<TValue>` is returned as-is
- Atomic single-flight coordination through `ConcurrentDictionary` add, swap and compare-remove
- Striped per-key locks guarding writes, invalidations and evictions only, never reads
- `System.Threading.Lock` on `.NET 9+` targets
- Continuations run asynchronously, so completing a load never executes waiter code on the factory's thread
- `ConfigureAwait(false)` throughout, safe for synchronous callers on `.NET Framework`
- Interned collection keys and cached delegates, so steady-state lookups allocate no keys or closures
- `LFU` frequency tracking without interlocked operations on the hit path

The library is optimized for **throughput** and **predictable latency** while preserving strict **correctness** under concurrency.

---

## Comparison

| Capability | `Microlens.Cache` | `IMemoryCache` |
| :--- | :---: | :---: |
| In-process key/value caching | Yes | Yes |
| Absolute and sliding expiration | Yes | Yes |
| Single factory execution per key | Yes | No |
| Stale-while-revalidate refresh | Yes | No |
| Invalidation that cancels in-flight loads | Yes | No |
| Collection loading with throttled rebuilds | Yes | No |
| Named containers with independent policies | Yes | No |
| `LRU` eviction | Yes | Yes |
| `LFU` eviction | Yes | No |
| Type-safe reads with loud mismatch failures | Yes | No |
| Re-entrant factory deadlock detection | Yes | No |

**`Microlens.Cache`** builds on `IMemoryCache` for storage and expiration, and adds the **coordination**, **refresh** and **eviction** semantics it lacks.

---

## Supported Frameworks

| Target Framework | Supported |
| :--- | :---: |
| `.NET 10` | ✓ |
| `.NET 8` | ✓ |
| `.NET Standard 2.0` | ✓ |
| `.NET Framework 4.8` | ✓ |
| `.NET Framework 4.7.2` | ✓ |

Dependencies:

- `Microsoft.Extensions.Caching.Memory`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Options`

---

## When Not To Use `Microlens.Cache`

`Microlens.Cache` is not intended for:

- Distributed or shared caching across multiple processes or servers
- Persisting cached data across application restarts
- Replacing `Redis`, `IDistributedCache` or `HybridCache` in multi-instance deployments
- Caching values that must be evicted by exact memory size in bytes

If your data must be shared between instances or survive restarts, use a distributed cache.

---

## License

Licensed under the **Apache License 2.0**.

---
