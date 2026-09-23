namespace Microlens.Cache.Services;

public readonly struct CacheResult<TValue>(TValue value) {
    public bool Found { get; } = true;

    public TValue? Value { get; } = value;
}
