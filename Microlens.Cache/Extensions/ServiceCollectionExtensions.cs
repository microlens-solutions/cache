using Microlens.Cache.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Microlens.Cache.Extensions {
    public static class ServiceCollectionExtensions {
        public static IServiceCollection AddMicrolensCache(this IServiceCollection services) {
            _ = services.AddSingleton<ICachingService, CachingService>();
            return services;
        }
    }
}
