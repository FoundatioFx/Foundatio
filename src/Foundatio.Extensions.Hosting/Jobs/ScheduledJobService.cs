using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions.Hosting.Startup;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Extensions.Hosting.Jobs;

public class ScheduledJobService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly JobManager _jobManager;
    private readonly TimeProvider _timeProvider;
    private readonly ICacheClient _cacheClient;
    private readonly ILogger _logger;

    public ScheduledJobService(IServiceProvider serviceProvider, JobManager jobManager)
    {
        _serviceProvider = serviceProvider;
        _jobManager = jobManager;
        _timeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        _cacheClient = serviceProvider.GetService<ICacheClient>() ?? new InMemoryCacheClient(o => o.LoggerFactory(loggerFactory));;
        _logger = serviceProvider.GetService<ILogger<ScheduledJobService>>() ?? NullLogger<ScheduledJobService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startupContext = _serviceProvider.GetService<StartupActionsContext>();
        if (startupContext != null)
        {
            var result = await startupContext.WaitForStartupAsync(stoppingToken).AnyContext();
            if (!result.Success)
            {
                throw new StartupActionsException("Failed to wait for startup actions to complete");
            }
        }

        // delay until right after next minute starts to sync with cron schedules
        await Task.Delay(TimeSpan.FromSeconds(60 - _timeProvider.GetUtcNow().UtcDateTime.Second), stoppingToken);

        var jobsToRun = new List<ScheduledJobRunner>();

        while (!stoppingToken.IsCancellationRequested)
        {
            using (FoundatioDiagnostics.ActivitySource.StartActivity("Job Scheduler"))
            {
                var jobLastRuns = _jobManager.Jobs.ToDictionary(j => "lastrun:" + j.CacheKey, j => j);
                var jobLastRunTimes = await _cacheClient.GetAllAsync<DateTime>(jobLastRuns.Keys).AnyContext();

                foreach (var lastRun in jobLastRuns)
                {
                    var job = lastRun.Value;

                    if (jobLastRunTimes.TryGetValue(lastRun.Key, out var lastRunTime))
                    {
                        bool firstStatusCheck = job.LastRun == null;
                        job.LastRun = DateTime.SpecifyKind(lastRunTime.Value, DateTimeKind.Utc);

                        if (firstStatusCheck)
                        {
                            var lastSuccess = await _cacheClient.GetAsync<DateTime>("lastsuccess:" + job.CacheKey).AnyContext();
                            if (lastSuccess.HasValue)
                                job.LastSuccess = lastSuccess.Value;

                            var lastError = await _cacheClient.GetAsync<string>("lasterror:" + job.CacheKey).AnyContext();
                            if (lastError.HasValue)
                                job.LastErrorMessage = lastError.Value;
                        }
                    }

                    if (job.ShouldRun())
                        jobsToRun.Add(job);
                }
            }

            foreach (var jobToRun in jobsToRun)
            {
                using var activity = FoundatioDiagnostics.ActivitySource.StartActivity("Job: " + jobToRun.Options.Name);

                await jobToRun.StartAsync(stoppingToken).AnyContext();
            }

            jobsToRun.Clear();

            // shortest cron schedule is 1 minute so only check every minute
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).AnyContext();
        }
    }
}
