using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Nito.AsyncEx;

namespace Foundatio.Utility {
    public class ScheduledTimer : IDisposable {
        private DateTime _next = DateTime.MaxValue;
        private DateTime _last = DateTime.MinValue;
        private readonly Timer _timer;
        private readonly ILogger _logger;
        private readonly Func<Task<DateTime?>> _timerCallback;
        private readonly TimeSpan _minimumInterval;
        private readonly AsyncLock _lock = new AsyncLock();
        private bool _isRunning = false;
        private bool _shouldRunAgainImmediately = false;

        public ScheduledTimer(Func<Task<DateTime?>> timerCallback, TimeSpan? dueTime = null, TimeSpan? minimumIntervalTime = null, ILoggerFactory loggerFactory = null) {
            if (timerCallback == null)
                throw new ArgumentNullException(nameof(timerCallback));

            _logger = loggerFactory.CreateLogger<ScheduledTimer>();
            _timerCallback = timerCallback;
            _minimumInterval = minimumIntervalTime ?? TimeSpan.Zero;

            int dueTimeMs = dueTime.HasValue ? (int)dueTime.Value.TotalMilliseconds : Timeout.Infinite;
            _timer = new Timer(s => RunCallbackAsync().GetAwaiter().GetResult(), null, dueTimeMs, Timeout.Infinite);
        }

        public void ScheduleNext(DateTime? utcDate = null) {
            var utcNow = SystemClock.UtcNow;
            if (!utcDate.HasValue || utcDate.Value < utcNow)
                utcDate = utcNow;

            _logger.Trace(() => $"ScheduleNext called: value={utcDate.Value.ToString("O")}");

            if (utcDate == DateTime.MaxValue) {
                _logger.Trace("Ignoring MaxValue");
                return;
            }

            using (_lock.Lock()) {
                // already have an earlier scheduled time
                if (_next > utcNow && utcDate > _next) {
                    _logger.Trace(() => $"Ignoring because already scheduled for earlier time {utcDate.Value.Ticks} {_next.Ticks}");
                    return;
                }

                // ignore duplicate times
                if (_next == utcDate) {
                    _logger.Trace("Ignoring because already scheduled for same time");
                    return;
                }

                int delay = Math.Max((int)Math.Ceiling(utcDate.Value.Subtract(utcNow).TotalMilliseconds), 0);
                _next = utcDate.Value;
                if (_last == DateTime.MinValue)
                    _last = _next;

                _logger.Trace(() => $"Scheduling next: delay={delay}");

                _timer.Change(delay, Timeout.Infinite);
            }
        }

        private async Task RunCallbackAsync() {
            if (_isRunning) {
                _logger.Trace("Exiting run callback because its already running, will run again immediately.");
                _shouldRunAgainImmediately = true;
                return;
            }

            _logger.Trace("RunCallbackAsync");

            using (await _lock.LockAsync()) {
                if (_isRunning) {
                    _logger.Trace("Exiting run callback because its already running, will run again immediately.");
                    _shouldRunAgainImmediately = true;
                    return;
                }

                _last = SystemClock.UtcNow;
            }

            try {
                _isRunning = true;
                DateTime? next = null;

                try {
                    next = await _timerCallback().AnyContext();
                } catch (Exception ex) {
                    _logger.Error(ex, () => $"Error running scheduled timer callback: {ex.Message}");
                    _shouldRunAgainImmediately = true;
                }

                if (_minimumInterval > TimeSpan.Zero) {
                    _logger.Trace("Sleeping for minimum interval: {interval}", _minimumInterval);
                    await SystemClock.SleepAsync(_minimumInterval).AnyContext();
                    _logger.Trace("Finished sleeping");
                }

                var nextRun = SystemClock.UtcNow.AddMilliseconds(10);
                if (_shouldRunAgainImmediately || next.HasValue && next.Value <= nextRun)
                    ScheduleNext(nextRun);
                else if (next.HasValue)
                    ScheduleNext(next.Value);
            } catch (Exception ex) {
                _logger.Error(ex, () => $"Error running schedule next callback: {ex.Message}");
            } finally {
                _isRunning = false;
                _shouldRunAgainImmediately = false;
            }
        }

        public void Dispose() {
            _timer?.Dispose();
        }
    }
}