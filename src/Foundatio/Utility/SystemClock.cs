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
    }

    public class DefaultSystemClock : ISystemClock {
        public static readonly DefaultSystemClock Instance = new DefaultSystemClock();

        public DateTime Now() => DateTime.Now;
        public DateTime UtcNow() => DateTime.UtcNow;
        public DateTimeOffset OffsetNow() => DateTimeOffset.Now;
        public DateTimeOffset OffsetUtcNow() => DateTimeOffset.UtcNow;
        public void Sleep(int milliseconds) => Thread.Sleep(milliseconds);
        public Task SleepAsync(int milliseconds, CancellationToken ct) => Task.Delay(milliseconds, ct);
        public TimeSpan TimeZoneOffset() => DateTimeOffset.Now.Offset;
    }

    public class TestSystemClock : ISystemClock {
        private DateTime? _fixedUtc = null;
        private TimeSpan _offset = TimeSpan.Zero;
        private TimeSpan _timeZoneOffset = DateTimeOffset.Now.Offset;
        private bool _fakeSleep = false;

        public DateTime UtcNow() => _fixedUtc ?? DateTime.UtcNow.Add(_offset);
        public DateTime Now() => new DateTime(UtcNow().Ticks + TimeZoneOffset().Ticks, DateTimeKind.Local);
        public DateTimeOffset OffsetNow() => new DateTimeOffset(UtcNow().Ticks + TimeZoneOffset().Ticks, TimeZoneOffset());
        public DateTimeOffset OffsetUtcNow() => new DateTimeOffset(UtcNow().Ticks, TimeSpan.Zero);
        public void SetTimeZoneOffset(TimeSpan offset) => _timeZoneOffset = offset;
        public void AddTime(TimeSpan amount) => _offset = _offset.Add(amount);
        public void SubtractTime(TimeSpan amount) => _offset = _offset.Subtract(amount);
        public void UseFakeSleep() => _fakeSleep = true;
        public void UseRealSleep() => _fakeSleep = false;
        public static IDisposable Install() => new SwapSystemClock(new TestSystemClock());

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

        public TimeSpan TimeZoneOffset() => _timeZoneOffset;

        public void SetFixedTime(DateTime time) {
            if (time.Kind == DateTimeKind.Unspecified)
                time = time.ToUniversalTime();

            if (time.Kind == DateTimeKind.Utc) {
                _fixedUtc = time;
            } else if (time.Kind == DateTimeKind.Local) {
                _fixedUtc = new DateTime(time.Ticks - TimeZoneOffset().Ticks, DateTimeKind.Utc);
            }
        }

        public void SetTime(DateTime time) {
            var now = DateTime.Now;
            _fixedUtc = null;

            if (time.Kind == DateTimeKind.Unspecified)
                time = time.ToUniversalTime();

            if (time.Kind == DateTimeKind.Utc) {
                _offset = now.ToUniversalTime().Subtract(time);
            } else if (time.Kind == DateTimeKind.Local) {
                _offset = now.Subtract(time);
            }
        }

        private sealed class SwapSystemClock : IDisposable {
            private ISystemClock _originalInstance;

            public SwapSystemClock(ISystemClock replacementInstance) {
                _originalInstance = SystemClock.Instance;
                SystemClock.Instance = replacementInstance;
            }

            public void Dispose() {
                var originalInstance = Interlocked.Exchange(ref _originalInstance, null);
                if (originalInstance != null)
                    SystemClock.Instance = originalInstance;
            }
        }
    }

    public static class SystemClock {
        private static ISystemClock _instance = DefaultSystemClock.Instance;
        public static ISystemClock Instance {
            get => _instance ?? DefaultSystemClock.Instance;
            set => _instance = value;
        }
        
        public static TestSystemClock Test {
            get {
                var testClock = Instance as TestSystemClock;
                if (testClock == null)
                    throw new ArgumentException("You must set SystemClock.Instance to TestSystemClock");

                return testClock;
            }
        }

        public static DateTime Now => Instance.Now();
        public static DateTime UtcNow => Instance.UtcNow();
        public static DateTimeOffset OffsetNow => Instance.OffsetNow();
        public static DateTimeOffset OffsetUtcNow => Instance.OffsetUtcNow();
        public static TimeSpan TimeZoneOffset => Instance.TimeZoneOffset();
        public static void Sleep(TimeSpan time) => Instance.Sleep((int)time.TotalMilliseconds);
        public static void Sleep(int milliseconds) => Instance.Sleep(milliseconds);
        public static Task SleepAsync(TimeSpan time, CancellationToken cancellationToken = default(CancellationToken)) => Instance.SleepAsync((int)time.TotalMilliseconds, cancellationToken);
        public static Task SleepAsync(int milliseconds, CancellationToken cancellationToken = default(CancellationToken)) => Instance.SleepAsync(milliseconds, cancellationToken);
    }
}