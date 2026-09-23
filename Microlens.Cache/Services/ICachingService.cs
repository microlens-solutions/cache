using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Microlens.Cache.Services {
    public interface ICachingService {
        Task<TValue> GetOrAddAsync<TValue>(string container, string key, Func<Task<TValue>> factory, bool refresh = false, CancellationToken cancellationToken = default);

        Task<CacheResult<TValue>> GetOrAddAsync<TKey, TValue>(string container, string collection, TKey key, Func<Task<IReadOnlyDictionary<TKey, TValue>>> factory, bool refresh = false, CancellationToken cancellationToken = default);
    }
}
