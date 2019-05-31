using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Utility {
    public interface ISystemClock {
        DateTime Now();
        DateTime UtcNow();
        DateTimeOffset OffsetNow();
        DateTimeOffset OffsetUtcNow();
        void Sleep(int milliseconds);
        Task SleepAsync(int milliseconds, CancellationToken ct);
        TimeSpan TimeZoneOffset();
        void ScheduleWork(Action action, TimeSpan delay, TimeSpan? interval = null);
        void ScheduleWork(Action action, DateTime executeAt, TimeSpan? interval = null);
    }
}