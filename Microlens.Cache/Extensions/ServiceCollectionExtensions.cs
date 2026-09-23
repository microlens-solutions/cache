using Microlens.Cache.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microlens.Cache.Extensions;

public static class ServiceCollectionExtensions {
    public static IServiceCollection AddMicrolensCache(this IServiceCollection services) {
        Guard.NotNull(services);
        services.TryAddSingleton<ICachingService, CachingService>();

        return services;
    }
}
