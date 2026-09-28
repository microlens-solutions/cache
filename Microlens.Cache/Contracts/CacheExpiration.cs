using Microlens.Essentials.Guards;
using System;

namespace Microlens.Cache.Contracts;

public sealed class CacheExpiration(TimeSpan? absolute, TimeSpan? sliding) {
    public TimeSpan? Absolute { get; } = Guard.PositiveOrNull(absolute);

    public TimeSpan? Sliding { get; } = Guard.PositiveOrNull(sliding);

    public static readonly CacheExpiration Never = new(null, null);

    public static CacheExpiration AfterWrite(TimeSpan absolute) => new(absolute, null);

    public static CacheExpiration AfterAccess(TimeSpan sliding) => new(null, sliding);
}
