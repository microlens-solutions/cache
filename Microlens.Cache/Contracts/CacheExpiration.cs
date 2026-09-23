using Microlens.Cache.Shared;

namespace Microlens.Cache.Contracts;

public class CacheExpiration {
    public CacheExpiration(TimeSpan? absolute, TimeSpan? sliding) {
        Guard.Positive(absolute);
        Guard.Positive(sliding);

        Absolute = absolute;
        Sliding = sliding;
    }

    public TimeSpan? Absolute { get; }

    public TimeSpan? Sliding { get; }

    public static readonly CacheExpiration Default = new(null, null);

    public static CacheExpiration AfterWrite(TimeSpan absolute) => new(absolute, null);

    public static CacheExpiration AfterAccess(TimeSpan sliding) => new(null, sliding);
}
