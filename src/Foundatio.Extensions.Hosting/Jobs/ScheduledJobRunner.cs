using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions.Hosting.Cronos;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Extensions.Hosting.Jobs;

internal class ScheduledJobRunner
{
    private readonly ScheduledJobOptions _jobOptions;
    private readonly IServiceProvider _serviceProvider;
    private readonly ICacheClient _cacheClient;
    private readonly TimeProvider _timeProvider;
    private CronExpression _cronSchedule;
    private readonly ILockProvider _lockProvider;
    private readonly ILogger _logger;
    private readonly DateTime _baseDate = new(2010, 1, 1);
    private DateTime _lastStatusUpdate = DateTime.MinValue;

    public ScheduledJobRunner(ScheduledJobOptions jobOptions, IServiceProvider serviceProvider, ICacheClient cacheClient, ILoggerFactory loggerFactory = null)
    {
        _jobOptions = jobOptions;
        _jobOptions.Name ??= Guid.NewGuid().ToString("N").Substring(0, 10);
        _serviceProvider = serviceProvider;
        _timeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        _cacheClient = new ScopedCacheClient(cacheClient, "jobs");
        _logger = loggerFactory?.CreateLogger<ScheduledJobRunner>() ?? NullLogger<ScheduledJobRunner>.Instance;

        _cronSchedule = CronExpression.Parse(_jobOptions.CronSchedule);
        if (_cronSchedule == null)
            throw new ArgumentException("Could not parse schedule.", nameof(ScheduledJobOptions.CronSchedule));

        var interval = TimeSpan.FromDays(1);

        var nextOccurrence = _cronSchedule.GetNextOccurrence(_timeProvider.GetUtcNow().UtcDateTime);
        if (nextOccurrence.HasValue)
        {
            var nextNextOccurrence = _cronSchedule.GetNextOccurrence(nextOccurrence.Value);
            if (nextNextOccurrence.HasValue)
                interval = nextNextOccurrence.Value.Subtract(nextOccurrence.Value);
        }

        _lockProvider = new ThrottlingLockProvider(_cacheClient, 1, interval.Add(interval));

        NextRun = _cronSchedule.GetNextOccurrence(_timeProvider.GetUtcNow().UtcDateTime);
    }

    public ScheduledJobOptions Options => _jobOptions;

    private string _schedule;
    public string Schedule
    {
        get { return _schedule; }
        set
        {
            _cronSchedule = CronExpression.Parse(value);
            NextRun = _cronSchedule.GetNextOccurrence(_timeProvider.GetUtcNow().UtcDateTime);
            _schedule = value;
        }
    }

    public DateTime? LastRun { get; private set; }
    public DateTime? LastSuccess { get; private set; }
    public string LastErrorMessage { get; private set; }
    public DateTime? NextRun { get; private set; }
    public Task RunTask { get; private set; }

    public async ValueTask<bool> ShouldRunAsync()
    {
        if (_timeProvider.GetUtcNow().UtcDateTime.Subtract(_lastStatusUpdate).TotalSeconds > 15)
        {
            var lastRun = await _cacheClient.GetAsync<DateTime>("lastrun:" + Options.Name).AnyContext();
            if (lastRun.HasValue)
            {
                LastRun = lastRun.Value;
                NextRun = _cronSchedule.GetNextOccurrence(LastRun.Value);
            }

            var lastSuccess = await _cacheClient.GetAsync<DateTime>("lastsuccess:" + Options.Name).AnyContext();
            if (lastSuccess.HasValue)
                LastSuccess = lastSuccess.Value;

            var lastError = await _cacheClient.GetAsync<string>("lasterror:" + Options.Name).AnyContext();
            if (lastError.HasValue)
                LastErrorMessage = lastError.Value;

            _lastStatusUpdate = _timeProvider.GetUtcNow().UtcDateTime;
        }

        if (!NextRun.HasValue)
            return false;

        // not time yet
        if (NextRun > _timeProvider.GetUtcNow().UtcDateTime)
            return false;

        // check if already run
        if (LastRun != null && LastRun.Value == NextRun.Value)
            return false;

        return true;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ILock l = new EmptyLock();
        if (Options.IsDistributed)
        {
            // using lock provider in a cluster with a distributed cache implementation keeps cron jobs from running duplicates
            l = await _lockProvider.AcquireAsync(GetLockKey(NextRun.Value), TimeSpan.FromMinutes(60), TimeSpan.Zero).AnyContext();
            if (l == null)
            {
                // if we didn't get the lock, update the last run time
                var lastRun = await _cacheClient.GetAsync<DateTime>("lastrun:" + Options.Name).AnyContext();
                if (lastRun.HasValue)
                    LastRun = lastRun.Value;

                var lastSuccess = await _cacheClient.GetAsync<DateTime>("lastsuccess:" + Options.Name).AnyContext();
                if (lastSuccess.HasValue)
                    LastSuccess = lastSuccess.Value;

                var lastError = await _cacheClient.GetAsync<string>("lasterror:" + Options.Name).AnyContext();
                if (lastError.HasValue)
                    LastErrorMessage = lastError.Value;

                return;
            }
        }

        await using (l)
        {
            // start running the job in a thread
            RunTask = Task.Factory.StartNew(async () =>
            {
                try
                {
                    using var activity = FoundatioDiagnostics.ActivitySource.StartActivity("Job " + Options.Name, ActivityKind.Server);
                    await using var scope = _serviceProvider.CreateAsyncScope();

                    var job = Options.JobFactory(scope.ServiceProvider);
                    var result = await job.TryRunAsync(cancellationToken).AnyContext();

                    _logger.LogJobResult(result, Options.Name);
                    if (result.IsSuccess)
                    {
                        LastSuccess = _timeProvider.GetUtcNow().UtcDateTime;
                        await _cacheClient.SetAsync("lastsuccess:" + Options.Name, LastSuccess.Value).AnyContext();
                    }
                    else
                    {
                        LastErrorMessage = result.Message;
                        await _cacheClient.SetAsync("lasterror:" + Options.Name, LastErrorMessage).AnyContext();
                    }
                }
                catch (TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                    LastErrorMessage = ex.Message;
                    await _cacheClient.SetAsync("lasterror:" + Options.Name, LastErrorMessage).AnyContext();
                }
            }, cancellationToken).Unwrap();

            LastRun = _timeProvider.GetUtcNow().UtcDateTime;
            await _cacheClient.SetAsync("lastrun:" + Options.Name, LastRun.Value).AnyContext();
            NextRun = _cronSchedule.GetNextOccurrence(LastRun.Value);
        }
    }

    private string GetLockKey(DateTime date)
    {
        long minute = (long)date.Subtract(_baseDate).TotalMinutes;

        return Options.Name + ":" + minute;
    }
}
