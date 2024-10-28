using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions.Hosting.Startup;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Foundatio.Extensions.Hosting.Jobs;

public class ScheduledJobService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly JobManager _jobManager;
    private readonly TimeProvider _timeProvider;

    public ScheduledJobService(IServiceProvider serviceProvider, JobManager jobManager)
    {
        _serviceProvider = serviceProvider;
        _jobManager = jobManager;
        _timeProvider = _timeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startupContext = _serviceProvider.GetService<StartupActionsContext>();
        if (startupContext != null)
        {
            var result = await startupContext.WaitForStartupAsync(stoppingToken).AnyContext();
            if (!result.Success)
            {
                throw new ApplicationException("Failed to wait for startup actions to complete");
            }
        }

        // delay until right after next minute starts to sync with cron schedules
        await Task.Delay(TimeSpan.FromSeconds(60 - _timeProvider.GetUtcNow().UtcDateTime.Second));

        while (!stoppingToken.IsCancellationRequested)
        {
            var jobsToRun = new List<ScheduledJobRunner>();
            using (var activity = FoundatioDiagnostics.ActivitySource.StartActivity("Job Scheduler"))
            {
                foreach (var job in _jobManager.Jobs)
                    if (await job.ShouldRunAsync())
                        jobsToRun.Add(job);
            }

            foreach (var jobToRun in jobsToRun)
            {
                using var activity = FoundatioDiagnostics.ActivitySource.StartActivity("Job: " + jobToRun.Options.Name);

                await jobToRun.StartAsync(stoppingToken).AnyContext();
            }

            // shortest cron schedule is 1 minute so only check every minute
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).AnyContext();
        }
    }
}
