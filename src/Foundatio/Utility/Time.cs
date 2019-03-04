using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Utility {
    public interface ITime {
        DateTime Now();
        DateTime UtcNow();
        DateTimeOffset OffsetNow();
        DateTimeOffset OffsetUtcNow();
        TimeSpan TimeZoneOffset();
        void Delay(int milliseconds);
        Task DelayAsync(int milliseconds, CancellationToken ct);
    }

    public class RealTime : ITime {
        public static readonly RealTime Instance = new RealTime();

        public DateTime Now() => DateTime.Now;
        public DateTime UtcNow() => DateTime.UtcNow;
        public DateTimeOffset OffsetNow() => DateTimeOffset.Now;
        public DateTimeOffset OffsetUtcNow() => DateTimeOffset.UtcNow;
        public TimeSpan TimeZoneOffset() => DateTimeOffset.Now.Offset;
        public void Delay(int milliseconds) => Thread.Sleep(milliseconds);
        public Task DelayAsync(int milliseconds, CancellationToken ct) => Task.Delay(milliseconds, ct);
    }

    public class TestTime : ITime, IDisposable {
        private DateTime? _fixedUtc = null;
        private TimeSpan _offset = TimeSpan.Zero;
        private TimeSpan _timeZoneOffset = DateTimeOffset.Now.Offset;
        private bool _fakeSleep = false;
        
        public TestTime() {}

        public TestTime(ITime originalTime) {
            _originalTime = originalTime;
        }

        public DateTime UtcNow() => (_fixedUtc ?? DateTime.UtcNow).Add(_offset);
        public DateTime Now() => new DateTime(UtcNow().Ticks + TimeZoneOffset().Ticks, DateTimeKind.Local);
        public DateTimeOffset OffsetNow() => new DateTimeOffset(UtcNow().Ticks + TimeZoneOffset().Ticks, TimeZoneOffset());
        public DateTimeOffset OffsetUtcNow() => new DateTimeOffset(UtcNow().Ticks, TimeSpan.Zero);
        public void SetTimeZoneOffset(TimeSpan offset) => _timeZoneOffset = offset;
        public void AddTime(TimeSpan amount) => _offset = _offset.Add(amount);
        public void SubtractTime(TimeSpan amount) => _offset = _offset.Subtract(amount);
        public void UseFakeSleep() => _fakeSleep = true;
        public void UseRealSleep() => _fakeSleep = false;

        public void Delay(int milliseconds) {
            if (!_fakeSleep) {
                Thread.Sleep(milliseconds);
                return;
            }

            AddTime(TimeSpan.FromMilliseconds(milliseconds));
            Thread.Sleep(1);
        }

        public Task DelayAsync(int milliseconds, CancellationToken ct) {
            if (!_fakeSleep)
                return Task.Delay(milliseconds, ct);

            Delay(milliseconds);
            return Task.CompletedTask;
        }

        public TimeSpan TimeZoneOffset() => _timeZoneOffset;

        public void Freeze() {
            SetFrozenTime(Now());
        }

        public void Unfreeze() {
            SetTime(Now());
        }

        public void SetFrozenTime(DateTime time) {
            SetTime(time, true);
        }

        public void SetTime(DateTime time, bool freeze = false) {
            var now = DateTime.Now;
            if (freeze) {
                if (time.Kind == DateTimeKind.Unspecified)
                    time = time.ToUniversalTime();

                if (time.Kind == DateTimeKind.Utc) {
                    _fixedUtc = time;
                } else if (time.Kind == DateTimeKind.Local) {
                    _fixedUtc = new DateTime(time.Ticks - TimeZoneOffset().Ticks, DateTimeKind.Utc);
                }
            } else {
                _fixedUtc = null;

                if (time.Kind == DateTimeKind.Unspecified)
                    time = time.ToUniversalTime();

                if (time.Kind == DateTimeKind.Utc) {
                    _offset = now.ToUniversalTime().Subtract(time);
                } else if (time.Kind == DateTimeKind.Local) {
                    _offset = now.Subtract(time);
                }
            }
        }

        private ITime _originalTime;
        
        public void Dispose() {
            if (_originalTime == null)
                return;
            
            var originalTime = Interlocked.Exchange(ref _originalTime, null);
            if (originalTime != null)
                Time.Instance = originalTime;
        }
        
        public static TestTime Instance {
            get {
                if (!(Time.Instance is TestTime testTime))
                    throw new ArgumentException("You must first install TestTime using TestTime.Install");

                return testTime;
            }
        }
    }

    public static class Time {
        private static AsyncLocal<ITime> _instance;
        
        public static ITime Instance {
            get => _instance?.Value ?? RealTime.Instance;
            set {
                if (_instance == null)
                    _instance = new AsyncLocal<ITime>();
                
                _instance.Value = value;
            }
        }
        
        public static TestTime UseTestTime() {
            var testTime = new TestTime(Instance);
            Instance = testTime;
            
            return testTime;
        }

        public static DateTime Now => Instance.Now();
        public static DateTime UtcNow => Instance.UtcNow();
        public static DateTimeOffset OffsetNow => Instance.OffsetNow();
        public static DateTimeOffset OffsetUtcNow => Instance.OffsetUtcNow();
        public static TimeSpan TimeZoneOffset => Instance.TimeZoneOffset();
        public static void Delay(int milliseconds) => Instance.Delay(milliseconds);
        public static Task DelayAsync(int milliseconds, CancellationToken cancellationToken = default)
            => Instance.DelayAsync(milliseconds, cancellationToken);
        
        #region Extensions
        
        public static void Delay(TimeSpan delay)
            => Instance.Delay(delay);
        
        public static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
            => Instance.DelayAsync(delay, cancellationToken);
        
        public static Task SafeDelayAsync(int milliseconds, CancellationToken cancellationToken = default) {
            return Instance.SafeDelayAsync(milliseconds, cancellationToken);
        }
        
        public static Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
            => Instance.SafeDelayAsync(delay, cancellationToken);
        
        #endregion
    }
    
    [Obsolete("Use Time class instead")]
    public static class SystemClock {
        public static ITime Instance {
            get => Time.Instance;
            set => Time.Instance = value;
        }

        public static TestTime Test => TestTime.Instance;
        
        public static DateTime Now => Instance.Now();
        public static DateTime UtcNow => Instance.UtcNow();
        public static DateTimeOffset OffsetNow => Instance.OffsetNow();
        public static DateTimeOffset OffsetUtcNow => Instance.OffsetUtcNow();
        public static TimeSpan TimeZoneOffset => Instance.TimeZoneOffset();
        public static void Sleep(int milliseconds) => Instance.Delay(milliseconds);
        public static Task SleepAsync(int milliseconds, CancellationToken cancellationToken = default)
            => Instance.DelayAsync(milliseconds, cancellationToken);

        #region Extensions

        public static void Sleep(TimeSpan time) => Instance.Delay(time);
        public static Task SleepAsync(TimeSpan time, CancellationToken cancellationToken = default)
            => Instance.DelayAsync(time, cancellationToken);
        
        public static Task SleepSafeAsync(TimeSpan time, CancellationToken cancellationToken = default)
            => Instance.SafeDelayAsync(time, cancellationToken);

        public static Task SleepSafeAsync(int milliseconds, CancellationToken cancellationToken = default)
            => Instance.DelayAsync(milliseconds, cancellationToken);
        
        #endregion
    }

    public static class TimeExtensions {
        public static void Delay(this ITime time, TimeSpan delay)
            => time.Delay((int)delay.TotalMilliseconds);
        
        public static Task DelayAsync(this ITime time, TimeSpan delay, CancellationToken cancellationToken = default)
            => time.DelayAsync((int)delay.TotalMilliseconds, cancellationToken);
        
        public static async Task SafeDelayAsync(this ITime time, int milliseconds, CancellationToken cancellationToken = default) {
            try {
                await time.DelayAsync(milliseconds, cancellationToken).AnyContext();
            } catch (OperationCanceledException) {}
        }
        
        public static Task SafeDelayAsync(this ITime time, TimeSpan delay, CancellationToken cancellationToken = default)
            => time.SafeDelayAsync((int)delay.TotalMilliseconds, cancellationToken);
    }
}