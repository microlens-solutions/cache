using Microlens.Internal;
using System;
using System.Collections.Generic;

namespace Microlens.Cache.Options;

public sealed class CacheOptions {
    private readonly Dictionary<string, ContainerOptions> _containers = new(StringComparer.Ordinal);

    public ContainerOptions Defaults { get; } = new();

    public ContainerOptions Container(string name) {
        Guard.NotNull(name);

        if (!_containers.TryGetValue(name, out var options)) {
            options = new ContainerOptions();
            _containers.Add(name, options);
        }

        return options;
    }

    internal ContainerOptions Resolve(string name) {
        return _containers.TryGetValue(name, out var options) ? options : Defaults;
    }

    internal void Validate() {
        Defaults.Validate(nameof(Defaults));

        foreach (var pair in _containers) {
            pair.Value.Validate($"Container '{pair.Key}'");
        }
    }
}
