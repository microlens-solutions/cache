using System;
using System.Threading;
using System.Threading.Tasks;

namespace Microlens.Cache.Loaders;

internal readonly struct StateLoader<TState, TValue> : ILoader<TValue> {
    private readonly TState _state;

    private readonly Func<TState, CancellationToken, Task<TValue>> _factory;

    internal StateLoader(TState state, Func<TState, CancellationToken, Task<TValue>> factory) {
        _state = state;
        _factory = factory;
    }

    public bool Cancellable => true;

    public Task<TValue> Load(CancellationToken cancellationToken) {
        return _factory(_state, cancellationToken);
    }
}
