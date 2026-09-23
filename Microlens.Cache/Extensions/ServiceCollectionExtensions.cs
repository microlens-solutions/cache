using Microlens.Cache.Options;
using Microlens.Cache.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microlens.Cache.Extensions;

public static class ServiceCollectionExtensions {
    public static IServiceCollection AddMicrolensCache(this IServiceCollection services, Action<CachingOptions>? configure = null) {
        Guard.NotNull(services);

        // Configure actions compose across repeated calls; the service is registered once.
        _ = services.AddOptions<CachingOptions>();

        if (configure is not null) {
            _ = services.Configure(configure);
        }

        services.TryAddSingleton<ICachingService, CachingService>();

        return services;
    }
}
