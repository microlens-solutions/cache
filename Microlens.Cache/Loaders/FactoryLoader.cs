using System;
using System.Threading;
using System.Threading.Tasks;

namespace Microlens.Cache.Loaders;

internal class FactoryLoader<TValue> : ILoader<TValue> {
    private readonly Func<Task<TValue>> _factory;

    internal FactoryLoader(Func<Task<TValue>> factory) {
        _factory = factory;
    }

    public bool Cancellable => false;

    public Task<TValue> Load(CancellationToken cancellationToken) {
        return _factory();
    }
}
