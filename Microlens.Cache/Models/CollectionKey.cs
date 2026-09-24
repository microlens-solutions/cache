using System;

namespace Microlens.Cache.Models;

internal sealed class CollectionKey(string name) : IEquatable<CollectionKey> {
    private readonly string _name = name;

    public bool Equals(CollectionKey? other) {
        return other is not null && string.Equals(_name, other._name, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) {
        return obj is CollectionKey other && Equals(other);
    }

    public override int GetHashCode() {
        return StringComparer.Ordinal.GetHashCode(_name);
    }

    public override string ToString() => _name;
}
