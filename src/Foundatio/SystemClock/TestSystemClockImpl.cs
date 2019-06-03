using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility {
    internal class TestSystemClockImpl : ITestSystemClock {
        private DateTime _utcTime = DateTime.UtcNow;
        private TimeSpan _offset = DateTimeOffset.Now.Offset;
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

        public DateTime UtcNow => _utcTime;
        public DateTime Now => new DateTime(_utcTime.Add(_offset).Ticks, DateTimeKind.Local);
        public DateTimeOffset OffsetNow => new DateTimeOffset(Now.Ticks, _offset);
        public DateTimeOffset OffsetUtcNow => new DateTimeOffset(_utcTime);
        public TimeSpan Offset => _offset;
        public void ScheduleWork(Action action, TimeSpan delay, TimeSpan? interval = null)
            => _workScheduler.Schedule(action, delay, interval);
        public void ScheduleWork(Action action, DateTime executeAtUtc, TimeSpan? interval = null)
            => _workScheduler.Schedule(action, executeAtUtc, interval);

        public void AddTime(TimeSpan amount) {
            _utcTime = _utcTime.Add(amount);
            OnChanged();
        }

        public void SetTime(DateTime time, TimeSpan? offset = null) {
            if (time.Kind == DateTimeKind.Local)
                _utcTime = time.ToUniversalTime();
            else if (time.Kind == DateTimeKind.Unspecified)
                _utcTime = new DateTime(time.Ticks, DateTimeKind.Utc);
            else
                _utcTime = time;

            if (offset.HasValue)
                _offset = offset.Value;
            
            OnChanged();
        }

        public WaitHandle NoScheduledWorkItemsDue => _workScheduler.NoWorkItemsDue;

        public void Sleep(int milliseconds) {
            AddTime(TimeSpan.FromMilliseconds(milliseconds));
            Thread.Sleep(1);
        }

        public Task SleepAsync(int milliseconds, CancellationToken ct = default) {
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