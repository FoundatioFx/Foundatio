using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Foundatio.Utility {
    public interface ISystemClock {
        DateTime Now();
        DateTime UtcNow();
        DateTimeOffset OffsetNow();
        DateTimeOffset OffsetUtcNow();
        void Sleep(int milliseconds);
        Task SleepAsync(int milliseconds, CancellationToken ct);
        TimeSpan TimeZoneOffset();
        void ScheduleWork(Func<Task> action, TimeSpan delay, TimeSpan? interval = null);
        void ScheduleWork(Action action, TimeSpan delay, TimeSpan? interval = null);
        void ScheduleWork(Func<Task> action, DateTime executeAt, TimeSpan? interval = null);
        void ScheduleWork(Action action, DateTime executeAt, TimeSpan? interval = null);
    }

    public class RealSystemClock : ISystemClock {
        public static readonly RealSystemClock Instance = new();

        public DateTime Now() => DateTime.Now;
        public DateTime UtcNow() => DateTime.UtcNow;
        public DateTimeOffset OffsetNow() => DateTimeOffset.Now;
        public DateTimeOffset OffsetUtcNow() => DateTimeOffset.UtcNow;
        public void Sleep(int milliseconds) => Thread.Sleep(milliseconds);
        public Task SleepAsync(int milliseconds, CancellationToken ct) => Task.Delay(milliseconds, ct);
        public TimeSpan TimeZoneOffset() => DateTimeOffset.Now.Offset;
        public void ScheduleWork(Func<Task> action, TimeSpan delay, TimeSpan? interval = null)
            => WorkScheduler.Default.Schedule(action, delay, interval);
        public void ScheduleWork(Action action, TimeSpan delay, TimeSpan? interval = null)
            => WorkScheduler.Default.Schedule(action, delay, interval);
        public void ScheduleWork(Func<Task> action, DateTime executeAt, TimeSpan? interval = null)
            => WorkScheduler.Default.Schedule(action, executeAt, interval);
        public void ScheduleWork(Action action, DateTime executeAt, TimeSpan? interval = null)
            => WorkScheduler.Default.Schedule(action, executeAt, interval);
    }

    internal class TestSystemClockImpl : ISystemClock, IDisposable {
        private DateTime? _fixedUtc = null;
        private TimeSpan _offset = TimeSpan.Zero;
        private TimeSpan _timeZoneOffset = DateTimeOffset.Now.Offset;
        private bool _fakeSleep = false;
        private ISystemClock _originalClock;
        
        public TestSystemClockImpl() {}

        public TestSystemClockImpl(ISystemClock originalTime) {
            _originalClock = originalTime;
        }

        public DateTime UtcNow() => (_fixedUtc ?? DateTime.UtcNow).Add(_offset);
        public DateTime Now() => new(UtcNow().Ticks + TimeZoneOffset().Ticks, DateTimeKind.Local);
        public DateTimeOffset OffsetNow() => new(UtcNow().Ticks + TimeZoneOffset().Ticks, TimeZoneOffset());
        public DateTimeOffset OffsetUtcNow() => new(UtcNow().Ticks, TimeSpan.Zero);
        public TimeSpan TimeZoneOffset() => _timeZoneOffset;
        public void ScheduleWork(Action action, TimeSpan delay, TimeSpan? interval = null)
            => WorkScheduler.Default.Schedule(action, delay, interval);
        public void ScheduleWork(Func<Task> action, TimeSpan delay, TimeSpan? interval = null)
            => WorkScheduler.Default.Schedule(action, delay, interval);
        public void ScheduleWork(Action action, DateTime executeAtUtc, TimeSpan? interval = null)
            => WorkScheduler.Default.Schedule(action, executeAtUtc, interval);
        public void ScheduleWork(Func<Task> action, DateTime executeAtUtc, TimeSpan? interval = null)
            => WorkScheduler.Default.Schedule(action, executeAtUtc, interval);

        public void SetTimeZoneOffset(TimeSpan offset) {
            _timeZoneOffset = offset;
            OnChanged();
        }

        public void AddTime(TimeSpan amount) {
            _offset = _offset.Add(amount);
            OnChanged();
        }

        public void SubtractTime(TimeSpan amount) {
            _offset = _offset.Subtract(amount);
            OnChanged();
        }

        public void UseFakeSleep() => _fakeSleep = true;
        public void UseRealSleep() => _fakeSleep = false;
        
        public void Sleep(int milliseconds) {
            if (!_fakeSleep) {
                Thread.Sleep(milliseconds);
                return;
            }

            AddTime(TimeSpan.FromMilliseconds(milliseconds));
            Thread.Sleep(1);
        }

        public Task SleepAsync(int milliseconds, CancellationToken ct) {
            if (!_fakeSleep)
                return Task.Delay(milliseconds, ct);

            Sleep(milliseconds);
            return Task.CompletedTask;
        }

        public DateTime Freeze() {
            var now = Now();
            SetFrozenTime(now);
            return now;
        }

        public void Unfreeze() {
            SetTime(Now());
        }

        public void SetFrozenTime(DateTime time) {
            UseFakeSleep();
            SetTime(time, true);
        }

        public void SetTime(DateTime time, bool freeze = false) {
            var now = DateTime.Now;
            if (freeze) {
                if (time.Kind == DateTimeKind.Unspecified)
                    time = time.ToUniversalTime();

                if (time.Kind == DateTimeKind.Utc) {
                    _fixedUtc = time;
                    OnChanged();
                } else if (time.Kind == DateTimeKind.Local) {
                    _fixedUtc = new DateTime(time.Ticks - TimeZoneOffset().Ticks, DateTimeKind.Utc);
                    OnChanged();
                }
            } else {
                _fixedUtc = null;

                if (time.Kind == DateTimeKind.Unspecified)
                    time = time.ToUniversalTime();

                if (time.Kind == DateTimeKind.Utc) {
                    _offset = now.ToUniversalTime().Subtract(time);
                    OnChanged();
                } else if (time.Kind == DateTimeKind.Local) {
                    _offset = now.Subtract(time);
                    OnChanged();
                }
            }
        }

        public event EventHandler Changed;

        private void OnChanged() {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        
        public void Dispose() {
            if (_originalClock == null)
                return;
            
            var originalClock = Interlocked.Exchange(ref _originalClock, null);
            if (originalClock != null)
                SystemClock.Instance = originalClock;
        }
        
        public static TestSystemClockImpl Instance {
            get {
                if (!(SystemClock.Instance is TestSystemClockImpl testClock))
                    throw new ArgumentException("You must first install TestSystemClock using TestSystemClock.Install");

                return testClock;
            }
        }
    }

    public class TestSystemClock {
        public static void SetTimeZoneOffset(TimeSpan offset) => TestSystemClockImpl.Instance.SetTimeZoneOffset(offset);
        public static void AddTime(TimeSpan amount) => TestSystemClockImpl.Instance.AddTime(amount);
        public static void SubtractTime(TimeSpan amount) => TestSystemClockImpl.Instance.SubtractTime(amount);
        public static void UseFakeSleep() => TestSystemClockImpl.Instance.UseFakeSleep();
        public static void UseRealSleep() => TestSystemClockImpl.Instance.UseRealSleep();
        public static DateTime Freeze() => TestSystemClockImpl.Instance.Freeze();
        public static void Unfreeze() => TestSystemClockImpl.Instance.Unfreeze();
        public static void SetFrozenTime(DateTime time) => TestSystemClockImpl.Instance.SetFrozenTime(time);
        public static void SetTime(DateTime time, bool freeze = false) => TestSystemClockImpl.Instance.SetTime(time, freeze);

        public static event EventHandler Changed {
            add => TestSystemClockImpl.Instance.Changed += value;
            remove => TestSystemClockImpl.Instance.Changed -= value;
        }

        public static IDisposable Install() {
            return Install(true, null);
        }

        public static IDisposable Install(ILoggerFactory loggerFactory) {
            return Install(true, loggerFactory);
        }

        public static IDisposable Install(bool freeze, ILoggerFactory loggerFactory) {
            var testClock = new TestSystemClockImpl(SystemClock.Instance);
            SystemClock.Instance = testClock;
            if (freeze)
                testClock.Freeze();
            
            if (loggerFactory != null)
                WorkScheduler.Default.SetLogger(loggerFactory);
            
            return testClock;
        }
    }
    
    public static class SystemClock {
        private static AsyncLocal<ISystemClock> _instance;
        
        public static ISystemClock Instance {
            get => _instance?.Value ?? RealSystemClock.Instance;
            set {
                if (_instance == null)
                    _instance = new AsyncLocal<ISystemClock>();
                
                _instance.Value = value;
            }
        }

        public static DateTime Now => Instance.Now();
        public static DateTime UtcNow => Instance.UtcNow();
        public static DateTimeOffset OffsetNow => Instance.OffsetNow();
        public static DateTimeOffset OffsetUtcNow => Instance.OffsetUtcNow();
        public static TimeSpan TimeZoneOffset => Instance.TimeZoneOffset();
        public static void Sleep(int milliseconds) => Instance.Sleep(milliseconds);
        public static Task SleepAsync(int milliseconds, CancellationToken cancellationToken = default)
            => Instance.SleepAsync(milliseconds, cancellationToken);
        public static void ScheduleWork(Action action, TimeSpan delay, TimeSpan? interval = null)
            => Instance.ScheduleWork(action, delay, interval);
        public static void ScheduleWork(Func<Task> action, TimeSpan delay, TimeSpan? interval = null)
            => Instance.ScheduleWork(action, delay, interval);
        public static void ScheduleWork(Action action, DateTime executeAt, TimeSpan? interval = null)
            => Instance.ScheduleWork(action, executeAt, interval);
        public static void ScheduleWork(Func<Task> action, DateTime executeAt, TimeSpan? interval = null)
            => Instance.ScheduleWork(action, executeAt, interval);

        #region Extensions

        public static void Sleep(TimeSpan delay)
            => Instance.Sleep(delay);
        
        public static Task SleepAsync(TimeSpan delay, CancellationToken cancellationToken = default)
            => Instance.SleepAsync(delay, cancellationToken);
        
        public static Task SleepSafeAsync(int milliseconds, CancellationToken cancellationToken = default) {
            return Instance.SleepSafeAsync(milliseconds, cancellationToken);
        }
        
        public static Task SleepSafeAsync(TimeSpan delay, CancellationToken cancellationToken = default)
            => Instance.SleepSafeAsync(delay, cancellationToken);
        
        #endregion
    }
 
    public static class TimeExtensions {
        public static void Sleep(this ISystemClock time, TimeSpan delay)
            => time.Sleep((int)delay.TotalMilliseconds);
        
        public static Task SleepAsync(this ISystemClock time, TimeSpan delay, CancellationToken cancellationToken = default)
            => time.SleepAsync((int)delay.TotalMilliseconds, cancellationToken);
        
        public static async Task SleepSafeAsync(this ISystemClock time, int milliseconds, CancellationToken cancellationToken = default) {
            try {
                await time.SleepAsync(milliseconds, cancellationToken).AnyContext();
            } catch (OperationCanceledException) {}
        }
        
        public static Task SleepSafeAsync(this ISystemClock time, TimeSpan delay, CancellationToken cancellationToken = default)
            => time.SleepSafeAsync((int)delay.TotalMilliseconds, cancellationToken);
    }
}