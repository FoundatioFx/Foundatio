using System;
using System.Threading;

namespace Foundatio.Utility {
    public interface ITestSystemClock : ISystemClock, IDisposable {
        void AddTime(TimeSpan amount);
        void SetTime(DateTime time, TimeSpan? offset = null);
        WaitHandle NoScheduledWorkItemsDue { get; }
        event EventHandler Changed;
    }
}