using Microlens.Cache.Contracts;
using System;
using System.Threading.Tasks;

namespace Microlens.Cache.Models;

internal sealed class Entry<TValue>(CacheExpiration expiration, bool cancellable) : EntryBase(expiration, cancellable) {
    internal readonly TaskCompletionSource<TValue> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal override Type ValueType => typeof(TValue);
}
