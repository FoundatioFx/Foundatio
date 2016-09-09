using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Utility {
    public static class SystemClock {
        public static Action<int> SleepFunc = Thread.Sleep;
        public static Func<int, CancellationToken, Task> SleepFuncAsync = Task.Delay;
        public static Func<DateTime> UtcNowFunc = () => DateTime.UtcNow;
        public static Func<DateTime> NowFunc = () => DateTime.Now;
        public static Func<DateTimeOffset> OffsetUtcNowFunc = () => DateTimeOffset.UtcNow;
        public static Func<DateTimeOffset> OffsetNowFunc = () => DateTimeOffset.Now;
        public static Func<TimeSpan> TimeZoneOffsetFunc = () => DateTimeOffset.Now.Offset;

        public static DateTime UtcNow => UtcNowFunc();
        public static DateTime Now => NowFunc();
        public static DateTimeOffset OffsetUtcNow => OffsetUtcNowFunc();
        public static DateTimeOffset OffsetNow => OffsetNowFunc();
        public static TimeSpan TimeZoneOffset => TimeZoneOffsetFunc();

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

        public static void SetFixedTime(DateTime time) {
            if (time.Kind == DateTimeKind.Unspecified)
                time = time.ToUniversalTime();

            if (time.Kind == DateTimeKind.Utc) {
                UtcNowFunc = () => time;
                OffsetUtcNowFunc = () => new DateTimeOffset(time, TimeSpan.Zero);

                NowFunc = () => {
                    var now = UtcNowFunc().Add(TimeZoneOffsetFunc());
                    return new DateTime(now.Ticks, DateTimeKind.Local);
                };
                OffsetNowFunc = () => {
                    var now = UtcNowFunc().Add(TimeZoneOffsetFunc());
                    return new DateTimeOffset(now.Ticks, TimeZoneOffsetFunc());
                };
            } else if (time.Kind == DateTimeKind.Local) {
                NowFunc = () => time;
                OffsetNowFunc = () => new DateTimeOffset(time, TimeZoneOffsetFunc());

                UtcNowFunc = () => {
                    var now = NowFunc().Subtract(TimeZoneOffsetFunc());
                    return new DateTime(now.Ticks, DateTimeKind.Local);
                };
                OffsetUtcNowFunc = () => {
                    var now = NowFunc().Subtract(TimeZoneOffsetFunc());
                    return new DateTimeOffset(now.Ticks, TimeSpan.Zero);
                };
            }
        }

        public static void SetTimeZoneOffset(TimeSpan offset) {
            TimeZoneOffsetFunc = () => offset;
            NowFunc = () => UtcNowFunc().Add(offset);
            OffsetNowFunc = () => new DateTimeOffset(UtcNowFunc().Add(offset).Ticks, offset);
        }

        public static void SetTime(DateTime time) {
            if (time.Kind == DateTimeKind.Unspecified)
                time = time.ToUniversalTime();

            TimeSpan adjustment = TimeSpan.Zero;
            if (time.Kind == DateTimeKind.Utc)
                adjustment = DateTime.UtcNow.Subtract(time);
            else if (time.Kind == DateTimeKind.Local)
                adjustment = DateTime.Now.Subtract(time);

            AdjustTime(adjustment);
        }

        public static void AdjustTime(TimeSpan adjustment) {
            UtcNowFunc = () => DateTime.UtcNow.Subtract(adjustment);
            OffsetUtcNowFunc = () => new DateTimeOffset(DateTimeOffset.UtcNow.Subtract(adjustment).Ticks, TimeSpan.Zero);

            NowFunc = () => {
                var now = UtcNowFunc().Add(TimeZoneOffsetFunc());
                return new DateTime(now.Ticks, DateTimeKind.Local);
            };
            OffsetNowFunc = () => {
                var now = UtcNowFunc().Add(TimeZoneOffsetFunc());
                return new DateTimeOffset(now.Ticks, TimeZoneOffsetFunc());
            };
        }

        public static void UseFakeSleep() {
            SleepFunc = delay => AdjustTime(TimeSpan.FromMilliseconds(-delay));
            SleepFuncAsync = (delay, ct) => {
                AdjustTime(TimeSpan.FromMilliseconds(-delay));
                return Task.CompletedTask;
            };
        }

        public static void Reset() {
            SleepFunc = Thread.Sleep;
            SleepFuncAsync = Task.Delay;
            UtcNowFunc = () => DateTime.UtcNow;
            NowFunc = () => DateTime.Now;
            OffsetUtcNowFunc = () => DateTimeOffset.UtcNow;
            OffsetNowFunc = () => DateTimeOffset.Now;
            TimeZoneOffsetFunc = () => DateTimeOffset.Now.Offset;
        }
    }
}
