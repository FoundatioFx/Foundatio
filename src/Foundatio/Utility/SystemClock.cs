using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Utility {
    public static class SystemClock {
        public static Action<int> SleepFunc = Thread.Sleep;
        public static Func<int, CancellationToken, Task> SleepFuncAsync = Task.Delay;
        public static Func<DateTime> UtcNowFunc = () => DateTime.UtcNow;

        public static DateTime UtcNow => UtcNowFunc();

        public static void Sleep(TimeSpan time) {
            SleepFunc((int)time.TotalMilliseconds);
        }

        public static void Sleep(int time) {
            SleepFunc(time);
        }

        public static Task SleepAsync(TimeSpan time, CancellationToken cancellationToken = default(CancellationToken)) {
            return SleepFuncAsync((int)time.TotalMilliseconds, cancellationToken);
        }

        public static Task SleepAsync(int milliseconds, CancellationToken cancellationToken = default(CancellationToken)) {
            return SleepAsync(TimeSpan.FromMilliseconds(milliseconds), cancellationToken);
        }

        public static void Reset() {
            SleepFunc = Thread.Sleep;
            SleepFuncAsync = Task.Delay;
            UtcNowFunc = () => DateTime.UtcNow;
        }
    }
}
