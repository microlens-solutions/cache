using System.Threading;
using System.Threading.Tasks;

namespace Microlens.Cache.Loaders;

internal interface ILoader<TValue> {
    bool Cancellable { get; }

    Task<TValue> Load(CancellationToken cancellationToken);
}
