using System;

namespace Microlens.Cache.Shared;

internal static class Registry {
    internal const CacheRegistry.EvictionPolicy OptionsEvictionPolicyDefaultValue = CacheRegistry.EvictionPolicy.None;

    internal const double OptionsCompactionPercentageDefaultValue = 0.05;

    internal static readonly TimeSpan OptionsAbsentKeyRebuildIntervalDefaultValue = TimeSpan.FromSeconds(30);

    internal static readonly long MaximumRelativeLifetimeTicks = TimeSpan.FromDays(365 * 1000).Ticks;

    internal const int StripeCount = 128;

    internal const string MeterName = "Microlens.Cache";

    internal const string MetricHits = "microlens.cache.hits";

    internal const string MetricLoads = "microlens.cache.loads";

    internal const string MetricFailures = "microlens.cache.failures";

    internal const string MetricEvictions = "microlens.cache.evictions";

    internal const string MetricContainerTag = "container";
}
