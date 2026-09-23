namespace Microlens.Cache.Services;

public readonly struct CacheExpiration {
    public CacheExpiration(TimeSpan? absolute, TimeSpan? sliding) {
        Guard.Positive(absolute);
        Guard.Positive(sliding);

        Absolute = absolute;
        Sliding = sliding;
    }

    // Lifetime measured from when the value is stored; the entry is removed afterwards regardless of access.
    public TimeSpan? Absolute { get; }

    // Idle window reset by every read; the entry is removed once unread for this long, and never outlives Absolute.
    public TimeSpan? Sliding { get; }

    public static CacheExpiration None => default;

    public static CacheExpiration AfterWrite(TimeSpan absolute) => new(absolute, null);

    public static CacheExpiration AfterAccess(TimeSpan sliding) => new(null, sliding);
}
