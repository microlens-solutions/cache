using Microlens.Cache.Shared;

namespace Microlens.Cache.Options;

public sealed class ContainerOptions {
    public Registry.EvictionPolicy Eviction { get; set; } = Registry.OptionsEvictionPolicyDefaultValue;

    public long Capacity { get; set; }

    public double CompactionPercentage { get; set; } = Registry.OptionsCompactionPercentageDefaultValue;

    internal void Validate(string scope) {
        switch (Eviction) {
            case Registry.EvictionPolicy.None:
                return;

            case Registry.EvictionPolicy.Lru:
            case Registry.EvictionPolicy.Lfu:
                if (Capacity <= 0) {
                    throw new InvalidOperationException($"{scope}: {nameof(Capacity)} must be greater than zero when {nameof(Eviction)} is {Eviction}.");
                }

                if (CompactionPercentage is not (> 0 and < 1)) {
                    throw new InvalidOperationException($"{scope}: {nameof(CompactionPercentage)} must be greater than 0 and less than 1.");
                }

                return;

            default:
                throw new InvalidOperationException($"{scope}: {nameof(Eviction)} '{Eviction}' is not supported.");
        }
    }
}
