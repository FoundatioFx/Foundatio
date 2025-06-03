using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;
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

                _logger.LogDebug("Cron schedule changed for job {JobName} ({JobId}): {CronSchedule}", _jobOptions.Name, Id, _jobOptions.CronSchedule);

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
    public DateTime? LastSuccess { get; internal set; }
    public DateTime? LastRun { get; internal set; }
    public List<JobRunResult> History { get; set; } = new();

    internal bool SkipUpdate { get; set; } = false;

    public Task RunTask { get; private set; }

    internal string CacheKey { get; }

    public DateTime? GetNextScheduledRun()
    {
        if (Options.IsEnabled == false || _cronExpression == null)
            return null;

        var lastRun = LastRun ?? _timeProvider.GetUtcNowDateTime(false).AddSeconds(-5);
        var nextRun = _cronExpression.GetNextOccurrence(lastRun, _jobOptions.CronTimeZone ?? TimeZoneInfo.Local);
        if (nextRun == null)
            return null;

        if (nextRun < _timeProvider.GetUtcNowDateTime(false))
        {
            var futureRun = _cronExpression.GetNextOccurrence(_timeProvider.GetUtcNowDateTime(false), _jobOptions.CronTimeZone ?? TimeZoneInfo.Local);

            // if next run is more than an hour in the past, use the future run
            if (_timeProvider.GetUtcNowDateTime(false).Subtract(nextRun.Value) > TimeSpan.FromHours(1))
                nextRun = futureRun;

            // if the next run is within 10 minutes, use it
            if (futureRun.HasValue && futureRun.Value.Subtract(_timeProvider.GetUtcNowDateTime(false)) < TimeSpan.FromMinutes(10))
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
        if (NextRun > _timeProvider.GetUtcNowDateTime(false))
            return false;

        // check if already run
        if (LastRun != null && LastRun.Value == NextRun.Value)
            return false;

        return true;
    }

    public async Task ReleaseLockAsync()
    {
        if (!Options.IsDistributed)
            return;

        _logger.LogDebug("Releasing lock for {JobName} ({JobId})", Options.Name, Id);

        try
        {
            await _lockProvider.ReleaseAsync(CacheKey).AnyContext();
            await _lockProvider.ReleaseAsync(GetLockKey(_baseDate)).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error releasing lock for {JobName} ({JobId}): {Message}", Options.Name, Id, ex.Message);
        }
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
                // hold this lock for 1 hour to prevent duplicates
                scheduledTimeLock = await _lockProvider.AcquireAsync(GetLockKey(scheduledTime), TimeSpan.FromHours(1), TimeSpan.Zero).AnyContext();

                if (scheduledTimeLock != null)
                {
                    // hold this lock while the job is running to prevent multiple instances of the job running at the same time
                    jobRunningLock = await _lockProvider.AcquireAsync(CacheKey, TimeSpan.FromMinutes(15), TimeSpan.Zero).AnyContext();

                    if (jobRunningLock == null)
                        await scheduledTimeLock.ReleaseAsync().AnyContext();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error acquiring locks for job ({JobName})", Options.Name);
                if (scheduledTimeLock != null)
                    await scheduledTimeLock.ReleaseAsync().AnyContext();
                scheduledTimeLock = null;
                if (jobRunningLock != null)
                    await jobRunningLock.ReleaseAsync().AnyContext();
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
                var utcNow = _timeProvider.GetUtcNowDateTime(false);
                var jobRunResult = new JobRunResult { Date = utcNow };
                if (isManual)
                    jobRunResult.Manual = true;
                else
                    jobRunResult.Scheduled = scheduledTime;

                var sw = new Stopwatch();

                try
                {
                    _logger.LogInformation("{JobType} {JobName} ({JobId}) starting for time: {ScheduledTime}", Options.IsDistributed ? "Distributed job" : "Job", Options.Name,
                        Id, isManual ? "Manual" : NextRun!.Value.ToString("t"));

                    await using var scope = _serviceProvider.CreateAsyncScope();

                    var job = Options.JobFactory(scope.ServiceProvider);

                    IsRunning = true;
                    LastRun = isManual ? utcNow : NextRun;
                    NextRun = GetNextScheduledRun();

                    await UpdateDistributedStateAsync(true).AnyContext();

                    sw.Start();
                    var result = await job.TryRunAsync(cancellationToken).AnyContext();
                    sw.Stop();
                    jobRunResult.Duration = sw.Elapsed;

                    _logger.LogJobResult(result, Options.Name);
                    if (result.IsSuccess)
                    {
                        jobRunResult.Success = true;
                        LastSuccess = _timeProvider.GetUtcNowDateTime(false);
                    }
                    else
                    {
                        jobRunResult.Success = false;

                        // TODO set next run time to retry, but need max retry count
                    }
                }
                catch (TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    jobRunResult.Duration = sw.Elapsed;
                    jobRunResult.Success = false;
                    jobRunResult.ErrorMessage = ex.Message;

                    if (scheduledTimeLock != null)
                        await scheduledTimeLock.ReleaseAsync();

                    if (jobRunningLock != null)
                        await jobRunningLock.ReleaseAsync();

                    // TODO set next run time to retry, but need max retry count
                }
                finally
                {
                    IsRunning = false;
                    AddJobRunResult(jobRunResult);

                    await UpdateDistributedStateAsync();

                    if (isManual)
                        await scheduledTimeLock.ReleaseAsync();
                }
            }
        }, cancellationToken).Unwrap();
    }

    private void AddJobRunResult(JobRunResult result)
    {
        if (result == null)
            return;

        const int maxCount = 10;

        History.Insert(0, result);
        if (History.Count > maxCount)
            History.RemoveRange(maxCount, History.Count - maxCount);
    }

    internal async Task UpdateDistributedStateAsync(bool setNextRun = false, string reason = null)
    {
        if (!Options.IsDistributed || SkipUpdate)
            return;

        try
        {
            var jobState = new JobInstanceState
            {
                IsEnabled = Options.IsEnabled,
                CronSchedule = Options.CronSchedule,
                IsRunning = IsRunning,
                LastRun = LastRun,
                LastSuccess = LastSuccess,
                History = History ?? []
            };

            _logger.LogDebug("Updating distributed state for {JobName} ({JobId}): {JobState}", Options.Name, Id, Options.CronSchedule);

            if (setNextRun)
                await _cacheClient.SetAsync(CacheKey + ":nextrun", NextRun).AnyContext();

            await _cacheClient.SetAsync(CacheKey + ":state", jobState).AnyContext();

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
                History = History,
                Reason = reason
            }).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating job state for {JobName} ({JobId}): {Message}", Options.Name, Id, ex.Message);
        }
    }

    internal async Task ApplyDistributedStateAsync()
    {
        if (!Options.IsDistributed)
            return;

        try
        {
            _logger.LogDebug("Getting job state for {JobName} ({JobId})", Options.Name, Id);

            LastStateSync = _timeProvider.GetUtcNowDateTime(false);

            var cacheState = await _cacheClient.GetAsync<JobInstanceState>(CacheKey + ":state").AnyContext();
            if (!cacheState.HasValue || cacheState.Value == null)
                return;

            ApplyDistributedState(cacheState.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting job state for {JobName} ({JobId}): {Message}", Options.Name, Id, ex.Message);
        }
    }

    internal void ApplyDistributedState(JobInstanceState state, string cronSchedule = null)
    {
        if (!Options.IsDistributed || state == null)
            return;

        _logger.LogDebug("Applying job state for {JobName} ({JobId})", Options.Name, Id);

        Options.IsEnabled = state.IsEnabled;
        Options.CronSchedule = cronSchedule ?? state.CronSchedule;
        IsRunning = state.IsRunning;
        LastRun = state.LastRun;
        LastSuccess = state.LastSuccess;
        History = state.History;
        NextRun = GetNextScheduledRun();

        LastStateSync = _timeProvider.GetUtcNowDateTime(false);
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
    public List<JobRunResult> History { get; set; }
}

public class JobRunResult
{
    public DateTime? Date { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Scheduled { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Manual { get; set; }
    public bool Success { get; set; }
    public TimeSpan? Duration { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ErrorMessage { get; set; }
}

public class JobStateChangedMessage : JobInstanceState
{
    public string Id { get; set; }
    public string JobName { get; set; }
    public string Reason { get; set; }
}
