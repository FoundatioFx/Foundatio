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

        public ScheduledTimer(Func<Task<DateTime?>> timerCallback, TimeSpan? dueTime = null, TimeSpan? minimumIntervalTime = null, ILoggerFactory loggerFactory = null) {
            if (timerCallback == null)
                throw new ArgumentNullException(nameof(timerCallback));

            _logger = loggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
            _timerCallback = timerCallback;
            _minimumInterval = minimumIntervalTime ?? TimeSpan.Zero;

            int dueTimeMs = dueTime.HasValue ? (int)dueTime.Value.TotalMilliseconds : Timeout.Infinite;
            _timer = new Timer(s => RunCallbackAsync().GetAwaiter().GetResult(), null, dueTimeMs, Timeout.Infinite);
        }

        public void Run() {
            ScheduleNext(DateTime.UtcNow);
        }

        public void ScheduleNext(DateTime utcDate) {
            _logger.Trace(() => $"ScheduleNext called: value={utcDate}");

            if (utcDate == DateTime.MaxValue) {
                _logger.Trace("Ignoring MaxValue");
                return;
            }

            using (_lock.Lock()) {
                var now = DateTime.UtcNow;

                // already have an earlier scheduled time
                if (_next > now && utcDate > _next) {
                    _logger.Trace(() => $"Ignoring because already scheduled for earlier time {utcDate.Ticks} {_next.Ticks}");
                    return;
                }

                // enforce minimum interval
                _logger.Trace(() => $"Last: {_last} Since: {_last.Subtract(utcDate)} Min interval: {_minimumInterval}");
                if (_last != DateTime.MinValue && _last.Subtract(utcDate) < _minimumInterval) {
                    _logger.Trace("Adding time due to minimum interval");
                    utcDate = _last.Add(_minimumInterval);
                }

                // ignore duplicate times
                if (_next == utcDate) {
                    _logger.Trace("Ignoring because already scheduled for same time");
                    return;
                }

                int delay = Math.Max((int)utcDate.Subtract(now).TotalMilliseconds, 0);
                _next = utcDate;
                _logger.Trace("Scheduling next: delay={delay}", delay);

                _timer.Change(delay, Timeout.Infinite);
            }
        }

        private async Task RunCallbackAsync() {
            _logger.Trace("RunCallbackAsync");

            using (await _lock.LockAsync()) {
                _last = DateTime.UtcNow;
                if (_isRunning) {
                    _logger.Trace("Exiting run callback because its already running");
                    Run();
                    return;
                }

                try {
                    _isRunning = true;
                    var next = await _timerCallback().AnyContext();

                    if (next.HasValue)
                        ScheduleNext(next.Value);
                } finally {
                    _isRunning = false;
                }
            }
        }

        public void Dispose() {
            _timer?.Dispose();
        }
    }
}