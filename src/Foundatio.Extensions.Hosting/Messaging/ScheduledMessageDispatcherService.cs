using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Extensions.Hosting.Messaging;

internal sealed class ScheduledMessageDispatcherService(ScheduledMessageDispatcher dispatcher, ILogger<ScheduledMessageDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int dispatched = await dispatcher.DispatchDueAsync(cancellationToken: stoppingToken).AnyContext();
                if (dispatched == 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).AnyContext();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error dispatching scheduled messages");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).AnyContext();
            }
        }
    }
}
