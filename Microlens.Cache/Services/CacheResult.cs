namespace Microlens.Cache.Services {
    public readonly struct CacheResult<TValue> {
        public CacheResult(TValue value) {
            Found = true;
            Value = value;
        }

        public bool Found { get; }

        public TValue Value { get; }
    }
}
