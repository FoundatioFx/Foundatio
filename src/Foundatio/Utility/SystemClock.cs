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
        private static readonly AsyncLocal<ISystemClock> _instance = new AsyncLocal<ISystemClock>();

        public static ISystemClock Instance {
            get { return _instance.Value ?? DefaultSystemClock.Instance; }
            private set {
                if (value == DefaultSystemClock.Instance)
                    _instance.Value = null;
                else
                    _instance.Value = value;
            }
        }

        public static IDisposable SetInstance(ISystemClock instance) {
            return new SwapSystemClock(instance);
        }

        public static DateTime Now => Instance.Now.LocalDateTime;
        public static DateTime UtcNow => Instance.Now.UtcDateTime;
        public static DateTimeOffset OffsetNow => Instance.Now.ToLocalTime();
        public static DateTimeOffset OffsetUtcNow => Instance.Now.ToUniversalTime();

        public static void Sleep(TimeSpan time, CancellationToken cancellationToken = default(CancellationToken)) {
            // ReSharper disable once MethodSupportsCancellation
            SleepAsync(time, cancellationToken).WaitAndUnwrapException();
        }

        public static void Sleep(int milliseconds, CancellationToken cancellationToken = default(CancellationToken)) {
            Sleep(TimeSpan.FromMilliseconds(milliseconds), cancellationToken);
        }

        public static Task SleepAsync(int milliseconds, CancellationToken cancellationToken = default(CancellationToken))
        {
            return SleepAsync(TimeSpan.FromMilliseconds(milliseconds), cancellationToken);
        }

        public static async Task SleepAsync(TimeSpan time, CancellationToken cancellationToken = default(CancellationToken)) {
            await Instance.Sleep(time, cancellationToken).ConfigureAwait(false);
        }

        public static CancellationTokenSource CreateCancellationTokenSource(TimeSpan timeout)
        {
            return Instance.CreateCancellationTokenSource(timeout);
        }

        private sealed class SwapSystemClock : IDisposable
        {
            private ISystemClock _originalInstance;

            public SwapSystemClock(ISystemClock replacementInstance)
            {
                _originalInstance = SystemClock.Instance;
                SystemClock.Instance = replacementInstance;
            }

            public void Dispose()
            {
                var originalInstance = Interlocked.Exchange(ref _originalInstance, null);
                if (originalInstance != null)
                    SystemClock.Instance = originalInstance;
            }
        }
    }
}
