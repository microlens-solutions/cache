using Microlens.Cache.Services;
using Microsoft.Extensions.DependencyInjection;
using Unity;
using Unity.Lifetime;

namespace Microlens.Cache.Extensions;

public static class ServiceCollectionExtensions {
    public static IServiceCollection AddMicrolensCache(this IServiceCollection services) {
        _ = services.AddSingleton<ICachingService, CachingService>();
        return services;
    }

    public static IUnityContainer AddMicrolensCache(this IUnityContainer container) {
        _ = container.RegisterType<ICachingService, CachingService>(new ContainerControlledLifetimeManager());
        return container;
    }
}
