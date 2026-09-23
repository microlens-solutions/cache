namespace Microlens.Cache.Shared;

public class Registry {
    internal const EvictionPolicy OptionsEvictionPolicyDefaultValue = EvictionPolicy.None;

    internal const double OptionsCompactionPercentageDefaultValue = 0.05;

    public enum EvictionPolicy {
        None,

        Lru,

        Lfu
    }
}
