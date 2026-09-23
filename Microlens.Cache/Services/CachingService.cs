using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;

#if NET9_0_OR_GREATER
using StripeLock = System.Threading.Lock;
#else
using StripeLock = object;
#endif

namespace Microlens.Cache.Services;

internal sealed class CachingService : ICachingService, IDisposable {
    // Minimum age (30 seconds, in Stopwatch ticks) a collection generation must reach before a lookup of an absent key may rebuild it.
    private static readonly long AbsentKeyRebuildInterval = 30 * Stopwatch.Frequency;

    // Entries whose factory is executing in the current logical call chain; flows across awaits to detect re-entrant factories.
    private static readonly AsyncLocal<Frame?> Populating = new();

    // Power of two so a key's stripe is a mask of its hash; locks are only taken on publish/unpublish, never on hits.
    private const int StripeCount = 128;

    // Containers owned by this instance, each with its own MemoryCache; Ordinal to match MemoryCache key equality.
    private readonly ConcurrentDictionary<string, Container> _containers = new(StringComparer.Ordinal);

    // Serializes check-and-publish per key, since IMemoryCache has no atomic add-if-absent or compare-and-swap.
    private readonly StripeLock[] _stripes = CreateStripes();

    private int _disposed;

    // Interface
    public Task<TValue> GetOrAddAsync<TValue>(string container, string key, Func<Task<TValue>> factory, bool refresh = false, CancellationToken cancellationToken = default) {
        // 1. Validate synchronously so misuse fails at the call site, not inside a faulted task.
        Guard.NotNull(container);
        Guard.NotNull(key);
        Guard.NotNull(factory);
        ThrowIfDisposed();

        // 2. An already-cancelled caller never publishes an entry or starts a factory.
        if (cancellationToken.IsCancellationRequested) {
            return Task.FromCanceled<TValue>(cancellationToken);
        }

        // 3. Refresh only accepts an entry created at or after this call; otherwise any entry qualifies.
        long freshAfter = refresh ? Stopwatch.GetTimestamp() : long.MinValue;

        // 4. Hit returns the stored task as-is (allocation-free); miss returns the task of the single in-flight factory.
        return WithCancellation(Acquire(GetContainer(container).Store, key, factory, freshAfter).Completion.Task, cancellationToken);
    }

    // Interface
    public Task<CacheResult<TValue>> GetOrAddAsync<TKey, TValue>(string container, string collection, TKey key, Func<Task<IReadOnlyDictionary<TKey, TValue>>> factory, bool refresh = false, CancellationToken cancellationToken = default) where TKey : notnull {
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
        return GetFromCollectionAsync(owner.Store, owner.GetCollectionKey(collection), key, factory, refresh, cancellationToken);
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        foreach (var pair in _containers) {
            pair.Value.Dispose();
        }
    }

    private async Task<CacheResult<TValue>> GetFromCollectionAsync<TKey, TValue>(MemoryCache store, CollectionKey collection, TKey key, Func<Task<IReadOnlyDictionary<TKey, TValue>>> factory, bool refresh, CancellationToken cancellationToken) where TKey : notnull {
        long requestedAt = Stopwatch.GetTimestamp();

        // 1. Resolve the current generation (or the previous one while a rebuild loads) or, when refreshing, one created at or after this call.
        var generation = Acquire(store, collection, factory, refresh ? requestedAt : long.MinValue);
        var items = await WithCancellation(generation.Completion.Task, cancellationToken).ConfigureAwait(false);

        if (items is not null && items.TryGetValue(key, out var value)) {
            return new CacheResult<TValue>(value);
        }

        // 2. Absent key (or null collection): a generation created after this call (always the case on refresh), or younger than the interval, is authoritative.
        if (requestedAt - generation.CreatedAt < AbsentKeyRebuildInterval) {
            return default;
        }

        // 3. Stale generation: rebuild once. Concurrent misses and refreshes coalesce into the first generation newer than this one.
        var rebuilt = Acquire(store, collection, factory, generation.CreatedAt + 1);
        items = await WithCancellation(rebuilt.Completion.Task, cancellationToken).ConfigureAwait(false);

        return items is not null && items.TryGetValue(key, out value) ? new CacheResult<TValue>(value) : default;
    }

    private Entry<TValue> Acquire<TValue>(MemoryCache store, object key, Func<Task<TValue>> factory, long freshAfter) {
        // 1. Lock-free fast path: a live entry that satisfies freshness is shared without touching a stripe.
        if (store.TryGetValue(key, out var found) && TryResolve(Cast<TValue>(found!, key), key, freshAfter, out var resolved)) {
            return resolved;
        }

        Entry<TValue> candidate;

        lock (Stripe(key)) {
            // 2. Double-check under the stripe: another caller may have published a qualifying entry in the meantime.
            Entry<TValue>? current = null;

            if (store.TryGetValue(key, out found)) {
                current = Cast<TValue>(found!, key);

                if (TryResolve(current, key, freshAfter, out resolved)) {
                    return resolved;
                }
            }

            // 3. Miss or outdated entry: publish a new generation that keeps the latest successful value servable while it loads.
            candidate = new Entry<TValue> {
                Stale = current is null ? null : current.Completion.Task.Status == TaskStatus.RanToCompletion ? current : Volatile.Read(ref current.Stale)
            };

            _ = store.Set(key, candidate);
        }

        // 4. Only the caller that published the entry runs the factory, outside the stripe; the task never faults (outcome lives in the entry).
        _ = PopulateAsync(store, key, candidate, factory);
        return candidate;
    }

    private static bool TryResolve<TValue>(Entry<TValue> entry, object key, long freshAfter, [NotNullWhen(true)] out Entry<TValue>? resolved) {
        if (!entry.Completion.Task.IsCompleted) {
            // 1. Rebuild in progress: serve the previous successful generation when it satisfies freshness.
            var previous = Volatile.Read(ref entry.Stale);

            if (previous is not null && previous.CreatedAt >= freshAfter) {
                resolved = previous;
                return true;
            }

            // 2. Waiting on (or replacing) an entry this call chain is populating would never complete: fail loudly.
            if (IsPopulating(entry)) {
                throw new InvalidOperationException($"Key '{key}' is being populated by the current call chain; its factory cannot await or refresh it.");
            }
        }

        // 3. Existing entry (completed or in-flight) that satisfies freshness is shared by every caller.
        if (entry.CreatedAt >= freshAfter) {
            resolved = entry;
            return true;
        }

        resolved = null;
        return false;
    }

    private async Task PopulateAsync<TValue>(MemoryCache store, object key, Entry<TValue> entry, Func<Task<TValue>> factory) {
        TValue value;

        // 1. Scope this entry to the factory's logical call chain; the async method restores the caller's context on return.
        Populating.Value = new Frame(entry, Populating.Value);

        try {
            value = await factory().ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) {
            // 2. Unpublish before completing so no caller arriving after completion observes a cancelled entry.
            Unpublish(store, key, entry);
            _ = entry.Completion.TrySetCanceled(exception.CancellationToken);
            return;
        }
        catch (Exception exception) {
            // 3. Unpublish before completing so the next caller retries, while current waiters share this failure.
            Unpublish(store, key, entry);
            _ = entry.Completion.TrySetException(exception);
            return;
        }

        // 4. Every result, including null, is retained; the previous generation is released once this one is servable.
        _ = entry.Completion.TrySetResult(value);
        Volatile.Write(ref entry.Stale, null);
    }

    private void Unpublish<TValue>(MemoryCache store, object key, Entry<TValue> entry) {
        lock (Stripe(key)) {
            // 1. Compare-and-act: only touch the key while it still maps to this exact entry, never a newer generation.
            if (!store.TryGetValue(key, out var current) || !ReferenceEquals(current, entry)) {
                return;
            }

            // 2. A failed rebuild restores the previous successful generation instead of discarding it.
            var previous = Volatile.Read(ref entry.Stale);

            if (previous is not null) {
                _ = store.Set(key, previous);
            }
            else {
                store.Remove(key);
            }
        }
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

    private Container GetContainer(string container) => _containers.GetOrAdd(container, static _ => new Container());

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
        // Store for both keyspaces: scalar values keyed by their string, collections keyed by their interned CollectionKey.
        internal readonly MemoryCache Store = new(new MemoryCacheOptions());

        // Interns one CollectionKey per collection name so collection lookups allocate nothing after first use.
        private readonly ConcurrentDictionary<string, CollectionKey> _collectionKeys = new(StringComparer.Ordinal);

        internal CollectionKey GetCollectionKey(string collection) => _collectionKeys.GetOrAdd(collection, static name => new CollectionKey(name));

        public void Dispose() => Store.Dispose();
    }

    // Reference-equality key: never equal to a string, so a collection and a scalar key with the same name cannot collide.
    private sealed class CollectionKey(string name) {
        public override string ToString() => name;
    }

    private abstract class Entry {
        // Monotonic creation stamp used to order generations for refresh and absent-key decisions.
        internal readonly long CreatedAt = Stopwatch.GetTimestamp();

        internal abstract Type ValueType { get; }
    }

    private sealed class Entry<TValue> : Entry {
        // Continuations run asynchronously so completing the entry never executes waiter code on the factory's thread.
        internal readonly TaskCompletionSource<TValue> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Latest successful generation, served while this one loads; cleared once this one completes.
        internal Entry<TValue>? Stale;

        internal override Type ValueType => typeof(TValue);
    }

    private sealed class Frame(Entry current, Frame? parent) {
        internal readonly Entry Current = current;
        internal readonly Frame? Parent = parent;
    }
}
