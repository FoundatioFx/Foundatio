using System;
using System.Threading;
using System.Threading.Tasks;
using Cronos;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Extensions.Hosting.Jobs;

internal class ScheduledJobRunner
{
    private readonly Func<IServiceProvider, IJob> _jobFactory;
    private readonly IServiceProvider _serviceProvider;
    private CronExpression _cronSchedule;
    private readonly ILockProvider _lockProvider;
    private readonly ILogger _logger;
    private readonly DateTime _baseDate = new(2010, 1, 1);
    private string _cacheKeyPrefix;

    public ScheduledJobRunner(string schedule, string jobName, Func<IServiceProvider, IJob> jobFactory, IServiceProvider serviceProvider, ICacheClient cacheClient, ILoggerFactory loggerFactory = null)
    {
        _jobFactory = jobFactory;
        _serviceProvider = serviceProvider;
        Schedule = schedule;
        JobName = jobName;
        _logger = loggerFactory?.CreateLogger<ScheduledJobRunner>() ?? NullLogger<ScheduledJobRunner>.Instance;

        _cronSchedule = CronExpression.Parse(schedule);
        if (_cronSchedule == null)
            throw new ArgumentException("Could not parse schedule.", nameof(schedule));

        var interval = TimeSpan.FromDays(1);

        var nextOccurrence = _cronSchedule.GetNextOccurrence(SystemClock.UtcNow);
        if (nextOccurrence.HasValue)
        {
            var nextNextOccurrence = _cronSchedule.GetNextOccurrence(nextOccurrence.Value);
            if (nextNextOccurrence.HasValue)
                interval = nextNextOccurrence.Value.Subtract(nextOccurrence.Value);
        }

        _lockProvider = new ThrottlingLockProvider(cacheClient, 1, interval.Add(interval));

        NextRun = _cronSchedule.GetNextOccurrence(SystemClock.UtcNow);
    }

    private string _schedule;
    public string Schedule
    {
        get { return _schedule;}
        set
        {
            _cronSchedule = CronExpression.Parse(value);
            NextRun = _cronSchedule.GetNextOccurrence(SystemClock.UtcNow);
            _schedule = value;
        }
    }

    public string JobName { get; private set; }
    public DateTime? LastRun { get; private set; }
    public DateTime? NextRun { get; private set; }
    public Task RunTask { get; private set; }

    public bool ShouldRun()
    {
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

    public Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        // using lock provider in a cluster with a distributed cache implementation keeps cron jobs from running duplicates
        // TODO: provide ability to run cron jobs on a per host isolated schedule
        return _lockProvider.TryUsingAsync(GetLockKey(NextRun.Value), t =>
        {
            // start running the job in a thread
            RunTask = Task.Factory.StartNew(async () =>
            {
                var result = await _jobFactory(_serviceProvider).TryRunAsync(cancellationToken).AnyContext();
                // TODO: Should we only set last run on success? Seems like that could be bad.
                _logger.LogJobResult(result, JobName);
            }, cancellationToken).Unwrap();

            LastRun = NextRun;
            NextRun = _cronSchedule.GetNextOccurrence(SystemClock.UtcNow);

            return Task.CompletedTask;
        }, TimeSpan.Zero, TimeSpan.Zero);
    }

    private string GetLockKey(DateTime date)
    {
        _cacheKeyPrefix ??= TypeHelper.GetTypeDisplayName(_jobFactory(_serviceProvider).GetType());

        long minute = (long)date.Subtract(_baseDate).TotalMinutes;

        return _cacheKeyPrefix + minute;
    }
}
