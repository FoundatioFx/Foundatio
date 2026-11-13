using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Monitoring;

/// <summary>
/// This process as monitoring sees it: its identity, its tracker, and the cache its snapshots go through. Shared by
/// <see cref="SnapshotPublisher"/> and <see cref="WorkMonitor"/>.
/// </summary>
internal sealed class MonitoringRuntime : IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly MonitoringRegistrations _registrations;
    private readonly Lazy<ICacheClient?> _cache;
    private readonly Lazy<string> _serviceName;

    public MonitoringRuntime(IServiceProvider services, MonitoringRegistrations registrations)
    {
        _services = services;
        _registrations = registrations;
        Options = registrations.Options;
        Tracker = services.GetRequiredService<WorkTracker>();
        TimeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
        LoggerFactory = services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        StartedAt = TimeProvider.GetUtcNow();
        MachineName = Environment.MachineName;
        ProcessId = Environment.ProcessId;
        InstanceId = $"{MachineName}:{ProcessId}:{Guid.NewGuid().ToString("N")[..6]}";
        _cache = new Lazy<ICacheClient?>(() => registrations.CacheFactory?.Invoke(services), LazyThreadSafetyMode.ExecutionAndPublication);
        _serviceName = new Lazy<string>(ResolveServiceName, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public MonitoringOptions Options { get; }
    public WorkTracker Tracker { get; }
    public TimeProvider TimeProvider { get; }
    public ILoggerFactory LoggerFactory { get; }
    public string InstanceId { get; }
    public string MachineName { get; }
    public int ProcessId { get; }
    public DateTimeOffset StartedAt { get; }

    /// <summary>Whether snapshots are shared through a cache.</summary>
    public bool HasCache => _registrations.CacheFactory is not null;

    /// <summary>The cache, created on first use; null without one.</summary>
    public ICacheClient? Cache => _cache.Value;

    public string IndexKey => $"{Options.KeyPrefix}:instances";

    public string GetInstanceKey(string instanceId) => $"{Options.KeyPrefix}:instance:{instanceId}";

    /// <summary>Whether this process handles any messages or runs any jobs, and so belongs in the monitor.</summary>
    public bool HasWork =>
        _services.GetService<MessagingRegistrations>()?.Handlers.Count > 0 || _services.GetService<JobsRegistrations>()?.Jobs.Count > 0;

    public InstanceSnapshot CreateSnapshot()
    {
        var work = Tracker.GetSnapshot();
        var sources = _services.GetService<MessagingRegistrations>()?.Handlers.Count > 0 && _services.GetService<MessagingRuntime>() is { } messaging
            ? messaging.GetProcessors().Select(p => p.Source.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
            : [];
        var jobNames = _services.GetService<JobsRegistrations>()?.Jobs.Keys.Order(StringComparer.Ordinal).ToList() ?? [];

        return new InstanceSnapshot
        {
            InstanceId = InstanceId,
            ServiceName = _serviceName.Value,
            MachineName = MachineName,
            ProcessId = ProcessId,
            StartedAt = StartedAt,
            UpdatedAt = TimeProvider.GetUtcNow(),
            MessageSources = sources,
            JobNames = jobNames,
            Active = work.Active,
            Recent = work.Recent,
            Stats = work.Stats,
            StatsWindow = work.StatsWindow
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (!_registrations.OwnsCache || !_cache.IsValueCreated)
            return;

        switch (_cache.Value)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().AnyContext();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private string ResolveServiceName()
    {
        if (Options.ServiceName is { } serviceName)
            return serviceName;

        if (_services.GetService<MessagingRegistrations>()?.Handlers.Count > 0 && _services.GetService<MessagingRuntime>() is { } messaging)
            return messaging.ServiceName;

        return _services.GetService<IHostEnvironment>()?.ApplicationName
            ?? Assembly.GetEntryAssembly()?.GetName().Name
            ?? "foundatio";
    }
}
