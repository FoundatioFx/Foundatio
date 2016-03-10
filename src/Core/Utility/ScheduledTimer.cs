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
            var now = DateTime.UtcNow;
            if (!utcDate.HasValue || utcDate.Value < now)
                utcDate = now;

            _logger.Trace(() => $"ScheduleNext called: value={utcDate.Value}");

            if (utcDate == DateTime.MaxValue) {
                _logger.Trace("Ignoring MaxValue");
                return;
            }

            using (_lock.Lock()) {
                // already have an earlier scheduled time
                if (_next > now && utcDate > _next) {
                    _logger.Trace(() => $"Ignoring because already scheduled for earlier time {utcDate.Value.Ticks} {_next.Ticks}");
                    return;
                }

                // ignore duplicate times
                if (_next == utcDate) {
                    _logger.Trace("Ignoring because already scheduled for same time");
                    return;
                }

                int delay = Math.Max((int)utcDate.Value.Subtract(now).TotalMilliseconds, 0);
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

                _last = DateTime.UtcNow;
            }

            try {
                _isRunning = true;
                var next = await _timerCallback().AnyContext();
                if (_minimumInterval > TimeSpan.Zero)
                    await Task.Delay(_minimumInterval).AnyContext();

                if (_shouldRunAgainImmediately)
                    ScheduleNext();
                else if (next.HasValue)
                    ScheduleNext(next.Value);
            } catch (Exception ex) {
                _logger.Error(ex, () => $"Error running scheduled timer callback: {ex.Message}");
            } finally {
                _shouldRunAgainImmediately = false;
                _isRunning = false;
            }
        }

        public void Dispose() {
            _timer?.Dispose();
        }
    }
}