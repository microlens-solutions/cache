using Microlens.Cache.Contracts;

namespace Microlens.Cache.Models;

internal sealed class Entry<TValue>(CacheExpiration expiration) : EntryBase(expiration) {
    internal readonly TaskCompletionSource<TValue> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal override Type ValueType => typeof(TValue);
}
