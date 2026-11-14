using System;
using System.Collections.Generic;
using System.Threading;
using Foundatio.Caching;

namespace Foundatio.Monitoring;

/// <summary>Everything added by every <c>AddFoundatioMonitoring</c> call, read when monitoring is first resolved.</summary>
internal sealed class MonitoringRegistrations
{
    private readonly Lazy<MonitoringOptions> _options;

    public MonitoringRegistrations()
    {
        _options = new Lazy<MonitoringOptions>(() =>
        {
            var options = new MonitoringOptions();
            foreach (var configure in ConfigureOptions)
                configure(options);

            options.Validate();
            return options;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public List<Action<MonitoringOptions>> ConfigureOptions { get; } = [];

    /// <summary>Creates the cache snapshots are shared through, or null to keep monitoring local to this process.</summary>
    public Func<IServiceProvider, ICacheClient>? CacheFactory { get; set; }

    /// <summary>Whether monitoring disposes the cache <see cref="CacheFactory"/> creates.</summary>
    public bool OwnsCache { get; set; }

    /// <summary>The options, built from <see cref="ConfigureOptions"/> on first use.</summary>
    public MonitoringOptions Options => _options.Value;
}
