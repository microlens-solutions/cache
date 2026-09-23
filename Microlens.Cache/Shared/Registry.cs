using System.Diagnostics;

namespace Microlens.Cache.Shared;

public class Registry {
    internal const EvictionPolicy OptionsEvictionPolicyDefaultValue = EvictionPolicy.None;

    internal const double OptionsCompactionPercentageDefaultValue = 0.05;

    internal static readonly long AbsentKeyRebuildInterval = 30 * Stopwatch.Frequency;

    internal static readonly long MaximumRelativeLifetimeTicks = TimeSpan.FromDays(365 * 1000).Ticks;

    internal const int StripeCount = 128;

    public enum EvictionPolicy {
        None,

        Lru,

        Lfu
    }
}
