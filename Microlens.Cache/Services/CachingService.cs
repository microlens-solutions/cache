using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Microlens.Cache.Services {
    internal class CachingService : ICachingService {
        // One comparer for the container registry and every map inside a container, so a key resolves to exactly one entry (C2).
        private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

        // Minimum age (30 seconds, in Stopwatch ticks) a collection generation must reach before a lookup of an absent key may rebuild it (C3).
        private static readonly long AbsentKeyRebuildInterval = 30 * Stopwatch.Frequency;

        // Containers owned by this instance; nothing is shared with other in-process consumers (C5).
        private readonly ConcurrentDictionary<string, Container> _containers;

        public CachingService() {
            _containers = new ConcurrentDictionary<string, Container>(KeyComparer);
        }

        // Interface
        public Task<TValue> GetOrAddAsync<TValue>(string container, string key, Func<Task<TValue>> factory, bool rebuild = false) {
            // 1. Rebuild (Forced Refresh) only accepts an entry created at or after this call; otherwise any entry qualifies.
            long freshAfter = rebuild ? Stopwatch.GetTimestamp() : long.MinValue;

            // 2. Hit returns the stored task as-is (allocation-free); miss returns the task of the single in-flight factory.
            return Acquire(GetContainer(container).Values, key, factory, freshAfter).Completion.Task;
        }

        // Interface
        public Task<TValue> GetOrAddAsync<TKey, TValue>(string container, string collection, TKey key, Func<Task<ConcurrentDictionary<TKey, TValue>>> factory, bool refresh = false) {
            return GetFromCollectionAsync(GetContainer(container).Collections, collection, key, factory, refresh);
        }

        private static async Task<TValue> GetFromCollectionAsync<TKey, TValue>(ConcurrentDictionary<string, Entry> collections, string collection, TKey key, Func<Task<ConcurrentDictionary<TKey, TValue>>> factory, bool refresh) {
            long requestedAt = Stopwatch.GetTimestamp();

            // 1. Resolve the current generation or, when refreshing, one created at or after this call.
            var generation = Acquire(collections, collection, factory, refresh ? requestedAt : long.MinValue);
            var items = await generation.Completion.Task.ConfigureAwait(false);

            if (items == null) {
                return default;
            }

            if (items.TryGetValue(key, out var value)) {
                return value;
            }

            // 2. Absent key: a generation created after this call (always the case on refresh), or younger than the interval, is authoritative (C3).
            if (requestedAt - generation.CreatedAt < AbsentKeyRebuildInterval) {
                return default;
            }

            // 3. Stale generation: rebuild once. Concurrent misses and refreshes coalesce into the first generation newer than this one (C8).
            var rebuilt = Acquire(collections, collection, factory, generation.CreatedAt + 1);
            items = await rebuilt.Completion.Task.ConfigureAwait(false);

            return items != null && items.TryGetValue(key, out value) ? value : default;
        }

        private static Entry<TValue> Acquire<TValue>(ConcurrentDictionary<string, Entry> map, string key, Func<Task<TValue>> factory, long freshAfter) {
            Entry<TValue> candidate = null;

            while (true) {
                if (map.TryGetValue(key, out var current)) {
                    // 1. Existing entry (completed or in-flight) that satisfies the freshness requirement is shared by every caller.
                    var typed = Cast<TValue>(current, key);

                    if (typed.CreatedAt >= freshAfter) {
                        return typed;
                    }

                    // 2. Stale entry: atomically swap in a new generation only if nobody replaced it first; otherwise re-evaluate.
                    candidate = candidate ?? new Entry<TValue>();

                    if (!map.TryUpdate(key, candidate, current)) {
                        continue;
                    }
                }
                else {
                    // 3. Miss: atomically publish a new entry; losing the race means another caller's entry is re-evaluated.
                    candidate = candidate ?? new Entry<TValue>();

                    if (!map.TryAdd(key, candidate)) {
                        continue;
                    }
                }

                // 4. Only the caller that published the entry runs the factory; the task never faults (outcome lives in the entry).
                _ = PopulateAsync(map, key, candidate, factory);
                return candidate;
            }
        }

        private static async Task PopulateAsync<TValue>(ConcurrentDictionary<string, Entry> map, string key, Entry<TValue> entry, Func<Task<TValue>> factory) {
            TValue value;

            try {
                value = await factory().ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) {
                // 1. Unpublish before completing so no caller arriving after completion observes a cancelled entry.
                Evict(map, key, entry);
                _ = entry.Completion.TrySetCanceled(exception.CancellationToken);
                return;
            }
            catch (Exception exception) {
                // 2. Unpublish before completing so the next caller retries, while current waiters share this failure.
                Evict(map, key, entry);
                _ = entry.Completion.TrySetException(exception);
                return;
            }

            // 3. Null results are handed to current waiters but not retained.
            if (value == null) {
                Evict(map, key, entry);
            }

            _ = entry.Completion.TrySetResult(value);
        }

        private static void Evict(ConcurrentDictionary<string, Entry> map, string key, Entry entry) {
            // Compare-and-remove: removes the key only while it still maps to this exact entry, never a newer generation (C1).
            _ = ((ICollection<KeyValuePair<string, Entry>>)map).Remove(new KeyValuePair<string, Entry>(key, entry));
        }

        private static Entry<TValue> Cast<TValue>(Entry entry, string key) {
            if (entry is Entry<TValue> typed) {
                return typed;
            }

            // Loud failure instead of an InvalidCastException deep inside the caller (C6).
            throw new InvalidOperationException($"Key '{key}' is cached as '{entry.ValueType}' and cannot be read as '{typeof(TValue)}'.");
        }

        private Container GetContainer(string container) => _containers.GetOrAdd(container, _ => new Container());

        private sealed class Container {
            // Scalar values and collections live in separate keyspaces (C6).
            internal readonly ConcurrentDictionary<string, Entry> Values = new ConcurrentDictionary<string, Entry>(KeyComparer);
            internal readonly ConcurrentDictionary<string, Entry> Collections = new ConcurrentDictionary<string, Entry>(KeyComparer);
        }

        private abstract class Entry {
            // Monotonic creation stamp used to order generations for rebuild, refresh and absent-key decisions.
            internal readonly long CreatedAt = Stopwatch.GetTimestamp();

            internal abstract Type ValueType { get; }
        }

        private sealed class Entry<TValue> : Entry {
            // Continuations run asynchronously so completing the entry never executes waiter code on the factory's thread.
            internal readonly TaskCompletionSource<TValue> Completion = new TaskCompletionSource<TValue>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal override Type ValueType => typeof(TValue);
        }
    }
}
