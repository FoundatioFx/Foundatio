using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility {
    public class SystemClock {
        private static AsyncLocal<ISystemClock> _instance;
        private static readonly ISystemClock _realClock = new RealSystemClock(null);
        
        public static ISystemClock Instance => _instance?.Value ?? _realClock;

        public static void SetInstance(ISystemClock clock, ILoggerFactory loggerFactory) {
            var logger = loggerFactory?.CreateLogger("SystemClock") ?? NullLogger.Instance;
            _instance = new AsyncLocal<ISystemClock>(e => {
                if (e.ThreadContextChanged)
                    return;

                if (e.PreviousValue != null && e.CurrentValue != null) {
                    var diff = e.PreviousValue.Now.Subtract(e.CurrentValue.Now);
                    logger.LogTrace("SystemClock instance is being changed by {ThreadId} from {OldTime} to {NewTime} diff {Difference:g}", Thread.CurrentThread.ManagedThreadId, e.PreviousValue?.Now, e.CurrentValue?.Now, diff);
                }

                if (e.PreviousValue == null)
                    logger.LogTrace("SystemClock instance is being initially set by {ThreadId} to {NewTime}", Thread.CurrentThread.ManagedThreadId, e.CurrentValue?.Now);

                if (e.CurrentValue == null)
                    logger.LogTrace("SystemClock instance is being removed set by {ThreadId} from {OldTime}", Thread.CurrentThread.ManagedThreadId, e.PreviousValue?.Now);
            });

            if (clock == null || clock is RealSystemClock) {
                if (_instance != null)
                    _instance.Value = null;
                _instance = null;
            } else {
                _instance.Value = clock;
            }
        }

        public static DateTime Now => Instance.Now;
        public static DateTime UtcNow => Instance.UtcNow;
        public static DateTimeOffset OffsetNow => Instance.OffsetNow;
        public static DateTimeOffset OffsetUtcNow => Instance.OffsetUtcNow;
        public static TimeSpan TimeZoneOffset => Instance.Offset;
        public static void Sleep(int milliseconds) => Instance.Sleep(milliseconds);
        
        public static Task SleepAsync(int milliseconds, CancellationToken cancellationToken = default)
            => Instance.SleepAsync(milliseconds, cancellationToken);

        public static void Sleep(TimeSpan delay)
            => Instance.Sleep(delay);
        
        public static Task SleepAsync(TimeSpan delay, CancellationToken cancellationToken = default)
            => Instance.SleepAsync(delay, cancellationToken);
        
        public static Task SleepSafeAsync(int milliseconds, CancellationToken cancellationToken = default) {
            return Instance.SleepSafeAsync(milliseconds, cancellationToken);
        }
        
        public static Task SleepSafeAsync(TimeSpan delay, CancellationToken cancellationToken = default)
            => Instance.SleepSafeAsync(delay, cancellationToken);
        
        public static void Schedule(Func<Task> action, TimeSpan dueTime)
            => Instance.Schedule(action, dueTime);
        
        public static void Schedule(Action action, TimeSpan dueTime)
            => Instance.Schedule(action, dueTime);
        
        public static void Schedule(Func<Task> action, DateTime executeAt)
            => Instance.Schedule(action, executeAt);
        
        public static void Schedule(Action action, DateTime executeAt)
            => Instance.Schedule(action, executeAt);
    }
 
    public static class SystemClockExtensions {
        public static void Sleep(this ISystemClock clock, TimeSpan delay)
            => clock.Sleep((int)delay.TotalMilliseconds);
        
        public static Task SleepAsync(this ISystemClock clock, TimeSpan delay, CancellationToken cancellationToken = default)
            => clock.SleepAsync((int)delay.TotalMilliseconds, cancellationToken);
        
        public static async Task SleepSafeAsync(this ISystemClock clock, int milliseconds, CancellationToken cancellationToken = default) {
            try {
                await clock.SleepAsync(milliseconds, cancellationToken).AnyContext();
            } catch (OperationCanceledException) {}
        }
        
        public static Task SleepSafeAsync(this ISystemClock clock, TimeSpan dueTime, CancellationToken cancellationToken = default)
            => clock.SleepSafeAsync((int)dueTime.TotalMilliseconds, cancellationToken);
        
        public static void Schedule(this ISystemClock clock, Action action, DateTime executeAt) =>
            clock.Schedule(action, executeAt.Subtract(clock.UtcNow));
        
        public static void Schedule(this ISystemClock clock, Func<Task> action, TimeSpan dueTime) =>
            clock.Schedule(() => { _ = action(); }, dueTime);

        public static void Schedule(this ISystemClock clock, Func<Task> action, DateTime executeAt) =>
            clock.Schedule(() => { _ = action(); }, executeAt);
        
        public static ITimer Timer(this ISystemClock clock, Func<Task> action, TimeSpan dueTime, TimeSpan period) =>
            clock.Timer(() => { _ = action(); }, dueTime, period);
    }
}