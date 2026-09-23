using Microlens.Cache.Models;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Microlens.Cache.Infrastructure;

internal sealed class LfuIndex(long capacity, double compactionPercentage) {
    private readonly ConcurrentDictionary<object, EntryBase> _items = new();

    private readonly long _capacity = capacity;

    private readonly long _lowWatermark = capacity - (long)(capacity * compactionPercentage);

    private long _count;

    private int _compacting;

    internal bool IsOverCapacity => Volatile.Read(ref _count) > _capacity;

    internal long EvictionTarget => Volatile.Read(ref _count) - _lowWatermark;

    internal static readonly PostEvictionDelegate OnEvicted = static (key, value, reason, state) => {
        if (state is not LfuIndex index || value is not EntryBase entry) {
            throw new InvalidOperationException($"LFU eviction callback for key '{key}' received state '{state?.GetType()}' and value '{value?.GetType()}' ({reason}).");
        }

        _ = index.Untrack(key, entry);
    };

    internal static void Touch(EntryBase entry) {
        if (entry.Frequency < int.MaxValue) {
            entry.Frequency++;
        }
    }

    internal void Track(object key, EntryBase entry) {
        while (true) {
            if (_items.TryGetValue(key, out var prior)) {
                if (_items.TryUpdate(key, entry, prior)) {
                    return;
                }
            }
            else if (_items.TryAdd(key, entry)) {
                _ = Interlocked.Increment(ref _count);
                return;
            }
        }
    }

    internal bool Untrack(object key, EntryBase entry) {
        if (!((ICollection<KeyValuePair<object, EntryBase>>)_items).Remove(new KeyValuePair<object, EntryBase>(key, entry))) {
            return false;
        }

        _ = Interlocked.Decrement(ref _count);
        return true;
    }

    internal bool TryBeginCompaction() {
        return Interlocked.CompareExchange(ref _compacting, 1, 0) == 0;
    }

    internal void EndCompaction() {
        Volatile.Write(ref _compacting, 0);
    }

    internal List<Victim> Snapshot(EntryBase excluded) {
        var victims = new List<Victim>((int)Math.Min(Volatile.Read(ref _count), int.MaxValue));

        foreach (var pair in _items) {
            if (!ReferenceEquals(pair.Value, excluded)) {
                victims.Add(new Victim(pair.Key, pair.Value));
            }
        }

        return victims;
    }

    internal void Age() {
        foreach (var pair in _items) {
            pair.Value.Frequency >>= 1;
        }
    }
}
