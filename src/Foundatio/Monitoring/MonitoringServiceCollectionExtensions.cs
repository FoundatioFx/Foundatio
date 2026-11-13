using System;
using System.Linq;
using Foundatio.Monitoring;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Foundatio monitoring.</summary>
public static class MonitoringServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="IWorkMonitor"/>, which shows the messages and jobs being handled, recent outcomes, rolling stats,
    /// queue backlog and job counts. Every process tracks its own work whether or not this is called; with
    /// <see cref="FoundatioMonitoringBuilder.UseCache()"/>, each instance that handles messages or runs jobs also writes
    /// a snapshot to the cache, so the monitor shows them all. Call as many times as you like; settings accumulate.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddFoundatioMonitoring(m => m
    ///     .UseCache()
    ///     .Configure(o => o.ActiveThreshold = TimeSpan.FromSeconds(2)));
    /// </code>
    /// </example>
    public static IServiceCollection AddFoundatioMonitoring(this IServiceCollection services, Action<FoundatioMonitoringBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var registrations = services.FirstOrDefault(d => d.ServiceType == typeof(MonitoringRegistrations))?.ImplementationInstance as MonitoringRegistrations;
        if (registrations is null)
        {
            registrations = new MonitoringRegistrations();
            services.AddSingleton(registrations);
            services.TryAddSingleton(WorkTracker.Create);
            services.TryAddSingleton(sp => new MonitoringRuntime(sp, registrations));
            services.TryAddSingleton<IWorkMonitor>(sp => new WorkMonitor(sp.GetRequiredService<MonitoringRuntime>(), sp));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SnapshotPublisher>());
        }

        configure?.Invoke(new FoundatioMonitoringBuilder(services, registrations));
        return services;
    }
}
