using System;
using Foundatio.Caching;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Monitoring;

/// <summary>Configures Foundatio monitoring. Every call to <c>AddFoundatioMonitoring</c> adds to the same configuration.</summary>
public sealed class FoundatioMonitoringBuilder
{
    private readonly MonitoringRegistrations _registrations;

    internal FoundatioMonitoringBuilder(IServiceCollection services, MonitoringRegistrations registrations)
    {
        Services = services;
        _registrations = registrations;
    }

    /// <summary>The service collection being configured.</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Shares snapshots through the <see cref="ICacheClient"/> registered in the service provider, so
    /// <see cref="IWorkMonitor"/> shows every instance that uses the same cache. Without a cache it shows only this one.
    /// </summary>
    /// <remarks>
    /// Use a cache every instance reaches, such as Redis. A <see cref="HybridCacheClient"/> or an in-memory cache shows
    /// stale or partial data, because each instance reads its own local copy.
    /// </remarks>
    public FoundatioMonitoringBuilder UseCache()
    {
        _registrations.CacheFactory = sp => sp.GetService<ICacheClient>()
            ?? throw new InvalidOperationException("Monitoring is set to share snapshots through the ICacheClient in the service provider, but none is registered. Register one, or pass a cache to UseCache.");
        _registrations.OwnsCache = false;
        return this;
    }

    /// <summary>Shares snapshots through a cache you create and dispose yourself.</summary>
    /// <inheritdoc cref="UseCache()" path="/remarks"/>
    public FoundatioMonitoringBuilder UseCache(ICacheClient cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _registrations.CacheFactory = _ => cache;
        _registrations.OwnsCache = false;
        return this;
    }

    /// <summary>Shares snapshots through a cache that monitoring creates when it starts, and disposes.</summary>
    /// <inheritdoc cref="UseCache()" path="/remarks"/>
    public FoundatioMonitoringBuilder UseCache(Func<IServiceProvider, ICacheClient> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _registrations.CacheFactory = factory;
        _registrations.OwnsCache = true;
        return this;
    }

    /// <summary>Changes <see cref="MonitoringOptions"/>. Callbacks run in the order they were added, when monitoring is first resolved.</summary>
    public FoundatioMonitoringBuilder Configure(Action<MonitoringOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _registrations.ConfigureOptions.Add(configure);
        return this;
    }
}
