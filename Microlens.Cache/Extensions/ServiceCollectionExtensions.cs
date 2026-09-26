using Microlens.Cache.Options;
using Microlens.Cache.Services;
using Microlens.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;

namespace Microlens.Cache.Extensions;

public static class ServiceCollectionExtensions {
    public static IServiceCollection AddMicrolensCache(this IServiceCollection services, Action<CacheOptions>? options = null) {
        Guard.NotNull(services);
        _ = services.AddOptions<CacheOptions>();

        if (options is not null) {
            _ = services.Configure(options);
        }

        services.TryAddSingleton<ICachingService, CachingService>();
        return services;
    }
}
