using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Microlens.Cache.Services {
    internal sealed class CachingService : ICachingService {
        // One comparer for the container registry and every map inside a container, so a key resolves to exactly one entry.
        private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

        // Minimum age (30 seconds, in Stopwatch ticks) a collection generation must reach before a lookup of an absent key may rebuild it.
        private static readonly long AbsentKeyRebuildInterval = 30 * Stopwatch.Frequency;

        // Entries whose factory is executing in the current logical call chain; flows across awaits to detect re-entrant factories (S8).
        private static readonly AsyncLocal<Frame> Populating = new AsyncLocal<Frame>();

        // Containers owned by this instance; nothing is shared with other in-process consumers.
        private readonly ConcurrentDictionary<string, Container> _containers;

        public CachingService() {
            _containers = new ConcurrentDictionary<string, Container>(KeyComparer);
        }

        // Interface
        public Task<TValue> GetOrAddAsync<TValue>(string container, string key, Func<Task<TValue>> factory, bool refresh = false, CancellationToken cancellationToken = default) {
            // 1. Validate synchronously so misuse fails at the call site, not inside a faulted task (S13).
            if (container == null) {
                throw new ArgumentNullException(nameof(container));
            }

            if (key == null) {
                throw new ArgumentNullException(nameof(key));
            }

            if (factory == null) {
                throw new ArgumentNullException(nameof(factory));
            }

            // 2. An already-cancelled caller never publishes an entry or starts a factory (S9).
            if (cancellationToken.IsCancellationRequested) {
                return Task.FromCanceled<TValue>(cancellationToken);
            }

            // 3. Refresh only accepts an entry created at or after this call; otherwise any entry qualifies.
            long freshAfter = refresh ? Stopwatch.GetTimestamp() : long.MinValue;

            // 4. Hit returns the stored task as-is (allocation-free); miss returns the task of the single in-flight factory.
            return WithCancellation(Acquire(GetContainer(container).Values, key, factory, freshAfter).Completion.Task, cancellationToken);
        }

        // Interface
        public Task<CacheResult<TValue>> GetOrAddAsync<TKey, TValue>(string container, string collection, TKey key, Func<Task<IReadOnlyDictionary<TKey, TValue>>> factory, bool refresh = false, CancellationToken cancellationToken = default) {
            // 1. Validate synchronously so misuse fails at the call site, not inside a faulted task (S13).
            if (container == null) {
                throw new ArgumentNullException(nameof(container));
            }

            if (collection == null) {
                throw new ArgumentNullException(nameof(collection));
            }

            if (key == null) {
                throw new ArgumentNullException(nameof(key));
            }

            if (factory == null) {
                throw new ArgumentNullException(nameof(factory));
            }

            // 2. An already-cancelled caller never publishes an entry or starts a factory (S9).
            return cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<CacheResult<TValue>>(cancellationToken)
                : GetFromCollectionAsync(GetContainer(container).Collections, collection, key, factory, refresh, cancellationToken);
        }

        private static async Task<CacheResult<TValue>> GetFromCollectionAsync<TKey, TValue>(ConcurrentDictionary<string, Entry> collections, string collection, TKey key, Func<Task<IReadOnlyDictionary<TKey, TValue>>> factory, bool refresh, CancellationToken cancellationToken) {
            long requestedAt = Stopwatch.GetTimestamp();

            // 1. Resolve the current generation (or the previous one while a rebuild loads) or, when refreshing, one created at or after this call.
            var generation = Acquire(collections, collection, factory, refresh ? requestedAt : long.MinValue);
            var items = await WithCancellation(generation.Completion.Task, cancellationToken).ConfigureAwait(false);

            if (items != null && items.TryGetValue(key, out var value)) {
                return new CacheResult<TValue>(value);
            }

            // 2. Absent key (or null collection): a generation created after this call (always the case on refresh), or younger than the interval, is authoritative.
            if (requestedAt - generation.CreatedAt < AbsentKeyRebuildInterval) {
                return default;
            }

            // 3. Stale generation: rebuild once. Concurrent misses and refreshes coalesce into the first generation newer than this one.
            var rebuilt = Acquire(collections, collection, factory, generation.CreatedAt + 1);
            items = await WithCancellation(rebuilt.Completion.Task, cancellationToken).ConfigureAwait(false);

            return items != null && items.TryGetValue(key, out value) ? new CacheResult<TValue>(value) : default;
        }

        private static Entry<TValue> Acquire<TValue>(ConcurrentDictionary<string, Entry> map, string key, Func<Task<TValue>> factory, long freshAfter) {
            Entry<TValue> candidate = null;

            while (true) {
                if (map.TryGetValue(key, out var current)) {
                    var typed = Cast<TValue>(current, key);
                    var task = typed.Completion.Task;

                    if (!task.IsCompleted) {
                        // 1. Rebuild in progress: serve the previous successful generation when it satisfies freshness (S3).
                        var previous = Volatile.Read(ref typed.Stale);

                        if (previous != null && previous.CreatedAt >= freshAfter) {
                            return previous;
                        }

                        // 2. Waiting on (or replacing) an entry this call chain is populating would never complete: fail loudly (S8).
                        if (IsPopulating(typed)) {
                            throw new InvalidOperationException($"Key '{key}' is being populated by the current call chain; its factory cannot await or refresh it.");
                        }
                    }

                    // 3. Existing entry (completed or in-flight) that satisfies freshness is shared by every caller.
                    if (typed.CreatedAt >= freshAfter) {
                        return typed;
                    }

                    // 4. Outdated entry: atomically swap in a new generation that keeps the latest successful value servable while it loads.
                    candidate = candidate ?? new Entry<TValue>();
                    candidate.Stale = task.Status == TaskStatus.RanToCompletion ? typed : Volatile.Read(ref typed.Stale);

                    if (!map.TryUpdate(key, candidate, current)) {
                        continue;
                    }
                }
                else {
                    // 5. Miss: atomically publish a new entry; losing the race means another caller's entry is re-evaluated.
                    candidate = candidate ?? new Entry<TValue>();
                    candidate.Stale = null;

                    if (!map.TryAdd(key, candidate)) {
                        continue;
                    }
                }

                // 6. Only the caller that published the entry runs the factory; the task never faults (outcome lives in the entry).
                _ = PopulateAsync(map, key, candidate, factory);
                return candidate;
            }
        }

        private static async Task PopulateAsync<TValue>(ConcurrentDictionary<string, Entry> map, string key, Entry<TValue> entry, Func<Task<TValue>> factory) {
            TValue value;

            // 1. Scope this entry to the factory's logical call chain; the async method restores the caller's context on return (S8).
            Populating.Value = new Frame(entry, Populating.Value);

            try {
                value = await factory().ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) {
                // 2. Unpublish before completing so no caller arriving after completion observes a cancelled entry.
                Unpublish(map, key, entry);
                _ = entry.Completion.TrySetCanceled(exception.CancellationToken);
                return;
            }
            catch (Exception exception) {
                // 3. Unpublish before completing so the next caller retries, while current waiters share this failure.
                Unpublish(map, key, entry);
                _ = entry.Completion.TrySetException(exception);
                return;
            }

            // 4. Every result, including null, is retained (S4); the previous generation is released once this one is servable.
            _ = entry.Completion.TrySetResult(value);
            Volatile.Write(ref entry.Stale, null);
        }

        private static void Unpublish<TValue>(ConcurrentDictionary<string, Entry> map, string key, Entry<TValue> entry) {
            var previous = Volatile.Read(ref entry.Stale);

            // 1. A failed rebuild restores the previous successful generation instead of discarding it (S3).
            if (previous != null) {
                _ = map.TryUpdate(key, previous, entry);
                return;
            }

            // 2. Compare-and-remove: removes the key only while it still maps to this exact entry, never a newer generation.
            _ = ((ICollection<KeyValuePair<string, Entry>>)map).Remove(new KeyValuePair<string, Entry>(key, entry));
        }

        private static bool IsPopulating(Entry entry) {
            for (var frame = Populating.Value; frame != null; frame = frame.Parent) {
                if (ReferenceEquals(frame.Current, entry)) {
                    return true;
                }
            }

            return false;
        }

        private static Task<TValue> WithCancellation<TValue>(Task<TValue> task, CancellationToken cancellationToken) {
            // Completed tasks and non-cancellable tokens pass through without allocation.
            if (task.IsCompleted || !cancellationToken.CanBeCanceled) {
                return task;
            }

#if NET6_0_OR_GREATER
            return task.WaitAsync(cancellationToken);
#else
            return WaitAsync(task, cancellationToken);
#endif
        }

#if !NET6_0_OR_GREATER
        private static async Task<TValue> WaitAsync<TValue>(Task<TValue> task, CancellationToken cancellationToken) {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state).TrySetResult(true), cancelled)) {
                if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task) {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            return await task.ConfigureAwait(false);
        }
#endif

        private static Entry<TValue> Cast<TValue>(Entry entry, string key) {
            if (entry is Entry<TValue> typed) {
                return typed;
            }

            // Loud failure instead of an InvalidCastException deep inside the caller.
            throw new InvalidOperationException($"Key '{key}' is cached as '{entry.ValueType}' and cannot be read as '{typeof(TValue)}'.");
        }

        private Container GetContainer(string container) => _containers.GetOrAdd(container, _ => new Container());

        private sealed class Container {
            // Scalar values and collections live in separate keyspaces.
            internal readonly ConcurrentDictionary<string, Entry> Values = new ConcurrentDictionary<string, Entry>(KeyComparer);
            internal readonly ConcurrentDictionary<string, Entry> Collections = new ConcurrentDictionary<string, Entry>(KeyComparer);
        }

        private abstract class Entry {
            // Monotonic creation stamp used to order generations for refresh and absent-key decisions.
            internal readonly long CreatedAt = Stopwatch.GetTimestamp();

            internal abstract Type ValueType { get; }
        }

        private sealed class Entry<TValue> : Entry {
            // Continuations run asynchronously so completing the entry never executes waiter code on the factory's thread.
            internal readonly TaskCompletionSource<TValue> Completion = new TaskCompletionSource<TValue>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Latest successful generation, served while this one loads; cleared once this one completes (S3).
            internal Entry<TValue> Stale;

            internal override Type ValueType => typeof(TValue);
        }

        private sealed class Frame {
            internal readonly Entry Current;
            internal readonly Frame Parent;

            internal Frame(Entry current, Frame parent) {
                Current = current;
                Parent = parent;
            }
        }
    }
}
