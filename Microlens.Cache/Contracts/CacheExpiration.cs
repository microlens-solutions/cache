using Microlens.Cache.Shared;
using System;

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

    public static readonly CacheExpiration Never = new(null, null);

    [Obsolete("Use CacheExpiration.Never. 'Default' means 'never expires', not 'use the container default' (pass null for that). It will be removed in 2.0.0.")]
    public static readonly CacheExpiration Default = Never;

    public static CacheExpiration AfterWrite(TimeSpan absolute) => new(absolute, null);

    public static CacheExpiration AfterAccess(TimeSpan sliding) => new(null, sliding);
}
