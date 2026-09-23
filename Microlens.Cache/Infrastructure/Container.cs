using Microlens.Cache.Models;
using Microlens.Cache.Options;
using Microlens.Cache.Shared;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;

namespace Microlens.Cache.Infrastructure;

internal sealed class Container : IDisposable {
    private readonly ConcurrentDictionary<string, CollectionKey> _collectionKeys = new(StringComparer.Ordinal);

    internal readonly MemoryCache Store;

    internal readonly ConcurrentDictionary<object, EntryBase> Pending = new();

    internal readonly LfuIndex? Lfu;

    internal readonly bool Sized;

    internal Container(ContainerOptions options) {
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

        Store = new MemoryCache(store);
    }

    internal CollectionKey GetCollectionKey(string collection) => _collectionKeys.GetOrAdd(collection, static name => new CollectionKey(name));

    public void Dispose() {
        Store.Dispose();
    }
}
