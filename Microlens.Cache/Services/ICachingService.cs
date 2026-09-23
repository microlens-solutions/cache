using Microlens.Cache.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Microlens.Cache.Services;

public interface ICachingService {
    Task<TValue> GetOrAddAsync<TValue>(string container, string key, Func<Task<TValue>> factory, CacheExpiration? expiration = null, bool refresh = false, CancellationToken cancellationToken = default);

    Task<CacheResult<TValue>> GetOrAddAsync<TKey, TValue>(string container, string collection, TKey key, Func<Task<IReadOnlyDictionary<TKey, TValue>>> factory, CacheExpiration? expiration = null, bool refresh = false, CancellationToken cancellationToken = default) where TKey : notnull;

    bool TryGet<TValue>(string container, string key, [MaybeNullWhen(false)] out TValue value);

    bool TryGet<TKey, TValue>(string container, string collection, TKey key, [MaybeNullWhen(false)] out TValue value) where TKey : notnull;

    void Set<TValue>(string container, string key, TValue value, CacheExpiration? expiration = null);

    void SetCollection<TKey, TValue>(string container, string collection, IReadOnlyDictionary<TKey, TValue> items, CacheExpiration? expiration = null) where TKey : notnull;

    bool Remove(string container, string key);

    bool RemoveCollection(string container, string collection);

    bool Clear(string container);
}
