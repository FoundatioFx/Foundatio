using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility {
    internal class TestSystemClockImpl : ITestSystemClock {
        private DateTimeOffset _time = DateTimeOffset.Now;
        private readonly ISystemClock _originalClock;
        private readonly WorkScheduler _workScheduler;

        public TestSystemClockImpl(ILoggerFactory loggerFactory) {
            loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
            var logger = loggerFactory.CreateLogger("Foundatio.Utility.SystemClock");
            _workScheduler = new WorkScheduler(this, logger);
        }

        public TestSystemClockImpl(ISystemClock originalTime, ILoggerFactory loggerFactory) : this(loggerFactory) {
            _originalClock = originalTime;
        }

        public DateTime UtcNow() => _time.UtcDateTime;
        public DateTime Now() => _time.DateTime;
        public DateTimeOffset OffsetNow() => _time;
        public DateTimeOffset OffsetUtcNow() => new DateTimeOffset(UtcNow().Ticks, TimeSpan.Zero);
        public TimeSpan TimeZoneOffset() => _time.Offset;
        public void ScheduleWork(Action action, TimeSpan delay, TimeSpan? interval = null)
            => _workScheduler.Schedule(action, delay, interval);
        public void ScheduleWork(Action action, DateTime executeAtUtc, TimeSpan? interval = null)
            => _workScheduler.Schedule(action, executeAtUtc, interval);

        public void AddTime(TimeSpan amount) {
            _time = _time.Add(amount);
            OnChanged();
        }

        public void SetTime(DateTime time, TimeSpan? timeZoneOffset = null) {
            if (timeZoneOffset.HasValue)
                _time = new DateTimeOffset(time.ToUniversalTime(), timeZoneOffset.Value);
            else
                _time = new DateTimeOffset(time);
            OnChanged();
        }

        public WaitHandle NoScheduledWorkItemsDue => _workScheduler.NoWorkItemsDue;

        public void Sleep(int milliseconds) {
            AddTime(TimeSpan.FromMilliseconds(milliseconds));
            Thread.Sleep(1);
        }

        public Task SleepAsync(int milliseconds, CancellationToken ct) {
            Sleep(milliseconds);
            return Task.CompletedTask;
        }

        public event EventHandler Changed;
        public WorkScheduler Scheduler => _workScheduler;

        private void OnChanged() {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        
        public void Dispose() {
            if (_originalClock != null)
                SystemClock.SetInstance(_originalClock, null);
        }
        
        public static TestSystemClockImpl Instance {
            get {
                if (!(SystemClock.Instance is TestSystemClockImpl testClock))
                    throw new ArgumentException("You must first install TestSystemClock using TestSystemClock.Install");

                return testClock;
            }
        }
    }
}