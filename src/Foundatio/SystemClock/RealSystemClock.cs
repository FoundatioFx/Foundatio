using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Utility {
    public class RealSystemClock : ISystemClock {
        public static readonly RealSystemClock Instance = new RealSystemClock();

        public DateTime Now => DateTime.Now;
        public DateTime UtcNow => DateTime.UtcNow;
        public DateTimeOffset OffsetNow => DateTimeOffset.Now;
        public DateTimeOffset OffsetUtcNow => DateTimeOffset.UtcNow;
        public void Sleep(int milliseconds) => Thread.Sleep(milliseconds);
        public Task SleepAsync(int milliseconds, CancellationToken ct = default) => Task.Delay(milliseconds, ct);
        public TimeSpan Offset => DateTimeOffset.Now.Offset;
        public void ScheduleWork(Action action, TimeSpan delay, TimeSpan? interval = null)
            => WorkScheduler.Default.Schedule(action, delay, interval);
        public void ScheduleWork(Action action, DateTime executeAt, TimeSpan? interval = null)
            => WorkScheduler.Default.Schedule(action, executeAt, interval);
    }
}