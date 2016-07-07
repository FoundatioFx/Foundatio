using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Utility;
using NCrontab;
using Topshelf;

namespace Foundatio.CronJob {
    public class CronService {
        private readonly List<ScheduledJobRunner> _jobs = new List<ScheduledJobRunner>();
        private readonly ICacheClient _cacheClient;
        private readonly ILoggerFactory _loggerFactory;

        public CronService(ICacheClient cacheClient, ILoggerFactory loggerFactory = null) {
            _cacheClient = cacheClient;
            _loggerFactory = loggerFactory;
        }

        public void Add<T>(T job, string schedule) where T : class, IJob {
            _jobs.Add(new ScheduledJobRunner(job, schedule, _cacheClient, _loggerFactory));
        }

        public void Add<T>(Func<T> jobFactory, string schedule) where T : class, IJob {
            _jobs.Add(new ScheduledJobRunner(jobFactory, schedule, _cacheClient, _loggerFactory));
        }

        public void Run() {
            var scheduler = new Scheduler(_jobs);
            scheduler.Start();
        }

        public void RunAsService() {
            var cancellationTokenSource = new CancellationTokenSource();

            HostFactory.Run(config => {
                config.Service<Scheduler>(s => {
                    s.ConstructUsing(name => new Scheduler(_jobs));
                    s.WhenStarted((service, control) => {
                        cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token, JobRunner.GetShutdownCancellationToken());
                        service.Start(cancellationTokenSource.Token);
                        return true;
                    });
                    s.WhenStopped((service, control) => {
                        cancellationTokenSource.Cancel();
                        service.Stop();
                        return true;
                    });
                });

                config.SetServiceName("CronJob");
                config.SetDisplayName("Foundatio CronJob");
                config.StartAutomatically();
                config.RunAsNetworkService();
            });
        }
    }

    public class ScheduledJobRunner {
        private readonly Func<IJob> _jobFactory;
        private readonly JobRunner _runner;
        private readonly CrontabSchedule _cronSchedule;
        private readonly ILockProvider _lockProvider;
        private readonly ILogger _logger;
        private readonly DateTime _baseDate = new DateTime(2000, 1, 1);
        private string _cacheKeyPrefix;

        public ScheduledJobRunner(IJob job, string schedule, ICacheClient cacheClient, ILoggerFactory loggerFactory = null)
            : this(() => job, schedule, cacheClient, loggerFactory) {}

        public ScheduledJobRunner(Func<IJob> jobFactory, string schedule, ICacheClient cacheClient, ILoggerFactory loggerFactory = null) {
            _jobFactory = jobFactory;
            Schedule = schedule;
            _logger = loggerFactory.CreateLogger<ScheduledJobRunner>();

            _runner = new JobRunner(jobFactory, loggerFactory, runContinuous: false);

            _cronSchedule = CrontabSchedule.TryParse(schedule, s => s, e => {
                var ex = e();
                _logger.Error(ex, $"Error parsing schedule {schedule}: {ex.Message}");
                return null;
            });

            if (_cronSchedule == null)
                throw new ArgumentException("Could not parse schedule.", nameof(schedule));

            var dates = _cronSchedule.GetNextOccurrences(SystemClock.UtcNow, DateTime.MaxValue).Take(2).ToList();
            var interval = TimeSpan.FromDays(1);
            if (dates.Count == 2)
                interval = dates[1].Subtract(dates[0]);

            _lockProvider = new ThrottlingLockProvider(cacheClient, 1, interval.Add(interval));
        }
        
        public string Schedule { get; private set; }
        public DateTime? LastRun { get; private set; }
        public Task RunTask { get; private set; }

        public Task<bool> StartIfScheduledAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            return StartIfScheduledAsync(SystemClock.UtcNow, cancellationToken);
        }

        internal async Task<bool> StartIfScheduledAsync(DateTime now, CancellationToken cancellationToken = default(CancellationToken)) {
            var nextDate = _cronSchedule.GetNextOccurrence(now.AddMinutes(-1));

            // not time yet
            if (nextDate > now)
                return false;

            // check if already run
            if (LastRun != null && LastRun.Value == nextDate)
                return false;

            return await _lockProvider.TryUsingAsync(GetLockKey(nextDate), t => {
                LastRun = nextDate;

                // start running the job in a thread
                RunTask = Task.Factory.StartNew(() => {
                    _runner.RunAsync(cancellationToken).GetAwaiter().GetResult();
                }, cancellationToken);

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

    internal class Scheduler {
        private readonly Timer _timer;
        private readonly List<ScheduledJobRunner> _jobs;
        private CancellationToken _cancellationToken;

        public Scheduler(List<ScheduledJobRunner> jobs) {
            _timer = new Timer(Run, null, Timeout.Infinite, Timeout.Infinite);
            _jobs = jobs;
        }

        private void Run(object state) {
            foreach (var job in _jobs)
                job.StartIfScheduledAsync(_cancellationToken).GetAwaiter().GetResult();
        }

        public void Start(CancellationToken cancellationToken = default(CancellationToken)) {
            _cancellationToken = cancellationToken;
            _timer.Change(0, 5000);
        }

        public void Stop() {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }
}
