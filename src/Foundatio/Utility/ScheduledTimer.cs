using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Foundatio.Utility {
    public class ScheduledTimer : IDisposable {
        private DateTime _next = DateTime.MaxValue;
        private DateTime _last = DateTime.MinValue;
        private IDisposable _nextScheduledCallback;
        private readonly ILogger _logger;
        private readonly Func<Task<DateTime?>> _timerCallback;
        private readonly object _lock = new object();
        private readonly Subject<int> _subject = new Subject<int>();

        public ScheduledTimer(Func<Task<DateTime?>> timerCallback, TimeSpan? dueTime = null, TimeSpan? minimumIntervalTime = null, ILoggerFactory loggerFactory = null) {
            if (timerCallback == null)
                throw new ArgumentNullException(nameof(timerCallback));

            _logger = loggerFactory.CreateLogger<ScheduledTimer>();
            _timerCallback = timerCallback;
            var intervalTime = minimumIntervalTime.HasValue && minimumIntervalTime.Value > TimeSpan.FromMilliseconds(100) ? minimumIntervalTime.Value : TimeSpan.FromMilliseconds(100);

            _subject
              .Select(l => Observable.FromAsync(RunCallbackAsync))
              .Concat()
              .Throttle(intervalTime)
              .Subscribe();

            if (dueTime != null)
                ScheduleNext(SystemClock.UtcNow.Add(dueTime.Value));
        }

        public void ScheduleNext(DateTime? utcDate = null) {
            var utcNow = SystemClock.UtcNow;
            if (!utcDate.HasValue || utcDate.Value < utcNow)
                utcDate = utcNow;

            _logger.Trace(() => $"ScheduleNext called: value={utcDate.Value:O}");

            if (utcDate == DateTime.MaxValue) {
                _logger.Trace("Ignoring MaxValue");
                return;
            }

            lock (_lock) {
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

                DisposeTimer();
                _nextScheduledCallback = SystemClock.Instance.Schedule(TimeSpan.FromMilliseconds(delay), () => _subject.OnNext(0));
            }
        }
        
        private async Task RunCallbackAsync(CancellationToken cancellationToken) {
            _logger.Trace("RunCallbackAsync");

            try {
                var next = await _timerCallback().AnyContext();
                if (next.HasValue)
                    ScheduleNext(next.Value);
            } catch (Exception ex) {
                _logger.Error(ex, () => $"Error running scheduled timer callback: {ex.Message}");
            }
        }

        public void Dispose() {
            DisposeTimer();
        }

        private void DisposeTimer() {
            try {
                _nextScheduledCallback?.Dispose();
            } catch {
                // ignored
            }
        }
    }
}