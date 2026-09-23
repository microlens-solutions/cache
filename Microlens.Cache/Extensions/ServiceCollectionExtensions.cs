using Microlens.Cache.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;

namespace Microlens.Cache.Extensions {
    public static class ServiceCollectionExtensions {
        public static IServiceCollection AddMicrolensCache(this IServiceCollection services) {
            if (services == null) {
                throw new ArgumentNullException(nameof(services));
            }

            services.TryAddSingleton<ICachingService, CachingService>();
            return services;
        }
    }
}
