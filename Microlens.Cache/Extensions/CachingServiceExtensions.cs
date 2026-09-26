using Microlens.Cache.Contracts;
using Microlens.Cache.Services;
using Microlens.Internal;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Microlens.Cache.Extensions;

public static class CachingServiceExtensions {
    public static Task<TValue> GetOrAddAsync<TState, TValue>(this ICachingService cache, string container, string key, TState state, Func<TState, CancellationToken, Task<TValue>> factory, CacheExpiration? expiration = null, bool refresh = false, CancellationToken cancellationToken = default) {
        Guard.NotNull(cache);
        Guard.NotNull(factory);

        return cache is CachingService service
            ? service.GetOrAddWithStateAsync(container, key, state, factory, expiration, refresh, cancellationToken)
            : cache.GetOrAddAsync(container, key, () => factory(state, CancellationToken.None), expiration, refresh, cancellationToken);
    }

    public static Task<CacheResult<TValue>> GetOrAddAsync<TState, TKey, TValue>(this ICachingService cache, string container, string collection, TKey key, TState state, Func<TState, CancellationToken, Task<IReadOnlyDictionary<TKey, TValue>>> factory, CacheExpiration? expiration = null, bool refresh = false, CancellationToken cancellationToken = default) where TKey : notnull {
        Guard.NotNull(cache);
        Guard.NotNull(factory);

        return cache is CachingService service
            ? service.GetOrAddWithStateAsync(container, collection, key, state, factory, expiration, refresh, cancellationToken)
            : cache.GetOrAddAsync(container, collection, key, () => factory(state, CancellationToken.None), expiration, refresh, cancellationToken);
    }
}
