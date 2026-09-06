using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions.Hosting.Messaging;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Foundatio;

/// <summary>Last observed infrastructure health and capacity of the hosted runtime.</summary>
public sealed class FoundatioRuntimeHealth : IDisposable
{
    private readonly ConcurrentDictionary<string, string?> _failures = new(StringComparer.Ordinal);
    private readonly Meter _meter = new("Foundatio.Runtime");
    private JobRuntimeStoreStats? _capacity;
    private long _scheduledDispatches = -1;

    public FoundatioRuntimeHealth()
    {
        _meter.CreateObservableGauge("foundatio.jobs.active", () => Volatile.Read(ref _capacity)?.ActiveJobs ?? 0);
        _meter.CreateObservableGauge("foundatio.jobs.history", () => Volatile.Read(ref _capacity)?.HistoryJobs ?? 0);
        _meter.CreateObservableGauge("foundatio.jobs.idempotency_records", () => Volatile.Read(ref _capacity)?.DeduplicationRecords ?? 0);
        _meter.CreateObservableGauge("foundatio.messaging.scheduled_dispatches", () => ScheduledDispatches ?? Volatile.Read(ref _capacity)?.ScheduledDispatches ?? 0);
    }

    public IReadOnlyDictionary<string, string?> Components => new Dictionary<string, string?>(_failures);
    public long? ScheduledDispatches => Interlocked.Read(ref _scheduledDispatches) is >= 0 and var count ? count : null;
    internal void UpdateScheduledDispatches(long count) => Interlocked.Exchange(ref _scheduledDispatches, count);
    public JobRuntimeStoreStats? Capacity => Volatile.Read(ref _capacity);
    internal void Healthy(string component) => _failures[component] = null;
    internal void Failed(string component, Exception error) => _failures[component] = error.Message;
    internal void UpdateCapacity(JobRuntimeStoreStats capacity) => Volatile.Write(ref _capacity, capacity);
    public void Dispose() => _meter.Dispose();
}

internal sealed class FoundatioHealthCheck(IServiceProvider services, FoundatioRuntimeHealth runtime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var failures = runtime.Components.Where(p => p.Value is not null).Select(p => $"{p.Key}: {p.Value}").ToList();
        if (services.GetService<IJobWorker>() is { IsHealthy: false }) failures.Add("Job worker is recovering.");
        foreach (var host in services.GetServices<IHostedService>().OfType<MessageHandlerHostedService>())
            failures.AddRange(host.Subscriptions.Where(s => s.Status != MessageSubscriptionStatus.Healthy).Select(s => $"{s.Source}: {s.Status}"));
        return Task.FromResult(failures.Count == 0 ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy(String.Join("; ", failures)));
    }
}
