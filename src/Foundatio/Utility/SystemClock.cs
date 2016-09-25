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

        public DateTime Now() {
            return DateTime.Now;
        }

        public DateTime UtcNow() {
            return DateTime.UtcNow;
        }

        public DateTimeOffset OffsetNow() {
            return DateTimeOffset.Now;
        }

        public DateTimeOffset OffsetUtcNow() {
            return DateTimeOffset.UtcNow;
        }

        public void Sleep(int milliseconds) {
            Thread.Sleep(milliseconds);
        }

        public Task SleepAsync(int milliseconds, CancellationToken ct) {
            return Task.Delay(milliseconds, ct);
        }

        public TimeSpan TimeZoneOffset() {
            return DateTimeOffset.Now.Offset;
        }
    }

    public class TestSystemClock : ISystemClock {
        private DateTime? _fixedUtc = null;
        private TimeSpan _offset = TimeSpan.Zero;
        private TimeSpan _timeZoneOffset = DateTimeOffset.Now.Offset;
        private bool _fakeSleep = false;

        public DateTime UtcNow() {
            return _fixedUtc ?? DateTime.UtcNow.Add(_offset);
        }

        public DateTime Now() {
            return UtcNow().Add(_timeZoneOffset);
        }

        public DateTimeOffset OffsetNow() {
            return new DateTimeOffset(Now().Ticks, _timeZoneOffset);
        }

        public DateTimeOffset OffsetUtcNow() {
            return new DateTimeOffset(UtcNow().Ticks, TimeSpan.Zero);
        }

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

        public TimeSpan TimeZoneOffset() {
            return _timeZoneOffset;
        }

        public void SetFixedTime(DateTime time) {
            if (time.Kind == DateTimeKind.Unspecified)
                time = time.ToUniversalTime();

            if (time.Kind == DateTimeKind.Utc) {
                _fixedUtc = time;
            } else if (time.Kind == DateTimeKind.Local) {
                _fixedUtc = time.Add(TimeZoneOffset());
            }
        }

        public void SetTime(DateTime time) {
            _fixedUtc = null;

            if (time.Kind == DateTimeKind.Unspecified)
                time = time.ToUniversalTime();

            if (time.Kind == DateTimeKind.Utc) {
                _offset = DateTime.UtcNow.Subtract(time);
            } else if (time.Kind == DateTimeKind.Local) {
                _offset = DateTime.Now.Subtract(time);
            }
        }

        public void SetTimeZoneOffset(TimeSpan offset) {
            _timeZoneOffset = offset;
        }

        public void AddTime(TimeSpan amount) {
            _offset = _offset.Subtract(amount);
        }

        public void SubtractTime(TimeSpan amount) {
            _offset = _offset.Add(amount);
        }

        public void UseFakeSleep() {
            _fakeSleep = true;
        }

        public void UseRealSleep() {
            _fakeSleep = false;
        }
    }

    public static class SystemClock {
        public static ISystemClock Instance { get; set; } = DefaultSystemClock.Instance;
        public static TestSystemClock Test {
            get {
                var testClock = Instance as TestSystemClock;
                if (testClock == null)
                    throw new Exception("You must set SystemClock.Instance to ");

                return testClock;
            }
        }

        public static DateTime Now => Instance.Now();
        public static DateTime UtcNow => Instance.UtcNow();
        public static DateTimeOffset OffsetNow => Instance.OffsetNow();
        public static DateTimeOffset OffsetUtcNow => Instance.OffsetUtcNow();
        public static TimeSpan TimeZoneOffset => Instance.TimeZoneOffset();

        public static void Sleep(TimeSpan time) {
            Instance.Sleep((int)time.TotalMilliseconds);
        }

        public static void Sleep(int milliseconds) {
            Instance.Sleep(milliseconds);
        }

        public static Task SleepAsync(TimeSpan time, CancellationToken cancellationToken = default(CancellationToken)) {
            return Instance.SleepAsync((int)time.TotalMilliseconds, cancellationToken);
        }

        public static Task SleepAsync(int milliseconds, CancellationToken cancellationToken = default(CancellationToken)) {
            return Instance.SleepAsync(milliseconds, cancellationToken);
        }

        public static void UseTestClock() {
            Instance = new TestSystemClock();
        }
    }
}
