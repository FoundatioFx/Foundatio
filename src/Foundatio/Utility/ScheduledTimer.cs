using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility;

public class ScheduledTimer : IDisposable
{
    private DateTime _next = DateTime.MaxValue;
    private DateTime _last = DateTime.MinValue;
    private readonly Timer _timer;
    private readonly ILogger _logger;
    private readonly Func<Task<DateTime?>> _timerCallback;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _minimumInterval;
    private readonly AsyncLock _lock = new();
    private bool _isRunning = false;
    private bool _shouldRunAgainImmediately = false;

    public ScheduledTimer(Func<Task<DateTime?>> timerCallback, TimeSpan? dueTime = null, TimeSpan? minimumIntervalTime = null, TimeProvider timeProvider = null, ILoggerFactory loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<ScheduledTimer>() ?? NullLogger<ScheduledTimer>.Instance;
        _timerCallback = timerCallback ?? throw new ArgumentNullException(nameof(timerCallback));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _minimumInterval = minimumIntervalTime ?? TimeSpan.Zero;

        int dueTimeMs = dueTime.HasValue ? (int)dueTime.Value.TotalMilliseconds : Timeout.Infinite;
        _timer = new Timer(s => Task.Run(RunCallbackAsync), null, dueTimeMs, Timeout.Infinite);
    }

    public void ScheduleNext(DateTime? utcDate = null)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        if (!utcDate.HasValue || utcDate.Value < utcNow)
            utcDate = utcNow;

        bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
        if (isTraceLogLevelEnabled) _logger.LogTrace("ScheduleNext called: value={NextRun:O}", utcDate.Value);

        if (utcDate == DateTime.MaxValue)
        {
            if (isTraceLogLevelEnabled) _logger.LogTrace("Ignoring MaxValue");
            return;
        }

        // already have an earlier scheduled time
        if (_next > utcNow && utcDate > _next)
        {
            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Ignoring because already scheduled for earlier time: {PreviousNextRun:O} Next: {NextRun:O}", utcDate.Value, _next);
            return;
        }

        // ignore duplicate times
        if (_next == utcDate)
        {
            if (isTraceLogLevelEnabled) _logger.LogTrace("Ignoring because already scheduled for same time");
            return;
        }

        using (_lock.Lock())
        {
            // already have an earlier scheduled time
            if (_next > utcNow && utcDate > _next)
            {
                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Ignoring because already scheduled for earlier time: {PreviousNextRun:O} Next: {NextRun:O}", utcDate.Value, _next);
                return;
            }

            // ignore duplicate times
            if (_next == utcDate)
            {
                if (isTraceLogLevelEnabled) _logger.LogTrace("Ignoring because already scheduled for same time");
                return;
            }

            int delay = Math.Max((int)Math.Ceiling(utcDate.Value.Subtract(utcNow).TotalMilliseconds), 0);
            _next = utcDate.Value;
            if (_last == DateTime.MinValue)
                _last = _next;

            if (isTraceLogLevelEnabled) _logger.LogTrace("Scheduling next: delay={Delay}", delay);
            if (delay > 0)
                _timer.Change(delay, Timeout.Infinite);
            else
                _ = Task.Run(RunCallbackAsync);
        }
    }

    private async Task RunCallbackAsync()
    {
        bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
        if (_isRunning)
        {
            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Exiting run callback because its already running, will run again immediately");

            _shouldRunAgainImmediately = true;
            return;
        }

        // If the callback runs before the next time, then store it here before we reset it and use it for scheduling.
        DateTime? nextTimeOverride = null;

        if (isTraceLogLevelEnabled) _logger.LogTrace("Starting RunCallbackAsync");
        using (await _lock.LockAsync().AnyContext())
        {
            if (_isRunning)
            {
                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Exiting run callback because its already running, will run again immediately");

                _shouldRunAgainImmediately = true;
                return;
            }

            _last = _timeProvider.GetUtcNow().UtcDateTime;
            if (_last < _next)
            {
                _logger.LogWarning("ScheduleNext RunCallbackAsync was called before next run time {NextRun:O}, setting next to current time and rescheduling", _next);
                nextTimeOverride = _next;
                _next = _timeProvider.GetUtcNow().UtcDateTime;
                _shouldRunAgainImmediately = true;
            }
        }

        try
        {
            _isRunning = true;
            DateTime? next = null;

            var sw = Stopwatch.StartNew();
            try
            {
                next = await _timerCallback().AnyContext();
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(ex, "Error running scheduled timer callback: {Message}", ex.Message);

                _shouldRunAgainImmediately = true;
            }
            finally
            {
                sw.Stop();
                if (isTraceLogLevelEnabled) _logger.LogTrace("Callback took: {Elapsed:g}", sw.Elapsed);
            }

            if (_minimumInterval > TimeSpan.Zero)
            {
                if (isTraceLogLevelEnabled) _logger.LogTrace("Sleeping for minimum interval: {Interval:g}", _minimumInterval);
                await _timeProvider.Delay(_minimumInterval).AnyContext();
                if (isTraceLogLevelEnabled) _logger.LogTrace("Finished sleeping");
            }

            var nextRun = _timeProvider.GetUtcNow().UtcDateTime.AddMilliseconds(10);
            if (nextRun < nextTimeOverride)
                nextRun = nextTimeOverride.Value;

            if (_shouldRunAgainImmediately || next.HasValue && next.Value <= nextRun)
                ScheduleNext(nextRun);
            else if (next.HasValue)
                ScheduleNext(next.Value);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex, "Error running schedule next callback: {Message}", ex.Message);
        }
        finally
        {
            _isRunning = false;
            _shouldRunAgainImmediately = false;
        }

        if (isTraceLogLevelEnabled) _logger.LogTrace("Finished RunCallbackAsync");
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
