using Microlens.Cache.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Microlens.Cache.Infrastructure;

internal sealed class CacheMetrics : IDisposable {
    private readonly Meter _meter;

    private readonly Counter<long> _hits;

    private readonly Counter<long> _loads;

    private readonly Counter<long> _failures;

    private readonly Counter<long> _evictions;

    internal CacheMetrics() {
        _meter = new Meter(Registry.MeterName);
        _hits = _meter.CreateCounter<long>(Registry.MetricHits, "{entry}", "GetOrAdd calls served from a stored value.");
        _loads = _meter.CreateCounter<long>(Registry.MetricLoads, "{load}", "Factory executions started.");
        _failures = _meter.CreateCounter<long>(Registry.MetricFailures, "{load}", "Factory executions that faulted.");
        _evictions = _meter.CreateCounter<long>(Registry.MetricEvictions, "{entry}", "Entries evicted by LFU compaction.");
    }

    internal void Hit(string container) {
        Add(_hits, 1, container);
    }

    internal void Load(string container) {
        Add(_loads, 1, container);
    }

    internal void Failure(string container) {
        Add(_failures, 1, container);
    }

    internal void Evicted(string container, long count) {
        Add(_evictions, count, container);
    }

    public void Dispose() {
        _meter.Dispose();
    }

    private static void Add(Counter<long> counter, long delta, string container) {
        if (counter.Enabled) {
            counter.Add(delta, new KeyValuePair<string, object?>(Registry.MetricContainerTag, container));
        }
    }
}
