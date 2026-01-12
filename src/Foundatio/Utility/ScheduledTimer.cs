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
    private readonly CancellationTokenSource _disposedCancellationTokenSource = new();
    private bool _isRunning = false;
    private bool _shouldRunAgainImmediately = false;
    private bool _isDisposed;

    public ScheduledTimer(Func<Task<DateTime?>> timerCallback, TimeSpan? dueTime = null, TimeSpan? minimumIntervalTime = null, TimeProvider timeProvider = null, ILoggerFactory loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<ScheduledTimer>() ?? NullLogger<ScheduledTimer>.Instance;
        _timerCallback = timerCallback ?? throw new ArgumentNullException(nameof(timerCallback));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _minimumInterval = minimumIntervalTime ?? TimeSpan.Zero;

        int dueTimeMs = dueTime.HasValue ? (int)dueTime.Value.TotalMilliseconds : Timeout.Infinite;
        _timer = new Timer(_ => OnTimerCallback(), null, dueTimeMs, Timeout.Infinite);
    }

    private void OnTimerCallback()
    {
        if (_disposedCancellationTokenSource.IsCancellationRequested)
        {
            _logger.LogTrace("OnTimerCallback: Ignoring because disposed");
            return;
        }

        _logger.LogTrace("OnTimerCallback: Starting callback task");
        _ = Task.Run(RunCallbackAsync, _disposedCancellationTokenSource.Token);
    }

    public void ScheduleNext(DateTime? utcDate = null)
    {
        if (_disposedCancellationTokenSource.IsCancellationRequested)
        {
            _logger.LogTrace("ScheduleNext: Ignoring because disposed");
            return;
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        if (!utcDate.HasValue || utcDate.Value < utcNow)
            utcDate = utcNow;

        _logger.LogTrace("ScheduleNext called: value={NextRun:O}", utcDate.Value);

        if (utcDate == DateTime.MaxValue)
        {
            _logger.LogTrace("Ignoring MaxValue");
            return;
        }

        // already have an earlier scheduled time
        if (_next > utcNow && utcDate > _next)
        {
            _logger.LogTrace("Ignoring because already scheduled for earlier time: {PreviousNextRun:O} Next: {NextRun:O}", utcDate.Value, _next);
            return;
        }

        // ignore duplicate times
        if (_next == utcDate)
        {
            _logger.LogTrace("Ignoring because already scheduled for same time");
            return;
        }

        using (_lock.Lock(_disposedCancellationTokenSource.Token))
        {
            if (_disposedCancellationTokenSource.IsCancellationRequested)
            {
                _logger.LogTrace("ScheduleNext: Ignoring because disposed after acquiring lock");
                return;
            }

            // already have an earlier scheduled time
            if (_next > utcNow && utcDate > _next)
            {
                _logger.LogTrace("Ignoring because already scheduled for earlier time: {PreviousNextRun:O} Next: {NextRun:O}", utcDate.Value, _next);
                return;
            }

            // ignore duplicate times
            if (_next == utcDate)
            {
                _logger.LogTrace("Ignoring because already scheduled for same time");
                return;
            }

            int delay = Math.Max((int)Math.Ceiling(utcDate.Value.Subtract(utcNow).TotalMilliseconds), 0);
            _next = utcDate.Value;
            if (_last == DateTime.MinValue)
                _last = _next;

            _logger.LogTrace("Scheduling next: delay={Delay}", delay);
            if (delay > 0)
                _timer.Change(delay, Timeout.Infinite);
            else
                OnTimerCallback();
        }
    }

    private async Task RunCallbackAsync()
    {
        if (_isRunning)
        {
            _logger.LogTrace("Exiting run callback because its already running, will run again immediately");
            _shouldRunAgainImmediately = true;
            return;
        }

        // If the callback runs before the next time, then store it here before we reset it and use it for scheduling.
        DateTime? nextTimeOverride = null;

        _logger.LogTrace("Starting RunCallbackAsync");
        using (await _lock.LockAsync().AnyContext())
        {
            if (_isRunning)
            {
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
                _logger.LogError(ex, "Error running scheduled timer callback: {Message}", ex.Message);
                _shouldRunAgainImmediately = true;
            }
            finally
            {
                sw.Stop();
                _logger.LogTrace("Callback took: {Elapsed:g}", sw.Elapsed);
            }

            if (_minimumInterval > TimeSpan.Zero)
            {
                _logger.LogTrace("Sleeping for minimum interval: {Interval:g}", _minimumInterval);
                await _timeProvider.Delay(_minimumInterval, _disposedCancellationTokenSource.Token).AnyContext();
                _logger.LogTrace("Finished sleeping");
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
            _logger.LogError(ex, "Error running schedule next callback: {Message}", ex.Message);
        }
        finally
        {
            _isRunning = false;
            _shouldRunAgainImmediately = false;
        }

        _logger.LogTrace("Finished RunCallbackAsync");
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _logger.LogTrace("Disposing scheduled timer");
        _disposedCancellationTokenSource.Cancel();
        _disposedCancellationTokenSource.Dispose();
        _timer?.Dispose();
    }
}
