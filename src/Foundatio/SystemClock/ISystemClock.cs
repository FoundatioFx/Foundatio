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
        void Schedule(Action action, TimeSpan dueTime);
        ITimer Timer(Action action, TimeSpan dueTime, TimeSpan period);
    }

    public interface ITimer : IDisposable {
        bool Change(TimeSpan dueTime, TimeSpan period);
    }
}