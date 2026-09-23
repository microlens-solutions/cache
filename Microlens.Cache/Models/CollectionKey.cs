namespace Microlens.Cache.Models;

internal sealed class CollectionKey(string name) {
    public override string ToString() => name;
}
