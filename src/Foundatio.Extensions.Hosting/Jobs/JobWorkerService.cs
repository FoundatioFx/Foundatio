using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Extensions.Hosting.Jobs;

/// <summary>Executes jobs and maintains retention independently of long-running executions.</summary>
internal sealed class JobWorkerService(IJobWorker worker, IJobRuntimeStore store, ILogger<JobWorkerService> logger, FoundatioRuntimeHealth health) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(worker.RunContinuouslyAsync(stoppingToken), CleanupAsync(stoppingToken));

    private async Task CleanupAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMinutes(1);
            try
            {
                health.UpdateCapacity(await store.GetStatsAsync(stoppingToken).AnyContext());
                health.Healthy("job-store");
                if (await store.CleanupAsync(cancellationToken: stoppingToken).AnyContext() == 1000)
                    delay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                health.Failed("job-store", ex);
                logger.LogError(ex, "Error cleaning up job history");
            }
            try
            {
                await Task.Delay(delay, stoppingToken).AnyContext();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
