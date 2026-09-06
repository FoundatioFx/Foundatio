using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Foundatio.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Extensions.Hosting.Messaging;

internal sealed class ScheduledMessageDispatcherService(ScheduledMessageDispatcher dispatcher, ILogger<ScheduledMessageDispatcherService> logger, FoundatioRuntimeHealth health, IServiceProvider services) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextStats = DateTimeOffset.MinValue;
        var store = services.GetService<IScheduledDispatchStore>() as IJobRuntimeStore ?? services.GetService<IJobRuntimeStore>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (store is not null && DateTimeOffset.UtcNow >= nextStats)
                {
                    health.UpdateScheduledDispatches((await store.GetStatsAsync(stoppingToken).AnyContext()).ScheduledDispatches);
                    nextStats = DateTimeOffset.UtcNow.AddMinutes(1);
                }
                int dispatched = await dispatcher.DispatchDueAsync(cancellationToken: stoppingToken).AnyContext();
                if (dispatcher.LastFailure is { } failure) health.Failed("dispatcher", failure);
                else health.Healthy("dispatcher");
                if (dispatched == 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).AnyContext();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                health.Failed("dispatcher", ex);
                logger.LogError(ex, "Error dispatching scheduled messages");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).AnyContext();
            }
        }
    }
}
