using Microlens.Cache.Contracts;
using Microlens.Cache.Models;
using Microlens.Cache.Options;
using Microlens.Cache.Shared;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Microlens.Cache.Infrastructure;

internal sealed class Container : IDisposable {
    private readonly ConcurrentDictionary<string, CollectionKey> _collectionKeys = new(StringComparer.Ordinal);

    internal readonly MemoryCache Store;

    internal readonly ConcurrentDictionary<object, EntryBase> Pending = new();

    internal readonly LfuIndex? Lfu;

    internal readonly bool Sized;

    internal readonly CacheExpiration Expiration;

    internal readonly long AbsentKeyRebuildInterval;

    internal Container(ContainerOptions options, ContainerOptions defaults) {
        var store = new MemoryCacheOptions();

        switch (options.Eviction) {
            case Registry.EvictionPolicy.Lru:
                store.SizeLimit = options.Capacity;
                store.CompactionPercentage = options.CompactionPercentage;
                Sized = true;
                break;

            case Registry.EvictionPolicy.Lfu:
                Lfu = new LfuIndex(options.Capacity, options.CompactionPercentage);
                break;
        }

        if ((options.ExpirationScanFrequency ?? defaults.ExpirationScanFrequency) is { } scan) {
            store.ExpirationScanFrequency = scan;
        }

        Store = new MemoryCache(store);
        Expiration = options.Expiration ?? defaults.Expiration ?? CacheExpiration.Default;
        AbsentKeyRebuildInterval = ToStopwatchTicks(options.AbsentKeyRebuildInterval ?? defaults.AbsentKeyRebuildInterval ?? Registry.OptionsAbsentKeyRebuildIntervalDefaultValue);
    }

    internal CollectionKey GetCollectionKey(string collection) {
        return _collectionKeys.GetOrAdd(collection, static name => new CollectionKey(name));
    }

    internal bool TryGetCollectionKey(string collection, [NotNullWhen(true)] out CollectionKey? key) {
        return _collectionKeys.TryGetValue(collection, out key);
    }

    public void Dispose() {
        Store.Dispose();
    }

    private static long ToStopwatchTicks(TimeSpan interval) {
        double ticks = interval.Ticks * (Stopwatch.Frequency / (double)TimeSpan.TicksPerSecond);
        return ticks >= long.MaxValue ? long.MaxValue : (long)ticks;
    }
}
