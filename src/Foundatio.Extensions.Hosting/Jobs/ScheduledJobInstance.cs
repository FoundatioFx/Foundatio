using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions.Hosting.Cronos;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Extensions.Hosting.Jobs;

internal class ScheduledJobInstance
{
    private readonly ScheduledJobOptions _jobOptions;
    private readonly IServiceProvider _serviceProvider;
    private readonly ICacheClient _cacheClient;
    private readonly IMessageBus _messageBus;
    private readonly TimeProvider _timeProvider;
    private CronExpression _cronExpression;
    private readonly ILockProvider _lockProvider;
    private readonly ILogger _logger;
    private readonly DateTime _baseDate = new(2010, 1, 1);

    public ScheduledJobInstance(ScheduledJobOptions jobOptions, IServiceProvider serviceProvider, ICacheClient cacheClient, ILoggerFactory loggerFactory = null)
    {
        _jobOptions = jobOptions;
        _jobOptions.Name ??= Guid.NewGuid().ToString("N").Substring(0, 10);
        CacheKey = _jobOptions.Name.ToLower().Replace(' ', '_');
        _serviceProvider = serviceProvider;
        _timeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        _cacheClient = new ScopedCacheClient(cacheClient, "jobs");
        _logger = loggerFactory?.CreateLogger<ScheduledJobInstance>() ?? NullLogger<ScheduledJobInstance>.Instance;

        Id = Guid.NewGuid().ToString("N").Substring(0, 10);

        UpdateCronExpression();

        _messageBus = serviceProvider.GetService<IMessageBus>() ?? new InMemoryMessageBus();
        _lockProvider = new CacheLockProvider(cacheClient, _messageBus, loggerFactory);

        _jobOptions.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ScheduledJobOptions.CronSchedule))
            {
                UpdateCronExpression();

                NextRun = GetNextScheduledRun();

                Task.Run(() => UpdateDistributedStateAsync(true, "Cron schedule changed"));
            }

            if (args.PropertyName == nameof(ScheduledJobOptions.IsEnabled))
            {
                NextRun = GetNextScheduledRun();

                Task.Run(() => UpdateDistributedStateAsync(true, "Enabled changed"));
            }
        };
    }

    private void UpdateCronExpression()
    {
        if (String.IsNullOrEmpty(_jobOptions.CronSchedule))
        {
            _cronExpression = null;
            return;
        }

        try
        {
            _cronExpression = CronExpression.Parse(_jobOptions.CronSchedule);
        }
        catch (Exception)
        {
            _logger.LogError("Failed to parse cron expression: {CronSchedule}", _jobOptions.CronSchedule);
            _cronExpression = null;
        }
    }

    public string Id { get; }

    public ScheduledJobOptions Options => _jobOptions;

    public DateTime? LastStateSync { get; internal set; }
    public bool IsRunning { get; internal set; }
    public DateTime? NextRun { get; internal set; }
    public DateTime? LastRun { get; internal set; }
    public DateTime? LastSuccess { get; internal set; }
    public TimeSpan? LastDuration { get; internal set; }
    public string LastErrorMessage { get; internal set; }

    public Task RunTask { get; private set; }

    internal string CacheKey { get; }

    public DateTime? GetNextScheduledRun()
    {
        if (Options.IsEnabled == false || _cronExpression == null)
            return null;

        var lastRun = LastRun ?? _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-5);
        var nextRun = _cronExpression.GetNextOccurrence(lastRun, _jobOptions.CronTimeZone ?? TimeZoneInfo.Local);
        if (nextRun == null)
            return null;

        if (nextRun < _timeProvider.GetUtcNow().UtcDateTime)
        {
            var futureRun = _cronExpression.GetNextOccurrence(_timeProvider.GetUtcNow().UtcDateTime, _jobOptions.CronTimeZone ?? TimeZoneInfo.Local);

            // if next run is more than an hour in the past, use the future run
            if (_timeProvider.GetUtcNow().UtcDateTime.Subtract(nextRun.Value) > TimeSpan.FromHours(1))
                nextRun = futureRun;

            // if the next run is within 10 minutes, use it
            if (futureRun.HasValue && futureRun.Value.Subtract(_timeProvider.GetUtcNow().UtcDateTime) < TimeSpan.FromMinutes(10))
                nextRun = futureRun;
        }

        return nextRun;
    }

    internal bool ShouldRun()
    {
        if (!Options.IsEnabled)
            return false;

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

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return StartAsync(false, cancellationToken);
    }

    public async Task StartAsync(bool isManual, CancellationToken cancellationToken = default)
    {
        var scheduledTime = isManual ? _baseDate : NextRun!.Value;

        ILock jobRunningLock = new EmptyLock();
        ILock scheduledTimeLock = new EmptyLock();
        if (Options.IsDistributed)
        {
            // using lock provider in a cluster with a distributed cache implementation keeps cron jobs from running duplicates
            try
            {
                // hold this lock for 2 hours to prevent duplicates
                scheduledTimeLock = await _lockProvider.AcquireAsync(GetLockKey(scheduledTime), TimeSpan.FromHours(2), TimeSpan.Zero).AnyContext();

                if (scheduledTimeLock != null)
                {
                    // hold this lock while the job is running to prevent multiple instances of the job running at the same time
                    jobRunningLock = await _lockProvider.AcquireAsync(CacheKey, TimeSpan.FromMinutes(30), TimeSpan.Zero).AnyContext();

                    if (jobRunningLock == null)
                        await scheduledTimeLock.ReleaseAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error acquiring locks for job ({JobName})", Options.Name);
                scheduledTimeLock = null;
                jobRunningLock = null;
            }

            if (isManual && (scheduledTimeLock == null || jobRunningLock == null))
                _logger.LogWarning("Job ({JobName}) is already running, skipping manual request", Options.Name);
            else if (jobRunningLock == null || scheduledTimeLock == null)
                _logger.LogDebug("Job ({JobName}) scheduled on another instance", Options.Name);

            if (scheduledTimeLock == null || jobRunningLock == null)
            {
                // sync distributed state
                await ApplyDistributedStateAsync();

                return;
            }
        }

        // start running the job in a thread
        RunTask = Task.Factory.StartNew(async () =>
        {
            await using (jobRunningLock)
            {
                try
                {
                    _logger.LogInformation("{JobType} {JobName} ({JobId}) starting for time: {ScheduledTime}", Options.IsDistributed ? "Distributed job" : "Job", Options.Name,
                        Id, isManual ? "Manual" : NextRun!.Value.ToString("t"));

                    await using var scope = _serviceProvider.CreateAsyncScope();

                    var job = Options.JobFactory(scope.ServiceProvider);

                    IsRunning = true;
                    LastRun = isManual ? _timeProvider.GetUtcNow().UtcDateTime : NextRun;
                    NextRun = GetNextScheduledRun();

                    await UpdateDistributedStateAsync(true).AnyContext();

                    var sw = Stopwatch.StartNew();
                    var result = await job.TryRunAsync(cancellationToken).AnyContext();
                    sw.Stop();
                    LastDuration = sw.Elapsed;

                    _logger.LogJobResult(result, Options.Name);
                    if (result.IsSuccess)
                    {
                        LastSuccess = _timeProvider.GetUtcNow().UtcDateTime;
                    }
                    else
                    {
                        LastErrorMessage = result.Message;
                        // TODO set next run time to retry, but need max retry count
                    }
                }
                catch (TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                    LastErrorMessage = ex.Message;
                    // TODO set next run time to retry, but need max retry count
                }
                finally
                {
                    IsRunning = false;
                    await UpdateDistributedStateAsync();

                    if (isManual)
                        await scheduledTimeLock.ReleaseAsync();
                }
            }
        }, cancellationToken).Unwrap();
    }

    internal async Task UpdateNextRunAsync()
    {
        if (!Options.IsDistributed)
            return;

        _logger.LogDebug("Updating next run for {JobName} ({JobId})", Options.Name, Id);

        try
        {
            await _cacheClient.SetAsync(CacheKey + ":nextrun", NextRun).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating next run for {JobName} ({JobId}): {Message}", Options.Name, Id, ex.Message);
        }
    }

    internal async Task UpdateDistributedStateAsync(bool setNextRun = false, string reason = null)
    {
        if (!Options.IsDistributed)
            return;

        _logger.LogDebug("Updating distributed state for {JobName} ({JobId})", Options.Name, Id);

        try
        {
            if (setNextRun)
                await UpdateNextRunAsync().AnyContext();

            await _cacheClient.SetAsync(CacheKey + ":state",
                new JobInstanceState
                {
                    IsEnabled = Options.IsEnabled,
                    CronSchedule = Options.CronSchedule,
                    IsRunning = IsRunning,
                    LastRun = LastRun,
                    LastSuccess = LastSuccess,
                    LastDuration = LastDuration,
                    LastErrorMessage = LastErrorMessage
                }).AnyContext();

            // send out change notification
            await _messageBus.PublishAsync(new JobStateChangedMessage
            {
                Id = Id,
                JobName = Options.Name,
                IsEnabled = Options.IsEnabled,
                CronSchedule = Options.CronSchedule,
                IsRunning = IsRunning,
                LastRun = LastRun,
                LastSuccess = LastSuccess,
                LastDuration = LastDuration,
                LastErrorMessage = LastErrorMessage,
                Reason = reason
            }).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating job state for {JobName} ({JobId}): {Message}", Options.Name, Id, ex.Message);
        }
    }

    internal async Task ApplyDistributedStateAsync(JobInstanceState state = null)
    {
        if (!Options.IsDistributed)
            return;

        _logger.LogTrace("Applying job state for {JobName} ({JobId})", Options.Name, Id);

        try
        {
            if (state == null)
            {
                LastStateSync = _timeProvider.GetUtcNow().UtcDateTime;

                var cacheState = await _cacheClient.GetAsync<JobInstanceState>(CacheKey + ":state").AnyContext();
                if (!cacheState.HasValue || cacheState.Value == null)
                    return;

                state = cacheState.Value;
            }

            Options.IsEnabled = state.IsEnabled;
            Options.CronSchedule = state.CronSchedule;
            IsRunning = state.IsRunning;
            LastRun = state.LastRun;
            LastSuccess = state.LastSuccess;
            LastDuration = state.LastDuration;
            LastErrorMessage = state.LastErrorMessage;
            NextRun = GetNextScheduledRun();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying job state for {JobName} ({JobId}): {Message}", Options.Name, Id, ex.Message);
        }
    }

    private string GetLockKey(DateTime date)
    {
        long minute = (long)date.Subtract(_baseDate).TotalMinutes;

        return CacheKey + ":" + minute;
    }
}

public class JobInstanceState
{
    public string CronSchedule { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsRunning { get; set; }
    public DateTime? LastRun { get; set; }
    public DateTime? LastSuccess { get; set; }
    public TimeSpan? LastDuration { get; set; }
    public string LastErrorMessage { get; set; }
}

public class JobStateChangedMessage : JobInstanceState
{
    public string Id { get; set; }
    public string JobName { get; set; }
    public string Reason { get; set; }
}
