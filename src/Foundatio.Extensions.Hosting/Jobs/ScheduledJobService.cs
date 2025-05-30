using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions.Hosting.Startup;
using Foundatio.Messaging;
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
    private readonly IMessageBus _messageBus;

    public ScheduledJobService(IServiceProvider serviceProvider, JobManager jobManager)
    {
        _serviceProvider = serviceProvider;
        _jobManager = jobManager;
        _timeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        _cacheClient = serviceProvider.GetService<ICacheClient>() ?? new InMemoryCacheClient(o => o.LoggerFactory(loggerFactory));
        _messageBus = serviceProvider.GetService<IMessageBus>() ?? new NullMessageBus();
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

        await _messageBus.SubscribeAsync<JobStateChangedMessage>(s =>
        {
            var job = _jobManager.Jobs.FirstOrDefault(j => j.Options.Name == s.JobName);
            if (job == null || s.Id == job.Id)
                return Task.CompletedTask;

            if (!String.IsNullOrEmpty(s.Reason))
                _logger.LogInformation("Received job state change for {JobName} with Id {Id} {JobId} Reason: {Reason}", s.JobName, s.Id, job.Id, s.Reason);
            else
                _logger.LogDebug("Received job state change for {JobName} with Id {Id} {JobId}", s.JobName, s.Id, job.Id);

            job.Options.CronSchedule = s.CronSchedule;
            job.Options.IsEnabled = s.IsEnabled;
            job.IsRunning = s.IsRunning;
            job.LastRun = s.LastRun;
            job.LastSuccess = s.LastSuccess;
            job.LastErrorMessage = s.LastErrorMessage;
            job.LastStateSync = _timeProvider.GetUtcNow().UtcDateTime;
            job.NextRun = job.GetNextScheduledRun();

            return Task.CompletedTask;
        }, cancellationToken: stoppingToken);

        // apply initial distributed job states
        try
        {
            var distributedJobs = _jobManager.Jobs.Where(j => j.Options.IsDistributed).ToDictionary(j => j.CacheKey + ":state", j => j);
            var distributedJobStates = await _cacheClient.GetAllAsync<JobInstanceState>(distributedJobs.Keys).AnyContext();

            foreach (var distributedJob in distributedJobs)
            {
                var job = distributedJob.Value;

                if (!distributedJobStates.TryGetValue(distributedJob.Key, out var jobState) || !jobState.HasValue)
                    continue;

                job.LastStateSync = _timeProvider.GetUtcNow().UtcDateTime;
                await job.ApplyDistributedStateAsync(jobState.Value).AnyContext();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying initial distributed job states: {Message}", ex.Message);
        }

        // delay until right after next minute starts to sync with cron schedules
        await Task.Delay(TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(_timeProvider.GetUtcNow().Second) - TimeSpan.FromMilliseconds(_timeProvider.GetUtcNow().Millisecond), stoppingToken).AnyContext();

        while (!stoppingToken.IsCancellationRequested)
        {
            using (FoundatioDiagnostics.ActivitySource.StartActivity("Job Scheduler"))
            {
                try
                {
                    var jobNextRuns = _jobManager.Jobs.ToDictionary(j => j.CacheKey + ":nextrun", j => j);
                    var jobNextRunTimes = await _cacheClient.GetAllAsync<DateTime>(jobNextRuns.Keys).AnyContext();

                    foreach ((string nextRunKey, ScheduledJobInstance job) in jobNextRuns)
                    {
                        if (jobNextRunTimes.TryGetValue(nextRunKey, out var nextRunTime) && nextRunTime.HasValue)
                        {
                            if (!nextRunTime.IsNull)
                                job.NextRun = DateTime.SpecifyKind(nextRunTime.Value, DateTimeKind.Utc);
                            else
                                job.NextRun = null;
                        }

                        job.NextRun ??= job.GetNextScheduledRun();
                    }
                } catch (Exception ex)
                {
                    _logger.LogError(ex, "Error retrieving job next run times: {Message}", ex.Message);
                }
            }

            foreach (var jobToRun in _jobManager.Jobs.Where(j => j.ShouldRun()))
            {
                using var activity = FoundatioDiagnostics.ActivitySource.StartActivity("Job: " + jobToRun.Options.Name);

                await jobToRun.StartAsync(stoppingToken).AnyContext();
            }

            // shortest cron schedule is 1 minute so only check every minute
            await Task.Delay(TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(_timeProvider.GetUtcNow().Second) - TimeSpan.FromMilliseconds(_timeProvider.GetUtcNow().Millisecond), stoppingToken).AnyContext();
        }
    }
}
