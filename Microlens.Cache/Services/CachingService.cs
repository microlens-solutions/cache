using System.Collections.Concurrent;
using System.Runtime.Caching;

namespace Microlens.Cache.Services;

internal class CachingService : ICachingService {
    // Underlying .NET MemoryCache instance used to persist containers globally.
    private readonly ObjectCache _cache;

    // A dictionary tracking fine-grained locks per specific key/container to prevent Cache Stampede (multiple concurrent threads from computing the exact same expensive operation).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks;

    public CachingService() {
        _cache = MemoryCache.Default;
        _locks = new ConcurrentDictionary<string, SemaphoreSlim>();
    }

    // Interface
    public async Task<TValue> GetOrAddAsync<TValue>(string container, string key, Func<Task<TValue>> factory, bool rebuild = false) {
        // 1. Fetch or initialize the dictionary from the MemoryCache.
        var dictionary = GetContainer(container);
        string keyLock = $"{container}:{key}";

        // 2. Rebuild (Forced Refresh).
        if (rebuild) {
            var removeLock = _locks.GetOrAdd(keyLock, _ => new SemaphoreSlim(1, 1));
            await removeLock.WaitAsync();

            try {
                // 3. Evict the existing key-value from dictionary.
                _ = dictionary.TryRemove(key, out _);
            }
            finally {
                _ = removeLock.Release();
                _ = _locks.TryRemove(keyLock, out _);
            }
        }

        // 3. Optimictic Path (If value already exists in the dictionary, return immediately without locking).
        if (dictionary.TryGetValue(key, out var value)) {
            return (TValue)value;
        }

        // 4. Pessimistic Path (Cache Miss)
        var addLock = _locks.GetOrAdd(keyLock, _ => new SemaphoreSlim(1, 1));
        await addLock.WaitAsync();

        try {
            // 5. Double Check Locking Pattern (While this thread was waiting for the lock, another thread might have already generated the value and populated the dictionary).
            if (dictionary.TryGetValue(key, out value)) {
                return (TValue)value;
            }

            value = await factory();

            if (value != null) {
                _ = dictionary.TryAdd(key, value);
            }

            return (TValue)value;
        }
        finally {
            _ = addLock.Release();
            _ = _locks.TryRemove(keyLock, out _);
        }
    }

    // Interface
    public async Task<TValue> GetOrAddAsync<TKey, TValue>(string container, string collection, TKey key, Func<Task<ConcurrentDictionary<TKey, TValue>>> factory, bool refresh = false) where TKey : notnull {
        if (refresh) {
            var refreshLock = _locks.GetOrAdd($"{container}:{collection}:refresh", _ => new SemaphoreSlim(1, 1));
            await refreshLock.WaitAsync();

            try {
                // 1. Force a hard rebuild to execute the factory and refresh the cache.
                var fresh = await GetOrAddAsync(container, collection, factory, true);

                return fresh == null ? default : fresh.TryGetValue(key, out var value) ? value : default;
            }
            finally {
                _ = refreshLock.Release();
            }
        }

        // 2. Optimictic Path (If collection already exists in the container, return immediately without locking).
        var first = await GetOrAddAsync(container, collection, factory);

        if (first == null) {
            return default;
        }

        // 3. Optimictic Path (If key already exists in the collection, return immediately without locking).
        if (first.TryGetValue(key, out var value1)) {
            return value1;
        }

        // 4. Pessimistic Path (A rebuild is required as the outer collection exists but the inner key is missing).
        var rebuildLock = _locks.GetOrAdd($"{container}:{collection}:rebuild", _ => new SemaphoreSlim(1, 1));
        await rebuildLock.WaitAsync();

        try {
            // 5. Re-evaluate the cache state.
            var current = await GetOrAddAsync(container, collection, factory);

            // 6. Double Check Locking Pattern (If `current` has changed and is no longer pointing to `first`, another thread might have already rebuilt the collection).
            if (current != null && current != first) {
                if (current.TryGetValue(key, out var value)) {
                    return value;
                }

                // 7. It means key genuinely does not exist in the fresh rebuilt collection.
                return default;
            }

            // 8. Stale Cache (If the 'current' has not changed and is is still pointing to 'first', force a hard rebuild to execute the factory and refresh the cache).
            var second = await GetOrAddAsync(container, collection, factory, true);

            return second == null ? default : second.TryGetValue(key, out var value2) ? value2 : default;
        }
        finally {
            _ = rebuildLock.Release();
        }
    }

    private ConcurrentDictionary<string, object> GetContainer(string container) {
        // 1. Optimictic Path (If container already exists in the `MemoryCache`, return immediately without locking).
        if (_cache.Contains(container)) {
            return (ConcurrentDictionary<string, object>)_cache.Get(container);
        }

        // 2. If container is not found, instantiate a new case-insensitive container.
        var current = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // 3. Define policy to ensure the garbage collector does not randomly evict the container.
        var policy = new CacheItemPolicy { Priority = CacheItemPriority.NotRemovable };

        // 4. Atomically add the container to `MemoryCache` or get it if another thread has already added.
        var existing = _cache.AddOrGetExisting(container, current, policy);

        // 5. If `AddOrGetExisting` returns null, it means `current` was successfully added, so returns `current`.
        if (existing == null) {
            return current;
        }

        // 6. If `AddOrGetExisting` returns an object, it means another thread has already added it, so returns `existing`.
        return (ConcurrentDictionary<string, object>)existing;
    }
}
