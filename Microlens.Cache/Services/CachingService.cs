using System.Collections.Concurrent;
using System.Diagnostics;
using Microlens.Cache.Contracts;
using Microlens.Cache.Infrastructure;
using Microlens.Cache.Models;
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
    private static readonly AsyncLocal<Frame?> Populating = new();

    private readonly CacheOptions _options;

    private readonly ConcurrentDictionary<string, Container> _containers = new(StringComparer.Ordinal);

    private readonly Func<string, Container> _function;

    private readonly StripeLock[] _stripes = CreateStripes();

    private int _disposed;

    public CachingService(IOptions<CacheOptions> options) {
        Guard.NotNull(options);

        _options = options.Value;
        _options.Validate();
        _function = name => new Container(_options.Resolve(name));
    }

    public Task<TValue> GetOrAddAsync<TValue>(string container, string key, Func<Task<TValue>> factory, CacheExpiration? expiration = null, bool refresh = false, CancellationToken cancellationToken = default) {
        Guard.NotNull(container);
        Guard.NotNull(key);
        Guard.NotNull(factory);
        ThrowIfDisposed();

        return cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<TValue>(cancellationToken)
            : WithCancellation(Acquire(GetContainer(container), key, factory, expiration ?? CacheExpiration.Default, refresh ? Stopwatch.GetTimestamp() : long.MinValue).Completion.Task, cancellationToken);
    }

    public Task<CacheResult<TValue>> GetOrAddAsync<TKey, TValue>(string container, string collection, TKey key, Func<Task<IReadOnlyDictionary<TKey, TValue>>> factory, CacheExpiration? expiration = null, bool refresh = false, CancellationToken cancellationToken = default) where TKey : notnull {
        Guard.NotNull(container);
        Guard.NotNull(collection);
        Guard.NotNull(key);
        Guard.NotNull(factory);
        ThrowIfDisposed();

        if (cancellationToken.IsCancellationRequested) {
            return Task.FromCanceled<CacheResult<TValue>>(cancellationToken);
        }

        var owner = GetContainer(container);
        return GetFromCollectionAsync(owner, owner.GetCollectionKey(collection), key, factory, expiration ?? CacheExpiration.Default, refresh, cancellationToken);
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
        long timestamp = Stopwatch.GetTimestamp();
        var first = Acquire(container, collection, factory, expiration, refresh ? timestamp : long.MinValue);
        var items = await WithCancellation(first.Completion.Task, cancellationToken).ConfigureAwait(false);

        if (items is not null && items.TryGetValue(key, out var value)) {
            return new CacheResult<TValue>(value);
        }

        if (timestamp - first.CreatedAt < Registry.AbsentKeyRebuildInterval) {
            return default;
        }

        var second = Acquire(container, collection, factory, expiration, first.CreatedAt + 1);
        items = await WithCancellation(second.Completion.Task, cancellationToken).ConfigureAwait(false);

        return items is not null && items.TryGetValue(key, out value) ? new CacheResult<TValue>(value) : default;
    }

    private Entry<TValue> Acquire<TValue>(Container container, object key, Func<Task<TValue>> factory, CacheExpiration expiration, long freshAfter) {
        Entry<TValue>? candidate = null;

        while (true) {
            if (container.Store.TryGetValue(key, out var found)) {
                if (found != null) {
                    var stored = Cast<TValue>(found, key);

                    if (stored.CreatedAt >= freshAfter) {
                        container.Lfu?.Touch(stored);
                        return stored;
                    }
                }
            }

            if (container.Pending.TryGetValue(key, out var pending)) {
                var flight = Cast<TValue>(pending, key);

                if (IsPopulating(flight)) {
                    throw new InvalidOperationException($"Key '{key}' is being populated by the current call chain; its factory cannot await or refresh it.");
                }

                if (flight.CreatedAt >= freshAfter) {
                    return flight;
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

                if (container.Store.TryGetValue(key, out found) && Cast<TValue>(found!, key) is var latest && latest.CreatedAt >= freshAfter) {
                    _ = candidate.Completion.TrySetResult(latest.Completion.Task.Result);
                    RemovePending(container, key, candidate);

                    return latest;
                }
            }

            _ = PopulateAsync(container, key, candidate, factory);
            return candidate;
        }
    }

    private async Task PopulateAsync<TValue>(Container container, object key, Entry<TValue> entry, Func<Task<TValue>> factory) {
        TValue value;

        Populating.Value = new Frame(entry, Populating.Value);

        try {
            value = await factory().ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) {
            RemovePending(container, key, entry);
            _ = entry.Completion.TrySetCanceled(exception.CancellationToken);

            return;
        }
        catch (Exception exception) {
            RemovePending(container, key, entry);
            _ = entry.Completion.TrySetException(exception);

            return;
        }

        _ = entry.Completion.TrySetResult(value);
        Commit(container, key, entry);
        RemovePending(container, key, entry);
    }

    private void Commit(Container container, object key, EntryBase entry) {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        try {
            lock (Stripe(key)) {
                var current = container.Store.TryGetValue(key, out var found) ? (EntryBase)found! : null;

                if (current is not null && current.CreatedAt > entry.CreatedAt) {
                    return;
                }

                WriteEntry(container, key, entry, current);
            }

            if (container.Lfu is not null) {
                Compact(container, entry);
            }
        }
        catch (ObjectDisposedException) { }
    }

    private static void WriteEntry(Container container, object key, EntryBase current, EntryBase? previous) {
        var lfu = container.Lfu;

        if (lfu is not null) {
            current.Frequency = previous?.Frequency ?? 0;
            lfu.Track(key, current);
        }

        using var entry = container.Store.CreateEntry(key);
        var expiration = current.Expiration;

        entry.AbsoluteExpirationRelativeToNow = expiration.Absolute is { } absolute && absolute.Ticks < Registry.MaximumRelativeLifetimeTicks ? absolute : null;
        entry.SlidingExpiration = expiration.Sliding;

        if (container.Sized) {
            entry.Size = 1;
        }

        if (lfu is not null) {
            _ = entry.RegisterPostEvictionCallback(LfuIndex.OnEvicted, lfu);
        }

        entry.Value = current;
    }

    private void Compact(Container container, EntryBase committed) {
        var lfu = container.Lfu!;

        if (!lfu.IsOverCapacity || !lfu.TryBeginCompaction()) {
            return;
        }

        try {
            long target = lfu.EvictionTarget;

            if (target <= 0) {
                return;
            }

            long evicted = 0;
            var victims = lfu.Snapshot(committed);
            victims.Sort(Victim.Order);

            for (int i = 0; i < victims.Count && evicted < target; i++) {
                var victim = victims[i];

                lock (Stripe(victim.Key)) {
                    if (lfu.Untrack(victim.Key, victim.Entry)) {
                        container.Store.Remove(victim.Key);
                        evicted++;
                    }
                }
            }

            lfu.Age();
        }
        finally {
            lfu.EndCompaction();
        }
    }

    private static void RemovePending(Container container, object key, EntryBase entry) {
        _ = ((ICollection<KeyValuePair<object, EntryBase>>)container.Pending).Remove(new KeyValuePair<object, EntryBase>(key, entry));
    }

    private static bool IsPopulating(EntryBase entry) {
        for (var frame = Populating.Value; frame is not null; frame = frame.Parent) {
            if (ReferenceEquals(frame.Current, entry)) {
                return true;
            }
        }

        return false;
    }

    private static Entry<TValue> Cast<TValue>(object entry, object key) {
        return entry is Entry<TValue> typed
            ? typed
            : throw new InvalidOperationException($"Key '{key}' is cached as '{((EntryBase)entry).ValueType}' and cannot be read as '{typeof(TValue)}'.");
    }

    private StripeLock Stripe(object key) {
        return _stripes[key.GetHashCode() & (Registry.StripeCount - 1)];
    }

    private static StripeLock[] CreateStripes() {
        var stripes = new StripeLock[Registry.StripeCount];

        for (int i = 0; i < stripes.Length; i++) {
            stripes[i] = new StripeLock();
        }

        return stripes;
    }

    private Container GetContainer(string container) {
        return _containers.GetOrAdd(container, _function);
    }

    private void ThrowIfDisposed() {
#if NET7_0_OR_GREATER
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
#else
        if (Volatile.Read(ref _disposed) != 0) {
            throw new ObjectDisposedException(nameof(CachingService));
        }
#endif
    }

    private static Task<TValue> WithCancellation<TValue>(Task<TValue> task, CancellationToken cancellationToken) {
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
}
