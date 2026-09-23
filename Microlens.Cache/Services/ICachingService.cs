using Microlens.Cache.Contracts;

namespace Microlens.Cache.Services;

public interface ICachingService {
    Task<TValue> GetOrAddAsync<TValue>(string container, string key, Func<Task<TValue>> factory, CacheExpiration? expiration = null, bool refresh = false, CancellationToken cancellationToken = default);

    Task<CacheResult<TValue>> GetOrAddAsync<TKey, TValue>(string container, string collection, TKey key, Func<Task<IReadOnlyDictionary<TKey, TValue>>> factory, CacheExpiration? expiration = null, bool refresh = false, CancellationToken cancellationToken = default) where TKey : notnull;
}
