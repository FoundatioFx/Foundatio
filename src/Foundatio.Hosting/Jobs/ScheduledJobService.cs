using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cronos;
using Foundatio.Caching;
using Foundatio.Hosting.Startup;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Hosting.Jobs {
    // https://github.com/kdcllc/CronScheduler.AspNetCore/blob/master/src/CronScheduler/SchedulerHostedService.cs
    public class ScheduledJobService : BackgroundService, IJobStatus {
        private readonly List<ScheduledJobRunner> _jobs;
        private readonly ICacheClient _cacheClient;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IServiceProvider _serviceProvider;

        public ScheduledJobService(IServiceProvider serviceProvider, ILoggerFactory loggerFactory) {
            _serviceProvider = serviceProvider;
            _cacheClient = serviceProvider.GetService<ICacheClient>() ?? new InMemoryCacheClient();
            _loggerFactory = loggerFactory;
            _jobs = new List<ScheduledJobRunner>(serviceProvider.GetServices<ScheduledJobRegistration>().Select(j => new ScheduledJobRunner(j.JobFactory, j.Schedule, _cacheClient, _loggerFactory)));

            var lifetime = serviceProvider.GetService<ShutdownHostIfNoJobsRunningService>();
            if (lifetime != null)
                lifetime.RegisterHostedJobInstance(this);
        }

        public bool IsRunning => true;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            var startupContext = _serviceProvider.GetRequiredService<StartupContext>();
            if (startupContext != null) {
                bool success = await startupContext.WaitForStartupAsync(stoppingToken).ConfigureAwait(false);
                if (!success)
                    throw new ApplicationException("Failed to wait for startup actions to complete.");
            }

            while (!stoppingToken.IsCancellationRequested) {
                var jobsToRun = _jobs.Where(j => j.ShouldRun()).ToList();

                foreach (var jobToRun in jobsToRun)
                    await jobToRun.StartAsync();

                // run jobs every minute, right after the minute changes
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }

        private class ScheduledJobRunner {
            private readonly Func<IJob> _jobFactory;
            private readonly CronExpression _cronSchedule;
            private readonly ILockProvider _lockProvider;
            private readonly ILogger _logger;
            private readonly DateTime _baseDate = new DateTime(2010, 1, 1);
            private string _cacheKeyPrefix;

            public ScheduledJobRunner(Func<IJob> jobFactory, string schedule, ICacheClient cacheClient, ILoggerFactory loggerFactory = null) {
                _jobFactory = jobFactory;
                Schedule = schedule;
                _logger = loggerFactory?.CreateLogger<ScheduledJobRunner>() ?? NullLogger<ScheduledJobRunner>.Instance;

                _cronSchedule = CronExpression.Parse(schedule);
                if (_cronSchedule == null)
                    throw new ArgumentException("Could not parse schedule.", nameof(schedule));

                var interval = TimeSpan.FromDays(1);

                var nextOccurrence = _cronSchedule.GetNextOccurrence(SystemClock.UtcNow);
                if (nextOccurrence.HasValue) {
                    var nextNextOccurrence = _cronSchedule.GetNextOccurrence(nextOccurrence.Value);
                    if (nextNextOccurrence.HasValue)
                        interval = nextNextOccurrence.Value.Subtract(nextOccurrence.Value);
                }

                _lockProvider = new ThrottlingLockProvider(cacheClient, 1, interval.Add(interval));

                NextRun = _cronSchedule.GetNextOccurrence(SystemClock.UtcNow);
            }

            public string Schedule { get; private set; }
            public DateTime? LastRun { get; private set; }
            public DateTime? NextRun { get; private set; }
            public Task RunTask { get; private set; }

            public bool ShouldRun() {
                if (!NextRun.HasValue)
                    return false;

                // not time yet
                if (NextRun > SystemClock.UtcNow)
                    return false;

                // check if already run
                if (LastRun != null && LastRun.Value == NextRun.Value)
                    return false;

                return true;
            }

            public async Task<bool> StartAsync(CancellationToken cancellationToken = default) {
                return await _lockProvider.TryUsingAsync(GetLockKey(NextRun.Value), t => {
                    // start running the job in a thread
                    RunTask = Task.Factory.StartNew(async () => {
                        var job = _jobFactory();
                        string jobName = job.GetType().Name;
                        var result = await _jobFactory().TryRunAsync(cancellationToken).ConfigureAwait(false);
                        _logger.LogJobResult(result, jobName);
                    }, cancellationToken);

                    LastRun = NextRun;
                    NextRun = _cronSchedule.GetNextOccurrence(SystemClock.UtcNow);

                    return Task.CompletedTask;
                }, TimeSpan.Zero, TimeSpan.Zero);
            }

            private string GetLockKey(DateTime date) {
                if (_cacheKeyPrefix == null)
                    _cacheKeyPrefix = TypeHelper.GetTypeDisplayName(_jobFactory().GetType());

                long minute = (long)date.Subtract(_baseDate).TotalMinutes;

                return _cacheKeyPrefix + minute;
            }
        }
    }

    public class ScheduledJobRegistration {
        public ScheduledJobRegistration(Func<IJob> jobFactory, string schedule) {
            JobFactory = jobFactory;
            Schedule = schedule;
        }

        public Func<IJob> JobFactory { get; }
        public string Schedule { get; }
    }
}
