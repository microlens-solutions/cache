using System.Collections.Concurrent;
using System.Diagnostics;
using Microlens.Cache.Options;
using Microlens.Cache.Shared;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

#if NET9_0_OR_GREATER
using StripeLock = System.Threading.Lock;
#else
using StripeLock = object;
#endif

namespace Microlens.Cache.Services;

internal sealed class CachingService : ICachingService, IDisposable {
    // Minimum age (30 seconds, in Stopwatch ticks) a collection generation must reach before a lookup of an absent key may rebuild it.
    private static readonly long AbsentKeyRebuildInterval = 30 * Stopwatch.Frequency;

    // Absolute lifetimes beyond this are stored as "never", since MemoryCache adds them to UtcNow and would overflow DateTime.
    private static readonly long MaxRelativeLifetimeTicks = TimeSpan.FromDays(365 * 1000).Ticks;

    // Entries whose factory is executing in the current logical call chain; flows across awaits to detect re-entrant factories.
    private static readonly AsyncLocal<Frame?> Populating = new();

    // Power of two so a key's stripe is a mask of its hash; locks guard store writes and evictions only, never reads.
    private const int StripeCount = 128;

    private readonly CachingOptions _options;

    // Containers owned by this instance; Ordinal to match MemoryCache key equality.
    private readonly ConcurrentDictionary<string, Container> _containers = new(StringComparer.Ordinal);

    // Cached factory so GetOrAdd never allocates a closure per call.
    private readonly Func<string, Container> _createContainer;

    // Serializes check-and-write per key in the store, since IMemoryCache has no compare-and-swap.
    private readonly StripeLock[] _stripes = CreateStripes();

    private int _disposed;

    public CachingService(IOptions<CachingOptions> options) {
        Guard.NotNull(options);

        // Fail at construction on invalid configuration rather than on the first call into a misconfigured container.
        _options = options.Value;
        _options.Validate();
        _createContainer = name => new Container(_options.Resolve(name));
    }

    // Interface
    public Task<TValue> GetOrAddAsync<TValue>(string container, string key, Func<Task<TValue>> factory, CacheExpiration expiration = default, bool refresh = false, CancellationToken cancellationToken = default) {
        // 1. Validate synchronously so misuse fails at the call site, not inside a faulted task.
        Guard.NotNull(container);
        Guard.NotNull(key);
        Guard.NotNull(factory);
        ThrowIfDisposed();

        // 2. An already-cancelled caller never publishes an entry or starts a factory.
        if (cancellationToken.IsCancellationRequested) {
            return Task.FromCanceled<TValue>(cancellationToken);
        }

        // 3. Refresh only accepts a generation created at or after this call; otherwise any generation qualifies.
        long freshAfter = refresh ? Stopwatch.GetTimestamp() : long.MinValue;

        // 4. Hit returns the stored task as-is (allocation-free); miss returns the task of the single in-flight factory.
        return WithCancellation(Acquire(GetContainer(container), key, factory, expiration, freshAfter).Completion.Task, cancellationToken);
    }

    // Interface
    public Task<CacheResult<TValue>> GetOrAddAsync<TKey, TValue>(string container, string collection, TKey key, Func<Task<IReadOnlyDictionary<TKey, TValue>>> factory, CacheExpiration expiration = default, bool refresh = false, CancellationToken cancellationToken = default) where TKey : notnull {
        // 1. Validate synchronously so misuse fails at the call site, not inside a faulted task.
        Guard.NotNull(container);
        Guard.NotNull(collection);
        Guard.NotNull(key);
        Guard.NotNull(factory);
        ThrowIfDisposed();

        // 2. An already-cancelled caller never publishes an entry or starts a factory.
        if (cancellationToken.IsCancellationRequested) {
            return Task.FromCanceled<CacheResult<TValue>>(cancellationToken);
        }

        // 3. Collections are keyed by an interned CollectionKey, so they never collide with scalar string keys in the same store.
        var owner = GetContainer(container);
        return GetFromCollectionAsync(owner, owner.GetCollectionKey(collection), key, factory, expiration, refresh, cancellationToken);
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        foreach (var pair in _containers) {
            pair.Value.Dispose();
        }
    }

    private async Task<CacheResult<TValue>> GetFromCollectionAsync<TKey, TValue>(Container container, CollectionKey collection, TKey key, Func<Task<IReadOnlyDictionary<TKey, TValue>>> factory, CacheExpiration expiration, bool refresh, CancellationToken cancellationToken) where TKey : notnull {
        long requestedAt = Stopwatch.GetTimestamp();

        // 1. Resolve the stored or in-flight generation or, when refreshing, one created at or after this call.
        var generation = Acquire(container, collection, factory, expiration, refresh ? requestedAt : long.MinValue);
        var items = await WithCancellation(generation.Completion.Task, cancellationToken).ConfigureAwait(false);

        if (items is not null && items.TryGetValue(key, out var value)) {
            return new CacheResult<TValue>(value);
        }

        // 2. Absent key (or null collection): a generation created after this call (always the case on refresh), or younger than the interval, is authoritative.
        if (requestedAt - generation.CreatedAt < AbsentKeyRebuildInterval) {
            return default;
        }

        // 3. Stale generation: rebuild once. Concurrent misses and refreshes coalesce into the first generation newer than this one.
        var rebuilt = Acquire(container, collection, factory, expiration, generation.CreatedAt + 1);
        items = await WithCancellation(rebuilt.Completion.Task, cancellationToken).ConfigureAwait(false);

        return items is not null && items.TryGetValue(key, out value) ? new CacheResult<TValue>(value) : default;
    }

    private Entry<TValue> Acquire<TValue>(Container container, object key, Func<Task<TValue>> factory, CacheExpiration expiration, long freshAfter) {
        Entry<TValue>? candidate = null;

        while (true) {
            // 1. Completed generation: lock-free MemoryCache read, which rejects expired entries and records the access for LRU and sliding expiration.
            if (container.Store.TryGetValue(key, out var found)) {
                var stored = Cast<TValue>(found!, key);

                if (stored.CreatedAt >= freshAfter) {
                    container.Lfu?.Touch(stored);
                    return stored;
                }
            }

            // 2. In-flight generation: joined when fresh enough, otherwise atomically superseded; the stored generation stays servable meanwhile.
            if (container.Pending.TryGetValue(key, out var pending)) {
                var inFlight = Cast<TValue>(pending, key);

                // Waiting on (or superseding) an entry this call chain is populating would never complete: fail loudly.
                if (IsPopulating(inFlight)) {
                    throw new InvalidOperationException($"Key '{key}' is being populated by the current call chain; its factory cannot await or refresh it.");
                }

                if (inFlight.CreatedAt >= freshAfter) {
                    return inFlight;
                }

                candidate ??= new Entry<TValue>(expiration);

                if (!container.Pending.TryUpdate(key, candidate, pending)) {
                    continue;
                }
            }
            else {
                candidate ??= new Entry<TValue>(expiration);

                if (!container.Pending.TryAdd(key, candidate)) {
                    continue;
                }

                // 3. A generation may have been stored (and left Pending) between steps 1 and 2: adopt its value instead of running the factory again.
                if (container.Store.TryGetValue(key, out found) && Cast<TValue>(found!, key) is var latest && latest.CreatedAt >= freshAfter) {
                    _ = candidate.Completion.TrySetResult(latest.Completion.Task.Result);
                    RemovePending(container, key, candidate);
                    return latest;
                }
            }

            // 4. Only the caller that published the in-flight entry runs the factory; the task never faults (outcome lives in the entry).
            _ = PopulateAsync(container, key, candidate, factory);
            return candidate;
        }
    }

    private async Task PopulateAsync<TValue>(Container container, object key, Entry<TValue> entry, Func<Task<TValue>> factory) {
        TValue value;

        // 1. Scope this entry to the factory's logical call chain; the async method restores the caller's context on return.
        Populating.Value = new Frame(entry, Populating.Value);

        try {
            value = await factory().ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) {
            // 2. Leave Pending before completing so the next caller retries; the stored generation (if any) is untouched.
            RemovePending(container, key, entry);
            _ = entry.Completion.TrySetCanceled(exception.CancellationToken);
            return;
        }
        catch (Exception exception) {
            // 3. Leave Pending before completing so the next caller retries, while current waiters share this failure.
            RemovePending(container, key, entry);
            _ = entry.Completion.TrySetException(exception);
            return;
        }

        // 4. Complete first (stored entries are always completed), store next, leave Pending last: this generation is always reachable in at least one place.
        _ = entry.Completion.TrySetResult(value);
        Commit(container, key, entry);
        RemovePending(container, key, entry);
    }

    private void Commit(Container container, object key, Entry entry) {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        try {
            lock (Stripe(key)) {
                var current = container.Store.TryGetValue(key, out var found) ? (Entry)found! : null;

                // 1. A superseding generation (refresh) may have been stored first: never overwrite newer data with older.
                if (current is not null && current.CreatedAt > entry.CreatedAt) {
                    return;
                }

                WriteEntry(container, key, entry, current);
            }

            // 2. LFU evicts synchronously on the completing thread, outside the stripe; LRU compacts natively inside MemoryCache.
            if (container.Lfu is not null) {
                Compact(container, entry);
            }
        }
        catch (ObjectDisposedException) {
            // Disposal raced with a background completion: waiters already hold the value and there is no store left to write to.
        }
    }

    // Caller holds the key's stripe.
    private static void WriteEntry(Container container, object key, Entry entry, Entry? previous) {
        var lfu = container.Lfu;

        // 1. LFU: a refreshed key keeps its popularity. Tracked before the write, so an entry MemoryCache rejects as already expired is untracked by its own callback.
        if (lfu is not null) {
            entry.Frequency = previous?.Frequency ?? 0;
            lfu.Track(key, entry);
        }

        using var cacheEntry = container.Store.CreateEntry(key);
        var expiration = entry.Expiration;

        cacheEntry.AbsoluteExpirationRelativeToNow = expiration.Absolute is { } absolute && absolute.Ticks < MaxRelativeLifetimeTicks ? absolute : null;
        cacheEntry.SlidingExpiration = expiration.Sliding;

        // 2. LRU: MemoryCache's SizeLimit requires a size on every entry; each entry counts as one toward Capacity.
        if (container.Sized) {
            cacheEntry.Size = 1;
        }

        // 3. LFU: expirations and removals unregister the entry from the index.
        if (lfu is not null) {
            _ = cacheEntry.RegisterPostEvictionCallback(LfuIndex.OnEvicted, lfu);
        }

        // 4. Committed to the store when the entry is disposed.
        cacheEntry.Value = entry;
    }

    private void Compact(Container container, Entry committed) {
        var lfu = container.Lfu!;

        // 1. One compaction per container at a time; concurrent commits overshoot Capacity briefly instead of queuing.
        if (!lfu.IsOverCapacity || !lfu.TryBeginCompaction()) {
            return;
        }

        try {
            long target = lfu.EvictionTarget;

            if (target <= 0) {
                return;
            }

            // 2. Lowest frequency first, oldest generation on ties; the entry that triggered the pass is excluded so a fresh load survives it.
            var victims = lfu.Snapshot(committed);
            victims.Sort(Victim.Order);

            long evicted = 0;

            for (int i = 0; i < victims.Count && evicted < target; i++) {
                var victim = victims[i];

                // 3. Compare-and-act under the stripe: skip victims that were refreshed, expired or removed since the snapshot.
                lock (Stripe(victim.Key)) {
                    if (lfu.Untrack(victim.Key, victim.Entry)) {
                        container.Store.Remove(victim.Key);
                        evicted++;
                    }
                }
            }

            // 4. Aging: halve survivors' frequencies so formerly hot entries cannot pin the container forever.
            lfu.Age();
        }
        finally {
            lfu.EndCompaction();
        }
    }

    private static void RemovePending(Container container, object key, Entry entry) {
        // Compare-and-remove: never removes a newer in-flight generation that superseded this one.
        _ = ((ICollection<KeyValuePair<object, Entry>>)container.Pending).Remove(new KeyValuePair<object, Entry>(key, entry));
    }

    private static bool IsPopulating(Entry entry) {
        for (var frame = Populating.Value; frame is not null; frame = frame.Parent) {
            if (ReferenceEquals(frame.Current, entry)) {
                return true;
            }
        }

        return false;
    }

    private static Task<TValue> WithCancellation<TValue>(Task<TValue> task, CancellationToken cancellationToken) {
        // Completed tasks and non-cancellable tokens pass through without allocation.
        if (task.IsCompleted || !cancellationToken.CanBeCanceled) {
            return task;
        }

#if NET6_0_OR_GREATER
        return task.WaitAsync(cancellationToken);
#else
        return WaitAsync(task, cancellationToken);
#endif
    }

#if !NET6_0_OR_GREATER
    private static async Task<TValue> WaitAsync<TValue>(Task<TValue> task, CancellationToken cancellationToken) {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled)) {
            if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task) {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        return await task.ConfigureAwait(false);
    }
#endif

    private static Entry<TValue> Cast<TValue>(object entry, object key) {
        if (entry is Entry<TValue> typed) {
            return typed;
        }

        // Loud failure instead of an InvalidCastException deep inside the caller.
        throw new InvalidOperationException($"Key '{key}' is cached as '{((Entry)entry).ValueType}' and cannot be read as '{typeof(TValue)}'.");
    }

    private StripeLock Stripe(object key) => _stripes[key.GetHashCode() & (StripeCount - 1)];

    private static StripeLock[] CreateStripes() {
        var stripes = new StripeLock[StripeCount];

        for (int i = 0; i < stripes.Length; i++) {
            stripes[i] = new StripeLock();
        }

        return stripes;
    }

    private Container GetContainer(string container) => _containers.GetOrAdd(container, _createContainer);

    private void ThrowIfDisposed() {
#if NET7_0_OR_GREATER
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
#else
        if (Volatile.Read(ref _disposed) != 0) {
            throw new ObjectDisposedException(nameof(CachingService));
        }
#endif
    }

    private sealed class Container : IDisposable {
        // Completed generations only, for both keyspaces: scalar values keyed by their string, collections by their interned CollectionKey.
        internal readonly MemoryCache Store;

        // In-flight generations only; ConcurrentDictionary gives the atomic add/swap/remove that single-flight needs and MemoryCache lacks.
        internal readonly ConcurrentDictionary<object, Entry> Pending = new();

        // LFU bookkeeping; null for None and LRU containers.
        internal readonly LfuIndex? Lfu;

        // LRU: MemoryCache runs with SizeLimit, so every entry must declare a size.
        internal readonly bool Sized;

        // Interns one CollectionKey per collection name so collection lookups allocate nothing after first use.
        private readonly ConcurrentDictionary<string, CollectionKey> _collectionKeys = new(StringComparer.Ordinal);

        internal Container(ContainerOptions options) {
            // Options are copied here, so later mutation of the options instance never affects a live container.
            var storeOptions = new MemoryCacheOptions();

            switch (options.Eviction) {
                case Registry.EvictionPolicy.Lru:
                    storeOptions.SizeLimit = options.Capacity;
                    storeOptions.CompactionPercentage = options.CompactionPercentage;
                    Sized = true;
                    break;

                case Registry.EvictionPolicy.Lfu:
                    Lfu = new LfuIndex(options.Capacity, options.CompactionPercentage);
                    break;
            }

            Store = new MemoryCache(storeOptions);
        }

        internal CollectionKey GetCollectionKey(string collection) => _collectionKeys.GetOrAdd(collection, static name => new CollectionKey(name));

        public void Dispose() => Store.Dispose();
    }

    private sealed class LfuIndex(long capacity, double compactionPercentage) {
        // Unregisters entries MemoryCache removed on its own (expiration); compare-based, so replaced or already-evicted entries are no-ops.
        internal static readonly PostEvictionDelegate OnEvicted = static (key, value, _, state) => ((LfuIndex)state!).Untrack(key, (Entry)value!);

        // Stored generations by key; mirrors the store for LFU containers (writes and evictions happen under the key's stripe).
        private readonly ConcurrentDictionary<object, Entry> _members = new();

        private readonly long _capacity = capacity;

        // Compaction evicts down to this, matching MemoryCache's own low-watermark formula.
        private readonly long _lowWatermark = capacity - (long)(capacity * compactionPercentage);

        // Tracked separately because ConcurrentDictionary.Count takes every internal lock.
        private long _count;

        private int _compacting;

        internal bool IsOverCapacity => Volatile.Read(ref _count) > _capacity;

        internal long EvictionTarget => Volatile.Read(ref _count) - _lowWatermark;

        // Lossy by design: unsynchronized increments keep the hit path free of interlocked traffic on hot entries.
        internal void Touch(Entry entry) {
            if (entry.Frequency < int.MaxValue) {
                entry.Frequency++;
            }
        }

        internal void Track(object key, Entry entry) {
            while (true) {
                if (_members.TryGetValue(key, out var prior)) {
                    if (_members.TryUpdate(key, entry, prior)) {
                        return;
                    }
                }
                else if (_members.TryAdd(key, entry)) {
                    _ = Interlocked.Increment(ref _count);
                    return;
                }
            }
        }

        internal bool Untrack(object key, Entry entry) {
            if (!((ICollection<KeyValuePair<object, Entry>>)_members).Remove(new KeyValuePair<object, Entry>(key, entry))) {
                return false;
            }

            _ = Interlocked.Decrement(ref _count);
            return true;
        }

        internal bool TryBeginCompaction() => Interlocked.CompareExchange(ref _compacting, 1, 0) == 0;

        internal void EndCompaction() => Volatile.Write(ref _compacting, 0);

        internal List<Victim> Snapshot(Entry excluded) {
            var victims = new List<Victim>((int)Math.Min(Volatile.Read(ref _count), int.MaxValue));

            foreach (var pair in _members) {
                if (!ReferenceEquals(pair.Value, excluded)) {
                    victims.Add(new Victim(pair.Key, pair.Value));
                }
            }

            return victims;
        }

        internal void Age() {
            foreach (var pair in _members) {
                pair.Value.Frequency >>= 1;
            }
        }
    }

    // Frequency and creation stamp are captured once, so the sort stays consistent while hits keep mutating live entries.
    private readonly struct Victim(object key, Entry entry) {
        internal static readonly Comparison<Victim> Order = static (x, y) => x.Frequency != y.Frequency ? x.Frequency.CompareTo(y.Frequency) : x.CreatedAt.CompareTo(y.CreatedAt);

        internal readonly object Key = key;
        internal readonly Entry Entry = entry;
        internal readonly int Frequency = entry.Frequency;
        internal readonly long CreatedAt = entry.CreatedAt;
    }

    // Reference-equality key: never equal to a string, so a collection and a scalar key with the same name cannot collide.
    private sealed class CollectionKey(string name) {
        public override string ToString() => name;
    }

    private abstract class Entry(CacheExpiration expiration) {
        // Monotonic creation stamp used to order generations for refresh, absent-key, commit and LFU tie-break decisions.
        internal readonly long CreatedAt = Stopwatch.GetTimestamp();

        // Policy supplied by the caller that published this generation.
        internal readonly CacheExpiration Expiration = expiration;

        // LFU access count; lossy, carried across refreshes, halved on every compaction pass.
        internal int Frequency;

        internal abstract Type ValueType { get; }
    }

    private sealed class Entry<TValue>(CacheExpiration expiration) : Entry(expiration) {
        // Continuations run asynchronously so completing the entry never executes waiter code on the factory's thread.
        internal readonly TaskCompletionSource<TValue> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal override Type ValueType => typeof(TValue);
    }

    private sealed class Frame(Entry current, Frame? parent) {
        internal readonly Entry Current = current;
        internal readonly Frame? Parent = parent;
    }
}
