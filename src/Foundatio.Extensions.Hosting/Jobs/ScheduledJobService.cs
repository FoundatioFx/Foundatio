using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cronos;
using Foundatio.Caching;
using Foundatio.Extensions.Hosting.Startup;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Extensions.Hosting.Jobs {
    public class ScheduledJobService : BackgroundService, IJobStatus {
        private readonly List<ScheduledJobRunner> _jobs;
        private readonly IServiceProvider _serviceProvider;

        public ScheduledJobService(IServiceProvider serviceProvider, ILoggerFactory loggerFactory) {
            _serviceProvider = serviceProvider;
            var cacheClient = serviceProvider.GetService<ICacheClient>() ?? new InMemoryCacheClient();
            _jobs = new List<ScheduledJobRunner>(serviceProvider.GetServices<ScheduledJobRegistration>().Select(j => new ScheduledJobRunner(j.JobFactory, j.Schedule, cacheClient, loggerFactory)));

            var lifetime = serviceProvider.GetService<ShutdownHostIfNoJobsRunningService>();
            lifetime?.RegisterHostedJobInstance(this);
        }

        public bool IsRunning { get; private set; } = true;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            // TODO: Add more logging throughout
            var startupContext = _serviceProvider.GetService<StartupActionsContext>();
            if (startupContext != null) {
                var result = await startupContext.WaitForStartupAsync(stoppingToken).AnyContext();
                if (!result.Success) {
                    IsRunning = false;
                    throw new ApplicationException("Failed to wait for startup actions to complete.");
                }
            }

            while (!stoppingToken.IsCancellationRequested) {
                var jobsToRun = _jobs.Where(j => j.ShouldRun()).ToArray();

                foreach (var jobToRun in jobsToRun)
                    await jobToRun.StartAsync(stoppingToken).AnyContext();

                // run jobs every minute since that is the lowest resolution of the cron schedule
                var now = SystemClock.Now;
                var nextMinute = now.AddTicks(TimeSpan.FromMinutes(1).Ticks - (now.Ticks % TimeSpan.FromMinutes(1).Ticks));
                var timeUntilNextMinute = nextMinute.Subtract(SystemClock.Now).Add(TimeSpan.FromMilliseconds(1));
                await Task.Delay(timeUntilNextMinute, stoppingToken).AnyContext();
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
                // using lock provider in a cluster with a distributed cache implementation keeps cron jobs from running duplicates
                // TODO: provide ability to run cron jobs on a per host isolated schedule
                return await _lockProvider.TryUsingAsync(GetLockKey(NextRun.Value), t => {
                    // start running the job in a thread
                    RunTask = Task.Factory.StartNew(async () => {
                        var job = _jobFactory();
                        // TODO: Don't calculate job name every time
                        string jobName = job.GetType().Name;
                        var result = await _jobFactory().TryRunAsync(cancellationToken).AnyContext();
                        // TODO: Should we only set last run on success? Seems like that could be bad.
                        _logger.LogJobResult(result, jobName);
                    }, cancellationToken).Unwrap();

                    LastRun = NextRun;
                    NextRun = _cronSchedule.GetNextOccurrence(SystemClock.UtcNow);

                    return Task.CompletedTask;
                }, TimeSpan.Zero, TimeSpan.Zero).AnyContext();
            }

            private string GetLockKey(DateTime date) {
                if (_cacheKeyPrefix == null)
                    _cacheKeyPrefix = TypeHelper.GetTypeDisplayName(_jobFactory().GetType());

                long minute = (long)date.Subtract(_baseDate).TotalMinutes;

                return _cacheKeyPrefix + minute;
            }
        }
    }
}
