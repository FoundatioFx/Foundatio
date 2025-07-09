using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Extensions;

public static class ServicesExtensions
{
    /// <summary>
    /// Replaces an existing service descriptor with a new singleton service of the specified type.
    /// </summary>
    /// <param name="services"></param>
    /// <typeparam name="TService"></typeparam>
    /// <typeparam name="TImplementation"></typeparam>
    /// <returns></returns>
    public static IServiceCollection ReplaceSingleton<TService, TImplementation>(this IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        // Remove the existing service descriptor
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(TService));
        if (descriptor != null)
        {
            services.Remove(descriptor);
        }

        // Add the new singleton service
        services.AddSingleton<TService, TImplementation>();

        return services;
    }

    /// <summary>
    /// Replaces an existing service descriptor with a new singleton service of the specified type.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="implementationFactory"></param>
    /// <typeparam name="TService"></typeparam>
    /// <returns></returns>
    public static IServiceCollection ReplaceSingleton<TService>(this IServiceCollection services, Func<IServiceProvider, TService> implementationFactory)
        where TService : class
    {
        // Remove the existing service descriptor
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(TService));
        if (descriptor != null)
        {
            services.Remove(descriptor);
        }

        // Add the new singleton service
        services.AddSingleton(implementationFactory);

        return services;
    }
}
