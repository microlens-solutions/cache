namespace Microlens.Cache.Shared;

public static class CacheRegistry {
    public enum EvictionPolicy {
        None,

        Lru,

        Lfu
    }
}
