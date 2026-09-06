using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Extensions.Hosting.Jobs;

/// <summary>Executes registered job types independently of schedule creation and message dispatch.</summary>
internal sealed class JobWorkerService(IJobWorker worker, IJobRuntimeStore store, ILogger<JobWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCleanup = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextCleanup)
                {
                    int removed = await store.CleanupAsync(cancellationToken: stoppingToken).AnyContext();
                    nextCleanup = DateTimeOffset.UtcNow.Add(removed == 1000 ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1));
                }
                int executed = await worker.RunQueuedAsync(cancellationToken: stoppingToken).AnyContext();
                if (executed == 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).AnyContext();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error running queued jobs");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).AnyContext();
            }
        }
    }
}
