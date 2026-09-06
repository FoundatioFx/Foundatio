using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System;
using Foundatio.Jobs;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Extensions.Hosting.Jobs;

/// <summary>Registers declared schedules at startup and materializes due work without executing jobs.</summary>
internal sealed class JobSchedulerService(IServiceProvider services, ILogger<JobSchedulerService> logger, FoundatioRuntimeHealth health) : BackgroundService
{
    private JobScheduleProcessor? _processor;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IJobRuntimeStore>() is null)
            throw new InvalidOperationException("A job scheduler was registered but no runtime store is configured, so jobs would never run. Call AddFoundatio().Jobs.UseInMemory() or UseRuntimeStore(...).");
        _processor = services.GetRequiredService<JobScheduleProcessor>();
        var store = services.GetRequiredService<IScheduledJobStore>();
        foreach (var definition in services.GetServices<ScheduledJobDefinition>())
        {
            if (definition.Scope == ScheduledJobScope.PerNode)
                NodeIdentity.RequireStable(services.GetService<JobWorkerOptions>()?.NodeId);
            await store.ReconcileAsync(definition, cancellationToken).AnyContext();
        }
        await base.StartAsync(cancellationToken).AnyContext();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _processor!.EnqueueDueOccurrencesAsync(stoppingToken).AnyContext();
                health.Healthy("scheduler");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).AnyContext();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                health.Failed("scheduler", ex);
                logger.LogError(ex, "Error creating scheduled job occurrences");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).AnyContext();
            }
        }
    }
}
