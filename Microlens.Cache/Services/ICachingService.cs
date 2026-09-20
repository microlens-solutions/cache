using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Microlens.Cache.Services {
    public interface ICachingService {
        Task<TValue> GetOrAddAsync<TValue>(string container, string key, Func<Task<TValue>> factory, bool rebuild = false);

        Task<TValue> GetOrAddAsync<TKey, TValue>(string container, string collection, TKey key, Func<Task<ConcurrentDictionary<TKey, TValue>>> factory, bool refresh = false);
    }
}
