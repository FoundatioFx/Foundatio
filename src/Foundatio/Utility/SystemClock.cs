using System;
using System.Reactive.Concurrency;
using System.Threading;
using System.Threading.Tasks;
using Nito.AsyncEx.Synchronous;

namespace Foundatio.Utility {
    public interface ISystemClock : IScheduler
    {
        CancellationTokenSource CreateCancellationTokenSource(TimeSpan timeout);
    }

    public abstract class SchedulerSystemClockBase : ISystemClock
    {
        private readonly IScheduler _scheduler;

        protected SchedulerSystemClockBase(IScheduler scheduler)
        {
            _scheduler = scheduler;
        }

        public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
        {
            return _scheduler.Schedule(state, action);
        }

        public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        {
            return _scheduler.Schedule(state, dueTime, action);
        }

        public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        {
            return _scheduler.Schedule(state, dueTime, action);
        }

        public abstract CancellationTokenSource CreateCancellationTokenSource(TimeSpan timeout);

        public DateTimeOffset Now => _scheduler.Now;
    }

    public sealed class DefaultSystemClock : SchedulerSystemClockBase {
        public static readonly DefaultSystemClock Instance = new DefaultSystemClock();

        public DefaultSystemClock() : base(DefaultScheduler.Instance)
        {
        }

        public override CancellationTokenSource CreateCancellationTokenSource(TimeSpan timeout)
        {
            return new CancellationTokenSource(timeout);
        }
    }

    public static class SystemClock {
        public static ISystemClock Instance { get; set; } = DefaultSystemClock.Instance;

        public static DateTime UtcNow => Instance.Now.UtcDateTime;

        public static void Sleep(int milliseconds) {
            SleepAsync(TimeSpan.FromMilliseconds(milliseconds)).WaitAndUnwrapException();
        }

        public static async Task SleepAsync(TimeSpan time, CancellationToken cancellationToken = default(CancellationToken)) {
            await Instance.Sleep(time, cancellationToken).ConfigureAwait(false);
        }

        public static Task SleepAsync(int milliseconds, CancellationToken cancellationToken = default(CancellationToken)) {
            return SleepAsync(TimeSpan.FromMilliseconds(milliseconds), cancellationToken);
        }

        public static CancellationTokenSource CreateCancellationTokenSource(TimeSpan timeout)
        {
            return Instance.CreateCancellationTokenSource(timeout);
        }
    }
}
