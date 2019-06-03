using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Utility {
    public interface ISystemClock {
        DateTime Now { get; }
        DateTime UtcNow { get; }
        DateTimeOffset OffsetNow { get; }
        DateTimeOffset OffsetUtcNow { get; }
        void Sleep(int milliseconds);
        Task SleepAsync(int milliseconds, CancellationToken ct = default);
        TimeSpan Offset { get; }
        void ScheduleWork(Action action, TimeSpan delay, TimeSpan? interval = null);
        void ScheduleWork(Action action, DateTime executeAt, TimeSpan? interval = null);
    }
}